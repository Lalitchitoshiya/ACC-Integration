namespace Connector.Api.Acc;

public record AccUploadResult(string ItemVersionUrn, int AccVersionNumber);

/// <summary>
/// Abstraction over ACC Data Management API (specs/08-architecture.md).
/// Implementations: MockAccClient (dev without APS credentials), ApsAccClient (real).
/// </summary>
public interface IAccClient
{
    /// <summary>
    /// Upload file content as a new version of an item in the given ACC folder.
    /// Creates the item on first upload, versions it thereafter (FR1.2 — never overwrites).
    /// </summary>
    Task<AccUploadResult> UploadVersionAsync(
        string projectUrn, string folderUrn, string fileName, Stream content, CancellationToken ct);

    /// <summary>Short-lived signed download URL for a specific item version (spec 04 flow).</summary>
    Task<string> GetDownloadUrlAsync(string projectUrn, string itemVersionUrn, CancellationToken ct);
}
