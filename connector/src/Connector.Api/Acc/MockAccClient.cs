using System.Collections.Concurrent;

namespace Connector.Api.Acc;

/// <summary>
/// In-memory + local-disk ACC stand-in for development before APS app credentials exist
/// (see docs/APS-SETUP.md). Stores uploaded files under a local directory and fabricates
/// URNs with a "mock:" prefix so they can never be confused with real ACC URNs.
/// </summary>
public class MockAccClient(IConfiguration config) : IAccClient
{
    private readonly string _storageRoot = config["MockAcc:StoragePath"]
        ?? Path.Combine(Path.GetTempPath(), "acc-mock-storage");
    private readonly ConcurrentDictionary<string, int> _versionCounters = new();

    public async Task<AccUploadResult> UploadVersionAsync(
        string projectUrn, string folderUrn, string fileName, Stream content, CancellationToken ct)
    {
        var itemKey = $"{projectUrn}|{folderUrn}|{fileName}";
        var versionNumber = _versionCounters.AddOrUpdate(itemKey, 1, (_, n) => n + 1);
        var urn = $"mock:itemver:{Guid.NewGuid():N}:v{versionNumber}";

        var dir = Path.Combine(_storageRoot, Sanitize(folderUrn));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Sanitize(urn)}_{fileName}");
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);

        return new AccUploadResult(urn, versionNumber);
    }

    public Task<string> GetDownloadUrlAsync(string projectUrn, string itemVersionUrn, CancellationToken ct)
    {
        var dirs = Directory.Exists(_storageRoot)
            ? Directory.EnumerateFiles(_storageRoot, $"{Sanitize(itemVersionUrn)}_*", SearchOption.AllDirectories)
            : [];
        var match = dirs.FirstOrDefault()
            ?? throw new FileNotFoundException($"Mock ACC has no stored file for {itemVersionUrn}");
        return Task.FromResult(new Uri(match).AbsoluteUri); // file:// URL — dev only
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
