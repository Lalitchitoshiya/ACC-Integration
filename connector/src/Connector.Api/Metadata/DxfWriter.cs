using System.Globalization;
using System.Text;

namespace Connector.Api.Metadata;

/// <summary>
/// Writes a NetworkGraph as a minimal, valid DXF R12 file (specs/14-cad-visualization.md).
/// R12 chosen deliberately: the simplest widely-supported DXF revision, ASCII group-code
/// format, no LWPOLYLINE/modern-entity dependency — every CAD tool and Model Derivative
/// both read it. Unlike the PNG/SVG renderers, coordinates are written RAW (no fit-to-canvas
/// scaling) since DXF consumers zoom-to-extents natively; Y is not inverted (DXF is Y-up,
/// matching the source data's own convention).
///
/// v1 simplification (see spec Open Questions): every node is a CIRCLE, differentiated only
/// by layer/color, not a proper symbol block — real symbol blocks are a later refinement.
/// </summary>
public static class DxfWriter
{
    private static readonly Dictionary<string, (string Layer, int ColorIndex)> NodeStyle = new()
    {
        ["junction"] = ("JUNCTION", 3),   // green
        ["tank"] = ("TANK", 5),           // blue
        ["reservoir"] = ("RESERVOIR", 6), // magenta (closest ACI to our purple)
    };
    private const string PipeLayer = "PIPE";
    private const int PipeColor = 8; // dark grey
    private const double NodeRadius = 0.35; // drawing units — small enough not to dominate real-scale coordinates

    public static byte[]? Render(NetworkGraph graph)
    {
        if (graph.Nodes.Count == 0) return null;

        var byId = graph.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var sb = new StringBuilder();

        void Code(int code, string value) { sb.Append(code).Append('\n').Append(value).Append('\n'); }
        void Num(int code, double value) { Code(code, value.ToString("0.######", CultureInfo.InvariantCulture)); }

        // ---- HEADER ----
        Code(0, "SECTION"); Code(2, "HEADER");
        Code(9, "$ACADVER"); Code(1, "AC1009"); // R12
        Code(0, "ENDSEC");

        // ---- TABLES (layer definitions) ----
        Code(0, "SECTION"); Code(2, "TABLES");
        Code(0, "TABLE"); Code(2, "LAYER"); Code(70, "4");
        foreach (var (layer, color) in NodeStyle.Values.Append((PipeLayer, PipeColor)))
        {
            Code(0, "LAYER"); Code(2, layer); Code(70, "0"); Code(62, color.ToString()); Code(6, "CONTINUOUS");
        }
        Code(0, "ENDTAB");
        Code(0, "ENDSEC");

        // ---- ENTITIES ----
        Code(0, "SECTION"); Code(2, "ENTITIES");

        foreach (var link in graph.Links)
        {
            if (!byId.TryGetValue(link.UsId, out var a) || !byId.TryGetValue(link.DsId, out var b)) continue;
            Code(0, "LINE"); Code(8, PipeLayer);
            Num(10, a.X); Num(20, a.Y); Num(30, 0);
            Num(11, b.X); Num(21, b.Y); Num(31, 0);
        }

        foreach (var n in graph.Nodes)
        {
            var (layer, _) = NodeStyle.TryGetValue(n.Type, out var style) ? style : ("JUNCTION", 3);
            Code(0, "CIRCLE"); Code(8, layer);
            Num(10, n.X); Num(20, n.Y); Num(30, 0);
            Num(40, NodeRadius);
        }

        Code(0, "ENDSEC");
        Code(0, "EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
