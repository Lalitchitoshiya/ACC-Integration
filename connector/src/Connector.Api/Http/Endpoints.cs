using System.Text.Json;
using Connector.Api.Acc;
using Connector.Api.Data;
using Connector.Api.Domain;
using Connector.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Connector.Api.Http;

// Phase 1 endpoints per specs/11-api-contracts.md: models CRUD (create/list/get),
// version upload, version list, latest-approved, download. Checkout/review are Phase 2.
public static class Endpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapConnectorEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        // ---- Models ----

        api.MapPost("/projects/{projectId:guid}/models", async (
            Guid projectId, CreateModelRequest body, ConnectorDbContext db,
            CurrentUserService current, CancellationToken ct) =>
        {
            var user = await current.GetUserAsync(ct);
            if (user is null) return ApiError.Unauthenticated();
            if (await current.GetRoleAsync(user.Id, projectId, ct) != ProjectRole.Admin)
                return ApiError.Forbidden("Only Admin can create models.");

            var project = await db.Projects.FindAsync([projectId], ct);
            if (project is null) return ApiError.NotFound($"Project {projectId} not found.");
            if (!Enum.TryParse<ToolType>(body.ToolType, ignoreCase: true, out var tool))
                return ApiError.ValidationFailed($"Unknown toolType '{body.ToolType}'. Phase 1 supports: InfoWorksWSPro.");
            if (await db.Models.AnyAsync(m => m.ProjectId == projectId && m.Name == body.Name, ct))
                return ApiError.Conflict($"A model named '{body.Name}' already exists in this project.");

            var model = new Model
            {
                Id = Guid.NewGuid(), ProjectId = projectId,
                Name = body.Name, ToolType = tool, AccFolderUrn = body.AccFolderUrn
            };
            db.Models.Add(model);
            await Audit(db, projectId, user, "model.created", model.Id, null, new { model.Name });
            await db.SaveChangesAsync(ct);
            return Results.Json(new { model = ModelDto(model) }, JsonOpts, statusCode: 201);
        });

        api.MapGet("/projects/{projectId:guid}/models", async (
            Guid projectId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var user = await current.GetUserAsync(ct);
            if (user is null) return ApiError.Unauthenticated();
            if (await current.GetRoleAsync(user.Id, projectId, ct) is null)
                return ApiError.Forbidden();

            var models = await db.Models.Where(m => m.ProjectId == projectId)
                .OrderBy(m => m.Name).ToListAsync(ct);
            return Results.Json(new { models = models.Select(ModelDto) }, JsonOpts);
        });

        api.MapGet("/models/{modelId:guid}", async (
            Guid modelId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (model, err) = await LoadModelForRead(modelId, db, current, ct);
            if (err is not null) return err;
            return Results.Json(new { model = ModelDto(model!) }, JsonOpts);
        });

        // ---- Upload (spec 01) ----

        api.MapPost("/models/{modelId:guid}/versions", async (
            Guid modelId, HttpRequest request, ConnectorDbContext db, CurrentUserService current,
            IAccClient acc, IEnumerable<IMetadataExtractor> extractors, CancellationToken ct) =>
        {
            var user = await current.GetUserAsync(ct);
            if (user is null) return ApiError.Unauthenticated();

            var model = await db.Models.FindAsync([modelId], ct);
            if (model is null)
                return ApiError.Conflict($"Model {modelId} does not exist — orphan uploads are rejected (FR1.5).");

            var role = await current.GetRoleAsync(user.Id, model.ProjectId, ct);
            if (role is null or ProjectRole.Viewer)
                return ApiError.Forbidden("Viewers cannot upload versions.");

            if (!request.HasFormContentType) return ApiError.ValidationFailed("Expected multipart/form-data.");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            var changeDescription = form["changeDescription"].FirstOrDefault()?.Trim();
            var sourceTool = form["sourceTool"].FirstOrDefault() ?? "";
            var sourceToolVersion = form["sourceToolVersion"].FirstOrDefault() ?? "";

            if (file is null || file.Length == 0) return ApiError.ValidationFailed("A non-empty 'file' part is required.");
            if (changeDescription is null || changeDescription.Length < 10)
                return ApiError.ValidationFailed("'changeDescription' is required (min 10 characters) — FR1.3.");
            if (string.IsNullOrWhiteSpace(sourceTool)) return ApiError.ValidationFailed("'sourceTool' is required — FR1.3.");

            var project = await db.Projects.FindAsync([model.ProjectId], ct);

            // 1. Store bytes in ACC — a new version, never an overwrite (FR1.2).
            AccUploadResult uploaded;
            try
            {
                await using var stream = file.OpenReadStream();
                uploaded = await acc.UploadVersionAsync(
                    project!.AccProjectUrn, model.AccFolderUrn, file.FileName, stream, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No partial success (spec 12): ACC failed → nothing was persisted locally either.
                return ApiError.UpstreamError($"ACC upload failed: {ex.Message}");
            }

            // 2. Extract metadata — failure never blocks storage (FR1.4 edge case).
            var extractor = extractors.FirstOrDefault(e =>
                string.Equals(e.SourceTool, sourceTool, StringComparison.OrdinalIgnoreCase));
            MetadataResult meta;
            if (extractor is null)
            {
                meta = new MetadataResult(null, $"No metadata extractor registered for sourceTool '{sourceTool}'.");
            }
            else
            {
                await using var stream = file.OpenReadStream();
                meta = await extractor.ExtractAsync(stream, file.FileName, ct);
            }

            // 3. Persist Version record linked to the ACC version (spec 04: no drift).
            var versionNumber = (await db.ModelVersions.Where(v => v.ModelId == modelId)
                .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
            var version = new ModelVersion
            {
                Id = Guid.NewGuid(), ModelId = modelId, VersionNumber = versionNumber,
                AccItemVersionUrn = uploaded.ItemVersionUrn,
                UploadedById = user.Id, UploadedAt = DateTimeOffset.UtcNow,
                ChangeDescription = changeDescription,
                SourceTool = sourceTool, SourceToolVersion = sourceToolVersion,
                ReviewStatus = ReviewStatus.Draft, FileSizeBytes = file.Length,
                MetadataJson = meta.Metadata is null ? null : JsonSerializer.Serialize(meta.Metadata, JsonOpts),
                ParseError = meta.ParseError
            };
            db.ModelVersions.Add(version);
            await Audit(db, model.ProjectId, user, "version.uploaded", modelId, version.Id,
                new { versionNumber, changeDescription });
            await db.SaveChangesAsync(ct);

            return Results.Json(new { version = VersionDto(version) }, JsonOpts, statusCode: 201);
        });

        // ---- Version history (spec 04) ----

        api.MapGet("/models/{modelId:guid}/versions", async (
            Guid modelId, int? page, int? pageSize, ConnectorDbContext db,
            CurrentUserService current, CancellationToken ct) =>
        {
            var (model, err) = await LoadModelForRead(modelId, db, current, ct);
            if (err is not null) return err;

            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var pageNum = Math.Max(page ?? 1, 1);
            var query = db.ModelVersions.Where(v => v.ModelId == modelId)
                .OrderByDescending(v => v.VersionNumber);
            var total = await query.CountAsync(ct);
            var versions = await query.Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);

            return Results.Json(new
            {
                versions = versions.Select(VersionDto),
                nextPage = pageNum * size < total ? pageNum + 1 : (int?)null
            }, JsonOpts);
        });

        api.MapGet("/models/{modelId:guid}/versions/latest-approved", async (
            Guid modelId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (model, err) = await LoadModelForRead(modelId, db, current, ct);
            if (err is not null) return err;

            if (model!.CurrentApprovedVersionId is Guid approvedId)
            {
                var approved = await db.ModelVersions.FindAsync([approvedId], ct);
                return Results.Json(new { version = VersionDto(approved!) }, JsonOpts);
            }

            // FR3.2: no approved version → explicit flagged fallback, never silent substitution.
            var latest = await db.ModelVersions.Where(v => v.ModelId == modelId)
                .OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync(ct);
            return Results.Json(new
            {
                version = (object?)null,
                fallback = latest is null ? null : new { version = VersionDto(latest), reviewStatus = latest.ReviewStatus.ToString() }
            }, JsonOpts);
        });

        api.MapGet("/versions/{versionId:guid}/download", async (
            Guid versionId, ConnectorDbContext db, CurrentUserService current,
            IAccClient acc, CancellationToken ct) =>
        {
            var version = await db.ModelVersions.Include(v => v.Model)
                .FirstOrDefaultAsync(v => v.Id == versionId, ct);
            if (version is null) return ApiError.NotFound($"Version {versionId} not found.");

            var user = await current.GetUserAsync(ct);
            if (user is null) return ApiError.Unauthenticated();
            if (await current.GetRoleAsync(user.Id, version.Model!.ProjectId, ct) is null)
                return ApiError.Forbidden();

            var project = await db.Projects.FindAsync([version.Model.ProjectId], ct);
            try
            {
                var url = await acc.GetDownloadUrlAsync(project!.AccProjectUrn, version.AccItemVersionUrn, ct);
                return Results.Redirect(url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiError.UpstreamError($"ACC download URL resolution failed: {ex.Message}");
            }
        });
    }

    // ---- helpers ----

    private static async Task<(Model?, IResult?)> LoadModelForRead(
        Guid modelId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct)
    {
        var user = await current.GetUserAsync(ct);
        if (user is null) return (null, ApiError.Unauthenticated());
        var model = await db.Models.FindAsync([modelId], ct);
        if (model is null) return (null, ApiError.NotFound($"Model {modelId} not found."));
        if (await current.GetRoleAsync(user.Id, model.ProjectId, ct) is null)
            return (null, ApiError.Forbidden());
        return (model, null);
    }

    private static async Task Audit(ConnectorDbContext db, Guid projectId, User actor,
        string eventType, Guid? modelId, Guid? versionId, object? payload)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            ProjectId = projectId, EventType = eventType,
            ActorSnapshotJson = JsonSerializer.Serialize(
                new { userId = actor.Id, actor.Name, actor.Email }, JsonOpts),
            ModelId = modelId, VersionId = versionId,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts),
            Timestamp = DateTimeOffset.UtcNow
        });
        await Task.CompletedTask;
    }

    private static object ModelDto(Model m) => new
    {
        id = m.Id, projectId = m.ProjectId, name = m.Name,
        toolType = m.ToolType.ToString(), accFolderUrn = m.AccFolderUrn,
        currentApprovedVersionId = m.CurrentApprovedVersionId
    };

    private static object VersionDto(ModelVersion v) => new
    {
        id = v.Id, modelId = v.ModelId, versionNumber = v.VersionNumber,
        accItemVersionUrn = v.AccItemVersionUrn, uploadedBy = v.UploadedById,
        uploadedAt = v.UploadedAt, changeDescription = v.ChangeDescription,
        sourceTool = v.SourceTool, sourceToolVersion = v.SourceToolVersion,
        reviewStatus = v.ReviewStatus.ToString(), fileSizeBytes = v.FileSizeBytes,
        metadata = v.MetadataJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(v.MetadataJson),
        parseError = v.ParseError
    };
}

public record CreateModelRequest(string Name, string ToolType, string AccFolderUrn);
