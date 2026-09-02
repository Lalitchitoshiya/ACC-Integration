using System.Globalization;

namespace Connector.Api.Metadata;

/// <summary>
/// Metadata extractor for InfoWorks WS Pro exports produced by
/// plugins/infoworks-wspro/upload_to_acc.rb — sectioned CSV:
///   ## table=wn_pipe
///   table,field1,field2,...
///   wn_pipe,value1,value2,...
///
/// Table mapping validated against a real WS Pro 2026.3 export of EPANET Net3
/// (specs/13-metadata-schema.md spike, closed 2026-08-13). WS Pro naming vs EPANET:
///   wn_node        → junction        wn_reservoir → tank (storage)
///   wn_fixed_head  → reservoir       wn_pipe/wn_pump/wn_valve* → links
/// </summary>
public class WsProMetadataExtractor : IMetadataExtractor
{
    public string SourceTool => "InfoWorksWSPro";

    private static readonly Dictionary<string, string> NodeTables = new()
    {
        ["wn_node"] = "junction",
        ["wn_reservoir"] = "tank",
        ["wn_fixed_head"] = "reservoir",
        ["wn_transfer_node"] = "transferNode",
        ["wn_well"] = "well",
        ["wn_hydrant"] = "hydrant",
    };

    private static readonly Dictionary<string, string> LinkTables = new()
    {
        ["wn_pipe"] = "pipe",
        ["wn_pump"] = "pump",
        ["wn_valve"] = "valve",
        ["wn_float_valve"] = "floatValve",
        ["wn_non_return_valve"] = "nonReturnValve",
        ["wn_pst"] = "pst",
        ["wn_meter"] = "meter",
        ["wn_open_channel"] = "openChannel",
    };

    public async Task<MetadataResult> ExtractAsync(Stream package, string fileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".csv" => new MetadataResult(await ParseAsync(package, ct), null),
                ".inp" => new MetadataResult(InpParser.Parse(new StreamReader(package)), null),
                _ => new MetadataResult(null,
                    $"Unsupported package format '{ext}' — supported: .csv (WS Pro script export), .inp (EPANET).")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never throw for bad input (spec 01 FR1.4): storage proceeds, metadata doesn't.
            return new MetadataResult(null, $"Export parse failed: {ex.Message}");
        }
    }

    private static async Task<ModelMetadata> ParseAsync(Stream package, CancellationToken ct)
    {
        var meta = new ModelMetadata();
        meta.Network.Nodes.ByType.Clear();
        meta.Network.Links.ByType.Clear();

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        double minDia = double.MaxValue, maxDia = double.MinValue;
        bool anyXy = false, anyZ = false, anyDia = false;

        using var reader = new StreamReader(package);
        string? currentTable = null;
        Dictionary<string, int>? cols = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("## table=", StringComparison.Ordinal))
            {
                currentTable = line["## table=".Length..].Trim();
                cols = null; // next line is the header
                continue;
            }
            if (currentTable is null || line.Length == 0) continue;

            if (cols is null)
            {
                var headers = line.Split(',');
                cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < headers.Length; i++) cols.TryAdd(headers[i], i);
                continue;
            }

            var isNode = NodeTables.TryGetValue(currentTable, out var nodeType);
            var isLink = LinkTables.TryGetValue(currentTable, out var linkType);
            if (!isNode && !isLink) continue; // config/polygon/etc. tables don't affect network stats

            var f = line.Split(',');
            string? Get(string name) =>
                cols.TryGetValue(name, out var i) && i < f.Length && f[i].Length > 0 ? f[i] : null;
            double? Num(string name) =>
                double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

            if (isNode)
            {
                meta.Network.Nodes.Total++;
                meta.Network.Nodes.ByType[nodeType!] = meta.Network.Nodes.ByType.GetValueOrDefault(nodeType!) + 1;
                meta.NamedElementIndex.Nodes.Add(new NamedElement
                {
                    Id = Get("node_id") ?? Get("id") ?? $"{currentTable}:{meta.Network.Nodes.Total}",
                    Type = nodeType!
                });

                if (Num("x") is double x && Num("y") is double y)
                {
                    anyXy = true;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                if ((Num("z") ?? Num("ground_level")) is double z)
                {
                    anyZ = true;
                    minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                }
                if (nodeType == "tank") meta.AttributeSummary.Tank.Count++;
            }
            else
            {
                meta.Network.Links.Total++;
                meta.Network.Links.ByType[linkType!] = meta.Network.Links.ByType.GetValueOrDefault(linkType!) + 1;

                var us = Get("us_node_id");
                var ds = Get("ds_node_id");
                var suffix = Get("link_suffix");
                var linkId = Get("id")
                    ?? (us is not null || ds is not null ? $"{us}-{ds}{(suffix is not null ? $".{suffix}" : "")}" : $"{currentTable}:{meta.Network.Links.Total}");
                meta.NamedElementIndex.Links.Add(new NamedElement { Id = linkId, Type = linkType! });

                if (linkType == "pipe")
                {
                    if (Num("length") is double len) meta.Network.TotalPipeLength += len;

                    if (Num("diameter") is double dia)
                    {
                        anyDia = true;
                        minDia = Math.Min(minDia, dia); maxDia = Math.Max(maxDia, dia);
                    }
                    else meta.AttributeSummary.Pipe.MissingDiameterCount++;

                    var material = Get("material");
                    if (material is null) meta.AttributeSummary.Pipe.MissingMaterialCount++;
                    else meta.AttributeSummary.Pipe.Materials[material] =
                        meta.AttributeSummary.Pipe.Materials.GetValueOrDefault(material) + 1;
                }
                else if (linkType == "pump") meta.AttributeSummary.Pump.Count++;
                else if (linkType is "valve" or "floatValve" or "nonReturnValve" or "pst")
                {
                    meta.AttributeSummary.Valve.Count++;
                    meta.AttributeSummary.Valve.ByType[linkType!] =
                        meta.AttributeSummary.Valve.ByType.GetValueOrDefault(linkType!) + 1;
                }
            }
        }

        if (anyXy) { meta.Extent.MinX = minX; meta.Extent.MinY = minY; meta.Extent.MaxX = maxX; meta.Extent.MaxY = maxY; }
        if (anyZ) { meta.AttributeSummary.Junction.ElevationRange.Min = minZ; meta.AttributeSummary.Junction.ElevationRange.Max = maxZ; }
        if (anyDia) { meta.AttributeSummary.Pipe.DiameterRange.Min = minDia; meta.AttributeSummary.Pipe.DiameterRange.Max = maxDia; }

        meta.ParseWarnings.Add("Units/CRS not present in the CSV export — unit fields reflect schema defaults, not the model's actual unit system.");
        if (meta.Network.Nodes.Total == 0 && meta.Network.Links.Total == 0)
            meta.ParseWarnings.Add("No recognized wn_* network tables found in export.");
        return meta;
    }
}
