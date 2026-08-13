namespace Connector.Api.Metadata;

// C# shape of specs/13-metadata-schema.md (schemaVersion 1.0, modelType waterDistribution).
// Serialized to ModelVersion.MetadataJson (jsonb).

public class ModelMetadata
{
    public string SchemaVersion { get; set; } = "1.0";
    public string SourceTool { get; set; } = "InfoWorksWSPro";
    public string SourceToolVersion { get; set; } = "";
    public string ModelType { get; set; } = "waterDistribution";
    public Units Units { get; set; } = new();
    public Extent Extent { get; set; } = new();
    public Network Network { get; set; } = new();
    public AttributeSummary AttributeSummary { get; set; } = new();
    public NamedElementIndex NamedElementIndex { get; set; } = new();
    public List<string> ParseWarnings { get; set; } = [];
}

public class Units
{
    public string Length { get; set; } = "m";
    public string Flow { get; set; } = "l/s";
    public string Pressure { get; set; } = "m";
}

public class Extent
{
    public string Crs { get; set; } = "";
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

public class Network
{
    public NodeCounts Nodes { get; set; } = new();
    public LinkCounts Links { get; set; } = new();
    public double TotalPipeLength { get; set; }
}

public class NodeCounts
{
    public int Total { get; set; }
    public Dictionary<string, int> ByType { get; set; } = new() { ["junction"] = 0, ["reservoir"] = 0, ["tank"] = 0 };
}

public class LinkCounts
{
    public int Total { get; set; }
    public Dictionary<string, int> ByType { get; set; } = new() { ["pipe"] = 0, ["pump"] = 0, ["valve"] = 0 };
}

public class AttributeSummary
{
    public PipeSummary Pipe { get; set; } = new();
    public JunctionSummary Junction { get; set; } = new();
    public TankSummary Tank { get; set; } = new();
    public CountSummary Pump { get; set; } = new();
    public ValveSummary Valve { get; set; } = new();
}

public class Range { public double Min { get; set; } public double Max { get; set; } }

public class PipeSummary
{
    public Range DiameterRange { get; set; } = new();
    public Dictionary<string, int> Materials { get; set; } = [];
    public int MissingMaterialCount { get; set; }
    public int MissingDiameterCount { get; set; }
}

public class JunctionSummary
{
    public Range ElevationRange { get; set; } = new();
    public double TotalBaseDemand { get; set; }
}

public class TankSummary { public int Count { get; set; } public double TotalCapacity { get; set; } }
public class CountSummary { public int Count { get; set; } }

public class ValveSummary
{
    public int Count { get; set; }
    public Dictionary<string, int> ByType { get; set; } = [];
}

public class NamedElementIndex
{
    public List<NamedElement> Nodes { get; set; } = [];
    public List<NamedElement> Links { get; set; } = [];
}

public class NamedElement
{
    public required string Id { get; set; }
    public required string Type { get; set; }
}
