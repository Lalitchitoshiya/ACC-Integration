using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Connector.Api.Acc;

public record TranslationStatusResult(string Status, string? ErrorMessage);

/// <summary>
/// Autodesk Model Derivative API client (specs/14-cad-visualization.md) — submits a
/// translation job for an uploaded DXF and polls its manifest. Separate from IAccClient
/// deliberately: this is a different Autodesk product surface (viewables, not Docs
/// storage), used only by the Phase 6 CAD visualization path.
/// </summary>
public class ModelDerivativeClient(ApsTokenService tokens, IHttpClientFactory httpFactory)
{
    private const string ApsBase = "https://developer.api.autodesk.com";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Resolves the DXF version's underlying storage object, submits a translation job for
    /// it, and returns the base64(no-padding) urn used both for polling and for the Viewer.
    /// </summary>
    public async Task<string> SubmitTranslationJobAsync(string projectUrn, string dxfItemVersionUrn, CancellationToken ct)
    {
        var http = await AuthedClientAsync(ct);

        var verRes = await http.GetAsync(
            $"{ApsBase}/data/v1/projects/{projectUrn}/versions/{Uri.EscapeDataString(dxfItemVersionUrn)}", ct);
        await ThrowIfFailed(verRes, "fetching DXF version for translation", ct);
        using var verJson = JsonDocument.Parse(await verRes.Content.ReadAsStringAsync(ct));
        var objectId = verJson.RootElement.GetProperty("data").GetProperty("relationships")
            .GetProperty("storage").GetProperty("data").GetProperty("id").GetString()!;

        var urn = Base64UrlEncode(objectId);

        var jobReq = new
        {
            input = new { urn },
            output = new { formats = new[] { new { type = "svf2", views = new[] { "2d" } } } }
        };
        var content = new StringContent(JsonSerializer.Serialize(jobReq, JsonOpts), Encoding.UTF8, "application/json");
        var jobRes = await http.PostAsync($"{ApsBase}/modelderivative/v2/designdata/job", content, ct);
        await ThrowIfFailed(jobRes, "submitting Model Derivative translation job", ct);

        return urn;
    }

    /// <summary>Polls the manifest once — caller decides polling cadence/timeout (specs/14 edge cases).</summary>
    public async Task<TranslationStatusResult> GetTranslationStatusAsync(string urn, CancellationToken ct)
    {
        var http = await AuthedClientAsync(ct);
        var res = await http.GetAsync($"{ApsBase}/modelderivative/v2/designdata/{urn}/manifest", ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            return new TranslationStatusResult("failed", $"Manifest fetch failed ({(int)res.StatusCode}): {body}");
        }
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var status = json.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
        string? error = null;
        if (status is "failed" or "timeout" && json.RootElement.TryGetProperty("derivatives", out var derivs))
        {
            var messages = derivs.EnumerateArray()
                .SelectMany(d => d.TryGetProperty("messages", out var m) ? m.EnumerateArray() : [])
                .Select(m => m.TryGetProperty("message", out var msg) ? msg.GetString() : null)
                .Where(m => m is not null);
            error = string.Join("; ", messages);
        }
        return new TranslationStatusResult(status, error);
    }

    private async Task<HttpClient> AuthedClientAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient("aps");
        var token = tokens.UserAuthorized ? await tokens.GetThreeLeggedTokenAsync(ct) : await tokens.GetTwoLeggedTokenAsync(ct);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static string Base64UrlEncode(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task ThrowIfFailed(HttpResponseMessage res, string action, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"APS error while {action} ({(int)res.StatusCode}): {body}");
    }
}
