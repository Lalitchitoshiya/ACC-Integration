using System.Globalization;

namespace Connector.Api.Metadata;

/// <summary>
/// Lightweight geometry-only extraction (id, x, y, type per node; endpoints per link) —
/// separate from ModelMetadata, which only stores aggregate counts (specs/13). Used by
/// the DXF writer (specs/14-cad-visualization.md) to build a real network drawing; never
/// persisted itself.
/// </summary>
public record GraphNode(string Id, double X, double Y, string Type);
public record GraphLink(string UsId, string DsId);
public record NetworkGraph(List<GraphNode> Nodes, List<GraphLink> Links);

public static class NetworkGraphExtractor
{
    private static readonly Dictionary<string, string> NodeTables = new()
    {
        ["wn_node"] = "junction", ["wn_reservoir"] = "tank", ["wn_fixed_head"] = "reservoir",
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
                    nodes.Add(new GraphNode(Get("node_id") ?? "", x, y, nodeType));
            }
            else if (LinkTables.Contains(table))
            {
                var us = Get("us_node_id"); var ds = Get("ds_node_id");
                if (us is not null && ds is not null) links.Add(new GraphLink(us, ds));
            }
        }
        return new NetworkGraph(nodes, links);
    }

    private static NetworkGraph ParseInp(string text)
    {
        var nodes = new List<GraphNode>();
        var links = new List<GraphLink>();
        var types = new Dictionary<string, string>();
        var coords = new Dictionary<string, (double X, double Y)>();
        string section = "";

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
                case "JUNCTIONS": types[f[0]] = "junction"; break;
                case "RESERVOIRS": types[f[0]] = "reservoir"; break;
                case "TANKS": types[f[0]] = "tank"; break;
                case "PIPES" or "PUMPS" or "VALVES":
                    if (f.Length >= 3) links.Add(new GraphLink(f[1], f[2]));
                    break;
                case "COORDINATES":
                    if (f.Length >= 3 &&
                        double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                        double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                        coords[f[0]] = (x, y);
                    break;
            }
        }
        foreach (var (id, type) in types)
            if (coords.TryGetValue(id, out var xy))
                nodes.Add(new GraphNode(id, xy.X, xy.Y, type));
        return new NetworkGraph(nodes, links);
    }
}
