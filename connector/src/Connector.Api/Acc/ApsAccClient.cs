using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Connector.Api.Acc;

/// <summary>
/// ACC Docs implementation via the APS Data Management API (3-legged user token).
///
/// Upload flow per version (spec 01 FR1.2 — always a new version, never an overwrite):
///   1. POST projects/:p/storage            → storage object in the project's WIP bucket
///   2. OSS signeds3upload PUT + complete   → bytes land in Autodesk cloud
///   3a. First upload of this file name     → POST projects/:p/items   (item + version 1)
///   3b. Subsequent uploads                 → POST projects/:p/versions (version N+1)
/// The created version URN is stored as ModelVersion.AccItemVersionUrn; files are visible
/// in the ACC Docs web UI folder tree immediately.
///
/// Download: version → storage relationship → OSS signeds3download URL.
/// </summary>
public class ApsAccClient(ApsTokenService tokens, IHttpClientFactory httpFactory) : IAccClient
{
    private const string ApsBase = "https://developer.api.autodesk.com";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<AccUploadResult> UploadVersionAsync(
        string projectUrn, string folderUrn, string fileName, Stream content, CancellationToken ct)
    {
        var http = await AuthedClientAsync(ct);

        // 1. Create a storage location in the project.
        var storageReq = new
        {
            jsonapi = new { version = "1.0" },
            data = new
            {
                type = "objects",
                attributes = new { name = fileName },
                relationships = new
                {
                    target = new { data = new { type = "folders", id = folderUrn } }
                }
            }
        };
        var storageRes = await PostJsonAsync(http,
            $"{ApsBase}/data/v1/projects/{projectUrn}/storage", storageReq, ct, "creating storage");
        var objectId = storageRes.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        storageRes.Dispose();
        var (bucket, objectKey) = ParseObjectId(objectId);

        // 2. Upload bytes via signed S3.
        var encodedKey = Uri.EscapeDataString(objectKey);
        var signedRes = await http.GetAsync(
            $"{ApsBase}/oss/v2/buckets/{bucket}/objects/{encodedKey}/signeds3upload?parts=1", ct);
        await ThrowIfFailed(signedRes, "requesting signed upload URL", ct);
        using var signedJson = JsonDocument.Parse(await signedRes.Content.ReadAsStringAsync(ct));
        var uploadUrl = signedJson.RootElement.GetProperty("urls")[0].GetString()!;
        var uploadKey = signedJson.RootElement.GetProperty("uploadKey").GetString()!;

        var s3 = httpFactory.CreateClient("s3");
        using (var putContent = new StreamContent(content))
        {
            var putRes = await s3.PutAsync(uploadUrl, putContent, ct);
            if (!putRes.IsSuccessStatusCode)
                throw new HttpRequestException($"S3 upload failed ({(int)putRes.StatusCode}).");
        }
        var completeRes = await http.PostAsync(
            $"{ApsBase}/oss/v2/buckets/{bucket}/objects/{encodedKey}/signeds3upload",
            new StringContent(JsonSerializer.Serialize(new { uploadKey }, JsonOpts), Encoding.UTF8, "application/json"), ct);
        await ThrowIfFailed(completeRes, "completing signed upload", ct);

        // 3. Create Docs item (first upload) or new version (subsequent).
        var existingItemId = await FindItemByNameAsync(http, projectUrn, folderUrn, fileName, ct);
        if (existingItemId is null)
        {
            var itemReq = new
            {
                jsonapi = new { version = "1.0" },
                data = new
                {
                    type = "items",
                    attributes = new
                    {
                        displayName = fileName,
                        extension = new { type = "items:autodesk.bim360:File", version = "1.0" }
                    },
                    relationships = new
                    {
                        tip = new { data = new { type = "versions", id = "1" } },
                        parent = new { data = new { type = "folders", id = folderUrn } }
                    }
                },
                included = new object[]
                {
                    new
                    {
                        type = "versions",
                        id = "1",
                        attributes = new
                        {
                            name = fileName,
                            extension = new { type = "versions:autodesk.bim360:File", version = "1.0" }
                        },
                        relationships = new
                        {
                            storage = new { data = new { type = "objects", id = objectId } }
                        }
                    }
                }
            };
            var itemRes = await PostJsonAsync(http,
                $"{ApsBase}/data/v1/projects/{projectUrn}/items", itemReq, ct, "creating Docs item");
            var versionUrn = itemRes.RootElement.GetProperty("included")[0].GetProperty("id").GetString()!;
            itemRes.Dispose();
            return new AccUploadResult(versionUrn, 1);
        }
        else
        {
            var versionReq = new
            {
                jsonapi = new { version = "1.0" },
                data = new
                {
                    type = "versions",
                    attributes = new
                    {
                        name = fileName,
                        extension = new { type = "versions:autodesk.bim360:File", version = "1.0" }
                    },
                    relationships = new
                    {
                        item = new { data = new { type = "items", id = existingItemId } },
                        storage = new { data = new { type = "objects", id = objectId } }
                    }
                }
            };
            var verRes = await PostJsonAsync(http,
                $"{ApsBase}/data/v1/projects/{projectUrn}/versions", versionReq, ct, "creating Docs version");
            var versionUrn = verRes.RootElement.GetProperty("data").GetProperty("id").GetString()!;
            var versionNumber = verRes.RootElement.GetProperty("data").GetProperty("attributes")
                .TryGetProperty("versionNumber", out var vn) ? vn.GetInt32() : 0;
            verRes.Dispose();
            return new AccUploadResult(versionUrn, versionNumber);
        }
    }

    public async Task<string> GetDownloadUrlAsync(string projectUrn, string itemVersionUrn, CancellationToken ct)
    {
        var http = await AuthedClientAsync(ct);

        var verRes = await http.GetAsync(
            $"{ApsBase}/data/v1/projects/{projectUrn}/versions/{Uri.EscapeDataString(itemVersionUrn)}", ct);
        await ThrowIfFailed(verRes, "fetching version", ct);
        using var verJson = JsonDocument.Parse(await verRes.Content.ReadAsStringAsync(ct));
        var objectId = verJson.RootElement.GetProperty("data").GetProperty("relationships")
            .GetProperty("storage").GetProperty("data").GetProperty("id").GetString()!;
        var (bucket, objectKey) = ParseObjectId(objectId);

        var dlRes = await http.GetAsync(
            $"{ApsBase}/oss/v2/buckets/{bucket}/objects/{Uri.EscapeDataString(objectKey)}/signeds3download", ct);
        await ThrowIfFailed(dlRes, "requesting signed download URL", ct);
        using var dlJson = JsonDocument.Parse(await dlRes.Content.ReadAsStringAsync(ct));
        return dlJson.RootElement.GetProperty("url").GetString()!;
    }

    // ---- helpers ----

    private async Task<HttpClient> AuthedClientAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient("aps");
        var token = tokens.UserAuthorized
            ? await tokens.GetThreeLeggedTokenAsync(ct)
            : await tokens.GetTwoLeggedTokenAsync(ct);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<string?> FindItemByNameAsync(
        HttpClient http, string projectUrn, string folderUrn, string fileName, CancellationToken ct)
    {
        var res = await http.GetAsync(
            $"{ApsBase}/data/v1/projects/{projectUrn}/folders/{Uri.EscapeDataString(folderUrn)}/contents" +
            $"?filter[type]=items&page[limit]=200", ct);
        await ThrowIfFailed(res, "listing folder contents", ct);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
        {
            var name = item.GetProperty("attributes").GetProperty("displayName").GetString();
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return item.GetProperty("id").GetString();
        }
        return null;
    }

    private static (string Bucket, string ObjectKey) ParseObjectId(string objectId)
    {
        const string marker = ":os.object:";
        var idx = objectId.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) throw new ArgumentException($"Not an OSS object URN: {objectId}");
        var path = objectId[(idx + marker.Length)..];
        var slash = path.IndexOf('/');
        // Keys can arrive pre-encoded (%2F) — normalize before callers re-encode.
        return (path[..slash], Uri.UnescapeDataString(path[(slash + 1)..]));
    }

    private static async Task<JsonDocument> PostJsonAsync(
        HttpClient http, string url, object body, CancellationToken ct, string action)
    {
        // DM API rejects "application/vnd.api+json; charset=utf-8" with 415 — the
        // Content-Type must be exactly "application/vnd.api+json", no charset parameter.
        var content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.api+json");
        var res = await http.PostAsync(url, content, ct);
        await ThrowIfFailed(res, action, ct);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private static async Task ThrowIfFailed(HttpResponseMessage res, string action, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"APS error while {action} ({(int)res.StatusCode}): {body}");
    }
}
