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
public record GraphNode(string Id, double X, double Y, string Type, double? Elevation = null,
    string? AssetId = null);
// Id: the link's own identity — WS Pro pipes have no standalone id field (keyed by
// endpoints + suffix instead), so it's synthesized as "us-ds[.suffix]"; EPANET INP pipes
// have a real id (the first column) and no separate AssetId concept.
//
// Kind drives the IFC entity choice (LinkKind below). Without it every link — pump,
// valve, meter alike — was emitted as IfcPipeSegment and displayed in ACC as "Pipe
// 9-10.1", which is simply the wrong object to a modeller.
//
// DutyHead/PowerConsumption are pump-station fields. They live on the shared record
// rather than a subtype because the graph is a flat DTO consumed by two writers; they
// are null for every other kind.
// CrossSection describes a conduit that isn't round (an open channel's shape plus its
// height x width). When set it replaces the Diameter property, which would otherwise
// report a rectangular channel as though it had a bore.
public record GraphLink(string UsId, string DsId, double? Length = null, double? Diameter = null,
    string? Material = null, string? Id = null, string? AssetId = null,
    string Kind = LinkKind.Pipe, double? DutyHead = null, double? PowerConsumption = null,
    string? CrossSection = null);

/// <summary>Link kinds the writers switch on. String constants, not an enum, to match
/// GraphNode.Type and keep the CSV table map declarative.</summary>
public static class LinkKind
{
    public const string Pipe = "pipe";
    public const string Pump = "pump";
    public const string Valve = "valve";
    public const string Meter = "meter";
    public const string OpenChannel = "openChannel";
}
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
    // wn_pst — the pump *station* — is the pump's link record: it carries us/ds nodes and
    // the geometry. wn_pump is a curve/unit catalogue (id, triplets, nominal_flow, no
    // endpoints, no coordinates) referenced by wn_pst.pumps, so it is deliberately NOT
    // listed here; adding it would only contribute endpoint-less rows.
    // wn_open_channel has the same us/ds/length/asset_id shape as wn_pipe but sizes itself
    // with shape/channel_height/channel_width instead of a diameter — see DiameterOf.
    private static readonly Dictionary<string, string> LinkTables = new()
    {
        ["wn_pipe"] = LinkKind.Pipe,
        ["wn_pst"] = LinkKind.Pump,
        ["wn_valve"] = LinkKind.Valve,
        ["wn_float_valve"] = LinkKind.Valve,
        ["wn_non_return_valve"] = LinkKind.Valve,
        ["wn_meter"] = LinkKind.Meter,
        ["wn_open_channel"] = LinkKind.OpenChannel,
    };

    /// <summary>Larger of two optional values; null only when both are null.</summary>
    private static double? MaxOf(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>Human-readable channel cross-section, e.g. "RECTANGULAR 800 x 1200 mm".
    /// Null when the export carries neither dimension nor shape.</summary>
    private static string? DescribeChannel(string? shape, double? height, double? width)
    {
        var size = height is double h && width is double w
            ? $"{h.ToString("0.#", CultureInfo.InvariantCulture)} x {w.ToString("0.#", CultureInfo.InvariantCulture)} mm"
            : MaxOf(height, width) is double only
                ? $"{only.ToString("0.#", CultureInfo.InvariantCulture)} mm"
                : null;
        return (shape, size) switch
        {
            (null, null) => null,
            (null, _) => size,
            (_, null) => shape,
            _ => $"{shape} {size}",
        };
    }

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

    private static NetworkGraph ParseWsProCsv(string text)
    {
        var nodes = new List<GraphNode>();
        var links = new List<GraphLink>();
        string? table = null;
        Dictionary<string, int>? cols = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## table=", StringComparison.Ordinal))
            {
                table = line["## table=".Length..].Trim();
                cols = null;
                continue;
            }
            if (table is null || line.Length == 0) continue;

            if (cols is null)
            {
                var headers = line.Split(',');
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
                        Num("z") ?? Num("ground_level"), Get("asset_id")));
            }
            else if (LinkTables.TryGetValue(table, out var linkKind))
            {
                var us = Get("us_node_id"); var ds = Get("ds_node_id");
                if (us is not null && ds is not null)
                {
                    var suffix = Get("link_suffix");
                    var linkId = $"{us}-{ds}{(suffix is not null ? $".{suffix}" : "")}";

                    // An open channel has no diameter; it is described by shape plus
                    // channel_height/channel_width. The larger dimension drives the drawn
                    // cylinder so the conduit stays proportionate, while the real
                    // cross-section is published as text rather than a fake bore.
                    var isChannel = linkKind == LinkKind.OpenChannel;
                    var height = Num("channel_height"); var width = Num("channel_width");
                    var diameter = isChannel ? MaxOf(height, width) : Num("diameter");
                    var crossSection = isChannel ? DescribeChannel(Get("shape"), height, width) : null;

                    // wn_pst's length/diameter are pump-station placeholders (WS Pro
                    // exports 1.0 m / 10 mm for a pump with no physical bore). Publishing
                    // them would claim a 1 m pipe that is drawn 27 m long, so they are
                    // dropped in favour of the fields that do describe a pump.
                    var isPump = linkKind == LinkKind.Pump;
                    links.Add(new GraphLink(us, ds,
                        isPump ? null : Num("length"),
                        isPump ? null : diameter,
                        Get("material"), linkId, Get("asset_id"), linkKind,
                        isPump ? Num("duty_head") : null,
                        isPump ? Num("power_consumption") : null,
                        crossSection));
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
        var rawLinks = new List<(string Id, string Us, string Ds, double? Length, double? Diameter, string Kind)>();
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
                    if (f.Length >= 3) rawLinks.Add((f[0], f[1], f[2], Num(f, 3), Num(f, 4), LinkKind.Pipe));
                    break;
                // EPANET pumps carry no length/diameter at all (their behaviour is a
                // curve), and valve diameter sits in column 3 where a pipe's length is —
                // so neither can reuse the PIPES column layout.
                case "PUMPS":
                    if (f.Length >= 3) rawLinks.Add((f[0], f[1], f[2], null, null, LinkKind.Pump));
                    break;
                case "VALVES":
                    // EPANET VALVES: ID Node1 Node2 Diameter Type Setting ...
                    if (f.Length >= 3) rawLinks.Add((f[0], f[1], f[2], null, Num(f, 3), LinkKind.Valve));
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
            Material: null, Id: l.Id, AssetId: null, // INP has no separate Asset ID concept
            Kind: l.Kind)).ToList();

        var nodes = new List<GraphNode>();
        foreach (var (id, type) in types)
            if (coords.TryGetValue(id, out var xy))
                nodes.Add(new GraphNode(id, xy.X, xy.Y, type,
                    rawElevations.TryGetValue(id, out var e) ? ConvertLength(e) : null));
        return new NetworkGraph(nodes, links);
    }
}
