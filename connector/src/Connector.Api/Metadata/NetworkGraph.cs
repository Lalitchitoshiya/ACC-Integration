using System.Globalization;

namespace Connector.Api.Metadata;

/// <summary>
/// Lightweight geometry-only extraction (id, x, y, type per node; endpoints per link) —
/// separate from ModelMetadata, which only stores aggregate counts (specs/13). Used by
/// the DXF writer (specs/14-cad-visualization.md) to build a real network drawing; never
/// persisted itself.
/// </summary>
// Elevation/Length/Diameter/Material carried through for XDATA embedding (specs/14
// CAD visualization follow-up: "Embed real attributes into the DXF") — the DXF's own
// drawing-space distance between node coordinates is NOT the real pipe length (EPANET/
// WS Pro plan coordinates are schematic, not true-to-scale), so the real values must be
// attached as data, not inferred from geometry.
/// <summary>
/// One WS Pro column carried straight through to the IFC property set.
///
/// The value stays the exact string the exporter wrote — no parsing, rounding or
/// reformatting — so a number never loses precision on the way to ACC and an
/// unrecognised field is still preserved rather than dropped.
/// </summary>
public readonly record struct GraphProperty(string Name, string Value);

// Properties is the open-ended half of the model: WS Pro tables carry 45–69 non-flag
// columns each, and which of them a given model populates varies enormously (an
// EPANET-derived network fills 6 node columns; a real utility export fills far more).
// Naming a field per property would mean guessing which ones matter and silently
// dropping the rest, so everything populated is carried and the writer decides how to
// present it. Adding a WS Pro field then needs no code change here at all.
public record GraphNode(string Id, double X, double Y, string Type, double? Elevation = null,
    string? AssetId = null, IReadOnlyList<GraphProperty>? Properties = null);
// Id: the link's own identity — WS Pro pipes have no standalone id field (keyed by
// endpoints + suffix instead), so it's synthesized as "us-ds[.suffix]"; EPANET INP pipes
// have a real id (the first column) and no separate AssetId concept.
public record GraphLink(string UsId, string DsId, double? Length = null, double? Diameter = null,
    string? Material = null, string? Id = null, string? AssetId = null,
    IReadOnlyList<GraphProperty>? Properties = null);
public record NetworkGraph(List<GraphNode> Nodes, List<GraphLink> Links);

public static class NetworkGraphExtractor
{
    // Must stay in step with WsProMetadataExtractor.NodeTables: the dashboard counted
    // transfer nodes/wells/hydrants while the geometry path silently dropped them, so a
    // model could report 36 nodes and draw 35. Worse, dropping a node also drops every
    // link attached to it (see the byId lookup in IfcWriter), so one unmapped table can
    // remove pipes from the drawing too. Only "tank" and "reservoir" map to a bespoke
    // IFC entity (IfcTank); every other type falls through to IfcFlowFitting, which is
    // the right generic for a node whose shape we don't model.
    private static readonly Dictionary<string, string> NodeTables = new()
    {
        ["wn_node"] = "junction", ["wn_reservoir"] = "tank", ["wn_fixed_head"] = "reservoir",
        ["wn_transfer_node"] = "transferNode", ["wn_well"] = "well", ["wn_hydrant"] = "hydrant",
    };
    private static readonly HashSet<string> LinkTables = ["wn_pipe", "wn_valve", "wn_float_valve", "wn_non_return_valve", "wn_pst", "wn_meter"];

    /// <summary>Returns null if the format/content isn't recognized — caller skips DXF generation.</summary>
    public static async Task<NetworkGraph?> ExtractAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(ct);

        if (text.Contains("## table="))
            return ParseWsProCsv(text);
        if (text.Contains("[JUNCTIONS]") || text.Contains("[PIPES]"))
            return ParseInp(text);
        return null;
    }

    // Columns the graph already surfaces under a curated name, or that describe geometry
    // rather than the asset. Carrying these in the bag as well would duplicate rows in
    // the ACC property palette ("length" beside "Length"), so they are claimed here and
    // emitted by the writer under their proper names instead.
    private static readonly HashSet<string> CuratedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "table", "node_id", "asset_id", "x", "y", "z",
        "us_node_id", "ds_node_id", "link_suffix",
        "length", "diameter", "material",
        "bends", "spatial_data",
    };

    /// <summary>
    /// Every populated, non-flag, non-curated column of one CSV row, in the order the
    /// exporter wrote them — WS Pro groups related fields together, so that order is
    /// worth keeping for the property palette.
    /// </summary>
    private static List<GraphProperty> BagOf(string[] headers, string[] f)
    {
        var bag = new List<GraphProperty>();
        for (var i = 0; i < headers.Length && i < f.Length; i++)
        {
            var name = headers[i];
            var value = f[i].Trim();
            if (value.Length == 0) continue;
            // WS Pro emits a per-field "<name>_flag" column recording how the value was
            // set (#I = inherited, etc.). That is editor bookkeeping, not asset data.
            if (name.EndsWith("_flag", StringComparison.OrdinalIgnoreCase)) continue;
            if (CuratedColumns.Contains(name)) continue;
            // Nested structures serialise as "#<WSStructure:0x0001ee73e70410>" — a Ruby
            // object reference the exporter's v.to_s could not walk (pump curves,
            // customer points, demand profiles). It is noise, not data; reaching the
            // real contents needs a change in upload_to_acc.rb, not here.
            if (value.StartsWith("#<", StringComparison.Ordinal)) continue;
            bag.Add(new GraphProperty(name, value));
        }
        return bag;
    }

    private static NetworkGraph ParseWsProCsv(string text)
    {
        var nodes = new List<GraphNode>();
        var links = new List<GraphLink>();
        string? table = null;
        Dictionary<string, int>? cols = null;
        string[]? headers = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## table=", StringComparison.Ordinal))
            {
                table = line["## table=".Length..].Trim();
                cols = null;
                headers = null;
                continue;
            }
            if (table is null || line.Length == 0) continue;

            if (cols is null)
            {
                headers = line.Split(',');
                cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < headers.Length; i++) cols.TryAdd(headers[i], i);
                continue;
            }

            var f = line.Split(',');
            string? Get(string name) => cols.TryGetValue(name, out var i) && i < f.Length && f[i].Length > 0 ? f[i] : null;
            double? Num(string name) => double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

            if (NodeTables.TryGetValue(table, out var nodeType))
            {
                if (Num("x") is double x && Num("y") is double y)
                    nodes.Add(new GraphNode(Get("node_id") ?? "", x, y, nodeType,
                        Num("z") ?? Num("ground_level"), Get("asset_id"),
                        BagOf(headers!, f)));
            }
            else if (LinkTables.Contains(table))
            {
                var us = Get("us_node_id"); var ds = Get("ds_node_id");
                if (us is not null && ds is not null)
                {
                    var suffix = Get("link_suffix");
                    var linkId = $"{us}-{ds}{(suffix is not null ? $".{suffix}" : "")}";
                    links.Add(new GraphLink(us, ds, Num("length"), Num("diameter"), Get("material"),
                        linkId, Get("asset_id"), BagOf(headers!, f)));
                }
            }
        }
        return new NetworkGraph(nodes, links);
    }

    // EPANET's flow-unit choice implicitly sets the whole unit system (there's no
    // separate length/diameter setting): these 5 flow units mean US Customary (length
    // in feet, diameter in inches); the other 5 (LPS, LPM, MLD, CMH, CMD) mean SI
    // (length in meters, diameter already in millimeters — no conversion needed).
    // This is exactly the gap that made WS Pro (1609.34 m, correctly converted) and the
    // ACC DXF label (previously "5280m" — actually feet, never converted) disagree.
    // Flagged as a known limitation in specs/13-metadata-schema.md parseWarnings before
    // it became visible in the drawn labels; fixed here for the DXF/graph path.
    private static readonly HashSet<string> UsCustomaryFlowUnits = new(StringComparer.OrdinalIgnoreCase)
        { "CFS", "GPM", "MGD", "IMGD", "AFD" };
    private const double FeetToMeters = 0.3048;
    private const double InchesToMm = 25.4;

    private static NetworkGraph ParseInp(string text)
    {
        var rawLinks = new List<(string Id, string Us, string Ds, double? Length, double? Diameter)>();
        var types = new Dictionary<string, string>();
        var rawElevations = new Dictionary<string, double>();
        var coords = new Dictionary<string, (double X, double Y)>();
        string section = "";
        string? unitsToken = null;

        double? Num(string[] f, int i) =>
            i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split(';')[0].TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']').ToUpperInvariant();
                continue;
            }
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (section)
            {
                case "OPTIONS":
                    if (f.Length >= 2 && f[0].Equals("Units", StringComparison.OrdinalIgnoreCase))
                        unitsToken = f[1];
                    break;
                // All three node sections carry a height in column 1, but they mean
                // subtly different things in EPANET: junctions/tanks give ground
                // elevation, reservoirs give total head (water surface). Treated
                // uniformly as "elevation" here — without them tanks and reservoirs
                // had no height at all and sank to the base plane in the 3D view,
                // inverting the real network profile (tank is usually the high point).
                case "JUNCTIONS":
                    types[f[0]] = "junction";
                    if (Num(f, 1) is double jElev) rawElevations[f[0]] = jElev;
                    break;
                case "RESERVOIRS":
                    types[f[0]] = "reservoir";
                    if (Num(f, 1) is double rHead) rawElevations[f[0]] = rHead;
                    break;
                case "TANKS":
                    types[f[0]] = "tank";
                    if (Num(f, 1) is double tElev) rawElevations[f[0]] = tElev;
                    break;
                case "PIPES":
                    // EPANET PIPES: ID Node1 Node2 Length Diameter Roughness ...
                    if (f.Length >= 3) rawLinks.Add((f[0], f[1], f[2], Num(f, 3), Num(f, 4)));
                    break;
                case "PUMPS" or "VALVES":
                    if (f.Length >= 3) rawLinks.Add((f[0], f[1], f[2], null, null));
                    break;
                case "COORDINATES":
                    if (f.Length >= 3 &&
                        double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                        double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                        coords[f[0]] = (x, y);
                    break;
            }
        }

        // [OPTIONS] can appear anywhere in the file (often after PIPES/JUNCTIONS in
        // practice), so conversion is applied here, once, after the full scan.
        var isUsUnits = unitsToken is not null && UsCustomaryFlowUnits.Contains(unitsToken);
        double ConvertLength(double v) => isUsUnits ? v * FeetToMeters : v;
        double ConvertDiameter(double v) => isUsUnits ? v * InchesToMm : v;

        var links = rawLinks.Select(l => new GraphLink(l.Us, l.Ds,
            l.Length is double len ? ConvertLength(len) : null,
            l.Diameter is double dia ? ConvertDiameter(dia) : null,
            Material: null, Id: l.Id, AssetId: null)).ToList(); // INP has no separate Asset ID concept

        var nodes = new List<GraphNode>();
        foreach (var (id, type) in types)
            if (coords.TryGetValue(id, out var xy))
                nodes.Add(new GraphNode(id, xy.X, xy.Y, type,
                    rawElevations.TryGetValue(id, out var e) ? ConvertLength(e) : null));
        return new NetworkGraph(nodes, links);
    }
}
