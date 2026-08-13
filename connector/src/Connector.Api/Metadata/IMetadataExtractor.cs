namespace Connector.Api.Metadata;

public record MetadataResult(ModelMetadata? Metadata, string? ParseError);

public interface IMetadataExtractor
{
    /// <summary>Which tool's export packages this extractor understands.</summary>
    string SourceTool { get; }

    /// <summary>
    /// Extract metadata per specs/13-metadata-schema.md from an uploaded model package.
    /// Must never throw for bad input — return a MetadataResult with ParseError set instead,
    /// since upload storage is never blocked by metadata failure (spec 01, FR1.4 edge case).
    /// </summary>
    Task<MetadataResult> ExtractAsync(Stream package, string fileName, CancellationToken ct);
}
