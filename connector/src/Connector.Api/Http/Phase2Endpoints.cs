using System.Text;
using System.Text.Json;
using Connector.Api.Data;
using Connector.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Connector.Api.Http;

// Phase 2 endpoints: checkout/soft-lock (specs/02), review workflow (specs/03),
// audit query + CSV export (specs/07). Contracts per specs/11, roles per specs/12.
public static class Phase2Endpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapPhase2Endpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        // ---- Checkout (specs/02) ----

        api.MapPost("/models/{modelId:guid}/checkout", async (
            Guid modelId, CheckoutRequest? body, ConnectorDbContext db, IConfiguration config,
            CurrentUserService current, CancellationToken ct) =>
        {
            var (user, model, role, err) = await Load(modelId, db, current, ct);
            if (err is not null) return err;
            if (role is ProjectRole.Viewer)
                return ApiError.Forbidden("Viewers cannot check out models.");

            var expiryHours = config.GetValue("Checkout:ExpiryHours", 24);
            var now = DateTimeOffset.UtcNow;
            var existing = await db.CheckoutStates.Include(c => c.CheckedOutBy)
                .FirstOrDefaultAsync(c => c.ModelId == modelId, ct);

            if (existing is not null && existing.ExpiresAt > now && existing.CheckedOutById != user!.Id)
            {
                if (body?.Override != true)
                    return ApiError.Conflict("checked_out", new
                    {
                        holder = new { userId = existing.CheckedOutById, name = existing.CheckedOutBy?.Name },
                        checkedOutAt = existing.CheckedOutAt
                    });

                // Explicit override — allowed but logged as a distinct audit event (FR2.2).
                await Audit(db, model!.ProjectId, user!, "checkout.override", modelId, null, new
                {
                    previousHolder = existing.CheckedOutBy?.Name,
                    newHolder = user!.Name
                });
                db.CheckoutStates.Remove(existing);
                await db.SaveChangesAsync(ct);
                existing = null;
            }

            if (existing is not null && (existing.ExpiresAt <= now || existing.CheckedOutById == user!.Id))
            {
                db.CheckoutStates.Remove(existing); // expired, or renewing own checkout
                await db.SaveChangesAsync(ct);
            }

            var checkout = new CheckoutState
            {
                ModelId = modelId, CheckedOutById = user!.Id,
                CheckedOutAt = now, ExpiresAt = now.AddHours(expiryHours)
            };
            db.CheckoutStates.Add(checkout);
            await Audit(db, model!.ProjectId, user, "checkout.acquired", modelId, null,
                new { expiresAt = checkout.ExpiresAt });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Near-simultaneous checkout race: first write wins (spec 02 edge case).
                return ApiError.Conflict("checked_out", new { detail = "Another user acquired the checkout first." });
            }
            return Results.Json(new { checkoutState = CheckoutDto(checkout, user.Name) }, JsonOpts);
        });

        api.MapDelete("/models/{modelId:guid}/checkout", async (
            Guid modelId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (user, model, role, err) = await Load(modelId, db, current, ct);
            if (err is not null) return err;

            var existing = await db.CheckoutStates.FirstOrDefaultAsync(c => c.ModelId == modelId, ct);
            if (existing is null) return Results.NoContent();
            if (existing.CheckedOutById != user!.Id && role != ProjectRole.Admin)
                return ApiError.Forbidden("Only the checkout owner or an Admin can release it.");

            db.CheckoutStates.Remove(existing);
            await Audit(db, model!.ProjectId, user, "checkout.released", modelId, null,
                new { releasedBy = existing.CheckedOutById == user.Id ? "self" : "admin" });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/models/{modelId:guid}/checkout", async (
            Guid modelId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (_, _, _, err) = await Load(modelId, db, current, ct);
            if (err is not null) return err;
            var c = await db.CheckoutStates.Include(x => x.CheckedOutBy)
                .FirstOrDefaultAsync(x => x.ModelId == modelId, ct);
            var active = c is not null && c.ExpiresAt > DateTimeOffset.UtcNow;
            return Results.Json(new { checkoutState = active ? CheckoutDto(c!, c!.CheckedOutBy?.Name) : null }, JsonOpts);
        });

        // ---- Review workflow (specs/03) ----

        api.MapPost("/versions/{versionId:guid}/submit", (Delegate)(async (
            Guid versionId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (user, version, role, err) = await LoadVersion(versionId, db, current, ct);
            if (err is not null) return err;
            if (role is ProjectRole.Viewer) return ApiError.Forbidden("Viewers cannot submit for review.");
            if (version!.ReviewStatus != ReviewStatus.Draft)
                return ApiError.Unprocessable($"Only Draft versions can be submitted (current: {version.ReviewStatus}).");

            version.ReviewStatus = ReviewStatus.InReview;
            db.ReviewEvents.Add(NewEvent(version.Id, user!.Id, "submitted", null));
            await Audit(db, version.Model!.ProjectId, user, "review.submitted", version.ModelId, version.Id, null);
            return await SaveTransition(db, version, ct);
        }));

        api.MapPost("/versions/{versionId:guid}/approve", (Delegate)(async (
            Guid versionId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (user, version, role, err) = await LoadVersion(versionId, db, current, ct);
            if (err is not null) return err;
            if (role is not (ProjectRole.Reviewer or ProjectRole.Admin))
                return ApiError.Forbidden("Only Reviewer or Admin can approve.");
            if (version!.ReviewStatus != ReviewStatus.InReview)
                return ApiError.Unprocessable($"Only InReview versions can be approved (current: {version.ReviewStatus}).");

            version.ReviewStatus = ReviewStatus.Approved;
            version.Model!.CurrentApprovedVersionId = version.Id; // FR5.6 — this is what "latest approved" serves
            var selfApproved = version.UploadedById == user!.Id;
            db.ReviewEvents.Add(NewEvent(version.Id, user.Id, "approved", null));
            await Audit(db, version.Model.ProjectId, user, "review.approved", version.ModelId, version.Id,
                new { selfApproved });
            return await SaveTransition(db, version, ct);
        }));

        api.MapPost("/versions/{versionId:guid}/reject", (Delegate)(async (
            Guid versionId, RejectRequest? body, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (user, version, role, err) = await LoadVersion(versionId, db, current, ct);
            if (err is not null) return err;
            if (role is not (ProjectRole.Reviewer or ProjectRole.Admin))
                return ApiError.Forbidden("Only Reviewer or Admin can reject.");
            if (string.IsNullOrWhiteSpace(body?.Comment))
                return ApiError.ValidationFailed("comment_required: rejection requires a comment (FR5.3).");
            if (version!.ReviewStatus != ReviewStatus.InReview)
                return ApiError.Unprocessable($"Only InReview versions can be rejected (current: {version.ReviewStatus}).");

            version.ReviewStatus = ReviewStatus.Rejected;
            db.ReviewEvents.Add(NewEvent(version.Id, user!.Id, "rejected", body.Comment.Trim()));
            await Audit(db, version.Model!.ProjectId, user, "review.rejected", version.ModelId, version.Id,
                new { comment = body.Comment.Trim() });
            return await SaveTransition(db, version, ct);
        }));

        api.MapGet("/versions/{versionId:guid}/review-events", async (
            Guid versionId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var (_, version, _, err) = await LoadVersion(versionId, db, current, ct);
            if (err is not null) return err;
            var events = await db.ReviewEvents.Where(e => e.VersionId == versionId)
                .OrderBy(e => e.Timestamp).ToListAsync(ct);
            var actors = await db.Users.Where(u => events.Select(e => e.ActorId).Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
            return Results.Json(new
            {
                reviewEvents = events.Select(e => new
                {
                    e.Id, e.Action, e.Comment, e.Timestamp,
                    actor = new { id = e.ActorId, name = actors.GetValueOrDefault(e.ActorId) }
                })
            }, JsonOpts);
        });

        // ---- Activity cursor ----
        // Cheap "anything new?" endpoint the dashboard polls: returns the latest audit
        // event id for the project. Any state change (upload, download, checkout, review)
        // writes an audit row, so a bumped id == something happened worth refreshing for.
        api.MapGet("/projects/{projectId:guid}/activity", async (
            Guid projectId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var user = await current.GetUserAsync(ct);
            if (user is null) return ApiError.Unauthenticated();
            if (await current.GetRoleAsync(user.Id, projectId, ct) is null) return ApiError.Forbidden();
            var lastId = await db.AuditEvents.Where(e => e.ProjectId == projectId)
                .MaxAsync(e => (long?)e.Id, ct) ?? 0;
            return Results.Json(new { lastEventId = lastId }, JsonOpts);
        });

        // ---- Audit query + CSV export (specs/07) ----

        api.MapGet("/projects/{projectId:guid}/audit-events", async (
            Guid projectId, DateTimeOffset? from, DateTimeOffset? to, string? eventType, Guid? modelId,
            ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var user = await current.GetUserAsync(ct);
            if (user is null) return ApiError.Unauthenticated();
            if (await current.GetRoleAsync(user.Id, projectId, ct) is null) return ApiError.Forbidden();

            var events = await AuditQuery(db, projectId, from, to, eventType, modelId).Take(500).ToListAsync(ct);
            return Results.Json(new
            {
                events = events.Select(e => new
                {
                    e.Id, e.EventType, e.Timestamp, e.ModelId, e.VersionId,
                    actor = JsonSerializer.Deserialize<JsonElement>(e.ActorSnapshotJson),
                    payload = e.PayloadJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(e.PayloadJson)
                })
            }, JsonOpts);
        });

        api.MapGet("/projects/{projectId:guid}/audit-events/export", async (
            Guid projectId, DateTimeOffset? from, DateTimeOffset? to, string? eventType, Guid? modelId,
            ConnectorDbContext db, CurrentUserService current, CancellationToken ct) =>
        {
            var user = await current.GetUserAsync(ct);
            if (user is null) return ApiError.Unauthenticated();
            if (await current.GetRoleAsync(user.Id, projectId, ct) != ProjectRole.Admin)
                return ApiError.Forbidden("Audit export is Admin-only (specs/12).");

            var events = await AuditQuery(db, projectId, from, to, eventType, modelId).ToListAsync(ct);

            var csv = new StringBuilder("timestamp,eventType,actorName,actorEmail,modelId,versionId,payload\r\n");
            foreach (var e in events)
            {
                var actor = JsonSerializer.Deserialize<JsonElement>(e.ActorSnapshotJson);
                csv.Append(Csv(e.Timestamp.ToString("o"))).Append(',')
                   .Append(Csv(e.EventType)).Append(',')
                   .Append(Csv(actor.TryGetProperty("name", out var n) ? n.GetString() : null)).Append(',')
                   .Append(Csv(actor.TryGetProperty("email", out var em) ? em.GetString() : null)).Append(',')
                   .Append(Csv(e.ModelId?.ToString())).Append(',')
                   .Append(Csv(e.VersionId?.ToString())).Append(',')
                   .Append(Csv(e.PayloadJson)).Append("\r\n");
            }

            // The export itself is auditable (specs/07 flow).
            db.AuditEvents.Add(new AuditEvent
            {
                ProjectId = projectId, EventType = "audit.exported",
                ActorSnapshotJson = JsonSerializer.Serialize(new { userId = user.Id, user.Name, user.Email }, JsonOpts),
                PayloadJson = JsonSerializer.Serialize(new { rowCount = events.Count }, JsonOpts),
                Timestamp = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);

            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
                $"audit_{projectId:N}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.csv");
        });
    }

    // ---- helpers ----

    private static IOrderedQueryable<AuditEvent> AuditQuery(ConnectorDbContext db, Guid projectId,
        DateTimeOffset? from, DateTimeOffset? to, string? eventType, Guid? modelId)
    {
        var q = db.AuditEvents.Where(e => e.ProjectId == projectId);
        if (from is not null) q = q.Where(e => e.Timestamp >= from);
        if (to is not null) q = q.Where(e => e.Timestamp <= to);
        if (eventType is not null) q = q.Where(e => e.EventType == eventType);
        if (modelId is not null) q = q.Where(e => e.ModelId == modelId);
        return q.OrderBy(e => e.Timestamp);
    }

    private static async Task<(User?, Model?, ProjectRole?, IResult?)> Load(
        Guid modelId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct)
    {
        var user = await current.GetUserAsync(ct);
        if (user is null) return (null, null, null, ApiError.Unauthenticated());
        var model = await db.Models.FindAsync([modelId], ct);
        if (model is null) return (null, null, null, ApiError.NotFound($"Model {modelId} not found."));
        var role = await current.GetRoleAsync(user.Id, model.ProjectId, ct);
        if (role is null) return (null, null, null, ApiError.Forbidden());
        return (user, model, role, null);
    }

    private static async Task<(User?, ModelVersion?, ProjectRole?, IResult?)> LoadVersion(
        Guid versionId, ConnectorDbContext db, CurrentUserService current, CancellationToken ct)
    {
        var user = await current.GetUserAsync(ct);
        if (user is null) return (null, null, null, ApiError.Unauthenticated());
        var version = await db.ModelVersions.Include(v => v.Model)
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null) return (null, null, null, ApiError.NotFound($"Version {versionId} not found."));
        var role = await current.GetRoleAsync(user.Id, version.Model!.ProjectId, ct);
        if (role is null) return (null, null, null, ApiError.Forbidden());
        return (user, version, role, null);
    }

    private static async Task<IResult> SaveTransition(ConnectorDbContext db, ModelVersion version, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two reviewers raced on the same version: first transition wins (spec 03 edge case).
            return ApiError.Conflict("already_reviewed",
                new { detail = "This version was reviewed by someone else a moment ago — reload and re-check." });
        }
        return Results.Json(new
        {
            version = new { version.Id, version.ModelId, version.VersionNumber, reviewStatus = version.ReviewStatus.ToString() }
        }, JsonOpts);
    }

    private static ReviewEvent NewEvent(Guid versionId, Guid actorId, string action, string? comment) => new()
    {
        Id = Guid.NewGuid(), VersionId = versionId, ActorId = actorId,
        Action = action, Comment = comment, Timestamp = DateTimeOffset.UtcNow
    };

    private static async Task Audit(ConnectorDbContext db, Guid projectId, User actor,
        string eventType, Guid? modelId, Guid? versionId, object? payload)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            ProjectId = projectId, EventType = eventType,
            ActorSnapshotJson = JsonSerializer.Serialize(new { userId = actor.Id, actor.Name, actor.Email }, JsonOpts),
            ModelId = modelId, VersionId = versionId,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts),
            Timestamp = DateTimeOffset.UtcNow
        });
        await Task.CompletedTask;
    }

    private static object CheckoutDto(CheckoutState c, string? holderName) => new
    {
        modelId = c.ModelId, checkedOutBy = new { id = c.CheckedOutById, name = holderName },
        checkedOutAt = c.CheckedOutAt, expiresAt = c.ExpiresAt
    };

    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}

public record CheckoutRequest(bool? Override);
public record RejectRequest(string? Comment);
