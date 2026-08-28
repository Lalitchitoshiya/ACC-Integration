using Connector.Api.Acc;
using Connector.Api.Data;
using Connector.Api.Http;
using Connector.Api.Metadata;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ConnectorDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Connector")));

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddSingleton<ApsTokenService>();
builder.Services.AddSingleton<ModelDerivativeClient>();
builder.Services.AddSingleton<IMetadataExtractor, WsProMetadataExtractor>();

if (builder.Configuration.GetValue("Acc:UseMock", true))
    builder.Services.AddSingleton<IAccClient, MockAccClient>();
else
    builder.Services.AddSingleton<IAccClient, ApsAccClient>();

builder.Services.AddHostedService<CheckoutExpirySweeper>();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Dev convenience: create schema + seed a demo project/users if DB is empty.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
    await db.Database.EnsureCreatedAsync();
    await DevSeed.EnsureSeededAsync(db);
}

app.UseDefaultFiles(); // serves wwwroot/index.html at / — the dashboard
app.UseStaticFiles();

app.MapConnectorEndpoints();
app.MapPhase2Endpoints();

// APS 3-legged login: browser flow. Visit /api/auth/login, sign in with the Autodesk
// account that owns the ACC hub, land back on /api/auth/callback.
app.MapGet("/api/auth/login", (ApsTokenService aps) => Results.Redirect(aps.BuildAuthorizeUrl()));

app.MapGet("/api/auth/callback", async (string? code, ApsTokenService aps, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(code)) return Results.BadRequest("Missing ?code from Autodesk redirect.");
    await aps.ExchangeCodeAsync(code, ct);
    return Results.Text("Autodesk authorization complete — you can close this tab.", "text/plain");
});

// Dev diagnostics: proxy an arbitrary Data Management GET with the user token, e.g.
// /api/v1/aps/dm?path=/project/v1/hubs/b.xxx/projects — exploration only, remove in prod.
app.MapGet("/api/v1/aps/dm", async (
    string path, ApsTokenService aps, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    if (!path.StartsWith('/')) return Results.BadRequest("path must start with /");
    var token = await aps.GetThreeLeggedTokenAsync(ct);
    var http = httpFactory.CreateClient("aps");
    http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    var res = await http.GetAsync($"https://developer.api.autodesk.com{path}", ct);
    var body = await res.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)res.StatusCode);
});

// Dev diagnostics: POST twin of the /aps/dm proxy — needed for the IFC spike
// (specs/14 FR14.8) to submit Model Derivative jobs for hand-assembled files.
// Same 3-legged token, exploration only, remove in prod.
app.MapPost("/api/v1/aps/dmpost", async (
    string path, HttpRequest request, ApsTokenService aps, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    if (!path.StartsWith('/')) return Results.BadRequest("path must start with /");
    var token = await aps.GetThreeLeggedTokenAsync(ct);
    var http = httpFactory.CreateClient("aps");
    http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    using var bodyReader = new StreamReader(request.Body);
    var body = await bodyReader.ReadToEndAsync(ct);
    var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
    // Force retranslation when resubmitting the same source URN (spike iteration).
    http.DefaultRequestHeaders.Add("x-ads-force", "true");
    var res = await http.PostAsync($"https://developer.api.autodesk.com{path}", content, ct);
    var resBody = await res.Content.ReadAsStringAsync(ct);
    return Results.Content(resBody, "application/json", statusCode: (int)res.StatusCode);
});

// Dev diagnostics: generate the IFC for an existing version's file on demand (Stage 2
// validation, specs/14 Track B) — downloads the source from ACC, runs the extractor +
// IfcWriter, returns the STEP text. No side effects; remove in prod.
app.MapGet("/api/v1/dev/ifc-preview", async (
    Guid versionId, ConnectorDbContext db, IAccClient acc, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var version = await db.ModelVersions.Include(v => v.Model).ThenInclude(m => m!.Project)
        .FirstOrDefaultAsync(v => v.Id == versionId, ct);
    if (version is null) return Results.NotFound();
    var url = await acc.GetDownloadUrlAsync(version.Model!.Project!.AccProjectUrn, version.AccItemVersionUrn, ct);
    var http = httpFactory.CreateClient("s3");
    await using var stream = await http.GetStreamAsync(url, ct);
    var graph = await NetworkGraphExtractor.ExtractAsync(stream, "source" +
        (version.SourceTool.Contains("INP", StringComparison.OrdinalIgnoreCase) ? ".inp" : ".csv"), ct);
    if (graph is null) return Results.BadRequest("Not a recognized CSV/INP file.");
    var ifc = IfcWriter.Render(graph, version.Model.Name);
    return ifc is null ? Results.BadRequest("Empty graph.") : Results.Text(System.Text.Encoding.ASCII.GetString(ifc), "text/plain");
});

// Dev diagnostics: list ACC/Forma hubs visible to the user token (falls back to app token).
app.MapGet("/api/v1/aps/hubs", async (
    ApsTokenService aps, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var token = aps.UserAuthorized
        ? await aps.GetThreeLeggedTokenAsync(ct)
        : await aps.GetTwoLeggedTokenAsync(ct);
    var http = httpFactory.CreateClient("aps");
    http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    var res = await http.GetAsync("https://developer.api.autodesk.com/project/v1/hubs", ct);
    var body = await res.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)res.StatusCode);
});

// Dev diagnostics: list raw objects in the app's OSS bucket, straight from Autodesk's API.
// Proves what is actually stored in the Autodesk cloud, independent of our DB records.
app.MapGet("/api/v1/aps/objects", async (
    ApsTokenService aps, IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct) =>
{
    var token = await aps.GetTwoLeggedTokenAsync(ct);
    var bucketKey = (config["Aps:BucketKey"] is { Length: > 0 } b
        ? b : $"acc-water-connector-{config["Aps:ClientId"]![..12]}").ToLowerInvariant();
    var http = httpFactory.CreateClient("aps");
    http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    var res = await http.GetAsync(
        $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects", ct);
    var body = await res.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)res.StatusCode);
});

// Dev diagnostics: delete an object from the app's OSS bucket by key — used to
// simulate a file deleted/moved outside the connector, for testing the
// AccFileMissing detection path (specs/04 drift case). Development only.
app.MapDelete("/api/v1/aps/buckets/{bucketKey}/objects/{*objectKey}", async (
    string bucketKey, string objectKey, ApsTokenService aps, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    // ACC Docs storage lives in Autodesk's own project bucket (e.g. wip.dm.*),
    // not our custom bucket — bucket is caller-supplied so this works for either.
    var token = aps.UserAuthorized ? await aps.GetThreeLeggedTokenAsync(ct) : await aps.GetTwoLeggedTokenAsync(ct);
    var http = httpFactory.CreateClient("aps");
    http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    var res = await http.DeleteAsync(
        $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects/{Uri.EscapeDataString(objectKey)}", ct);
    var body = await res.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)res.StatusCode);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Verifies APS credentials from user-secrets without exposing them: acquires a 2-legged
// token and reports only success/failure. Dev diagnostics — remove before production.
app.MapGet("/api/v1/aps/status", async (ApsTokenService aps, CancellationToken ct) =>
{
    if (!aps.CredentialsConfigured)
        return Results.Json(new
        {
            configured = false,
            hint = "Run: dotnet user-secrets set \"Aps:ClientId\" \"...\" --project src/Connector.Api (and ClientSecret)"
        });
    try
    {
        await aps.GetTwoLeggedTokenAsync(ct);
        return Results.Json(new { configured = true, tokenAcquired = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { configured = true, tokenAcquired = false, error = ex.Message }, statusCode: 502);
    }
});

// specs/14-cad-visualization.md FR14.5 — short-lived token for the APS Viewer JS SDK.
// Reuses the existing 2-legged token (already carries data:read, sufficient for viewing
// files in the same hub); a dedicated viewables:read-only scope profile is a future
// hardening step, not required for this to work.
app.MapGet("/api/v1/aps/viewer-token", async (ApsTokenService aps, CancellationToken ct) =>
{
    var token = await aps.GetTwoLeggedTokenAsync(ct);
    return Results.Json(new { access_token = token, token_type = "Bearer", expires_in = 3300 });
});

app.Run();
