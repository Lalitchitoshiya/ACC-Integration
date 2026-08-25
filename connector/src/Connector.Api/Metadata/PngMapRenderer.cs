using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Connector.Api.Metadata;

/// <summary>
/// Server-side raster rendering of a NetworkGraph, mirroring the dashboard's SVG map
/// (wwwroot/index.html viewNetwork) so the two look the same. Windows-only (System.Drawing.Common) —
/// acceptable since the connector currently runs on Windows; would need SkiaSharp to
/// go cross-platform later.
/// </summary>
public static class PngMapRenderer
{
    public static byte[]? Render(NetworkGraph graph, string title)
    {
        if (graph.Nodes.Count == 0) return null;

        const int w = 900, h = 650, pad = 50;
        var minX = graph.Nodes.Min(n => n.X); var maxX = graph.Nodes.Max(n => n.X);
        var minY = graph.Nodes.Min(n => n.Y); var maxY = graph.Nodes.Max(n => n.Y);
        var spanX = Math.Max(maxX - minX, 0.0001); var spanY = Math.Max(maxY - minY, 0.0001);
        var scale = Math.Min((w - 2.0 * pad) / spanX, (h - 2.0 * pad) / spanY);

        float X(double x) => (float)(pad + (x - minX) * scale);
        float Y(double y) => (float)(h - pad - (y - minY) * scale); // invert: screen Y grows downward

        var byId = graph.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());

        using var bmp = new Bitmap(w, h);
        using var g2 = Graphics.FromImage(bmp);
        g2.SmoothingMode = SmoothingMode.AntiAlias;
        g2.Clear(ColorTranslator.FromHtml("#f8fafc"));

        using var pipePen = new Pen(ColorTranslator.FromHtml("#94a3b8"), 1.5f);
        using var pumpPen = new Pen(ColorTranslator.FromHtml("#f59e0b"), 2.5f);
        foreach (var link in graph.Links)
        {
            if (!byId.TryGetValue(link.UsId, out var a) || !byId.TryGetValue(link.DsId, out var b)) continue;
            g2.DrawLine(pipePen, X(a.X), Y(a.Y), X(b.X), Y(b.Y));
        }

        using var junctionBrush = new SolidBrush(ColorTranslator.FromHtml("#16a34a"));
        using var tankBrush = new SolidBrush(ColorTranslator.FromHtml("#2563eb"));
        using var reservoirBrush = new SolidBrush(ColorTranslator.FromHtml("#7c3aed"));
        foreach (var n in graph.Nodes)
        {
            var cx = X(n.X); var cy = Y(n.Y);
            switch (n.Type)
            {
                case "tank":
                    g2.FillRectangle(tankBrush, cx - 5, cy - 5, 10, 10);
                    break;
                case "reservoir":
                    g2.FillPolygon(reservoirBrush, [new PointF(cx, cy - 6), new PointF(cx - 6, cy + 5), new PointF(cx + 6, cy + 5)]);
                    break;
                default:
                    g2.FillEllipse(junctionBrush, cx - 3.2f, cy - 3.2f, 6.4f, 6.4f);
                    break;
            }
        }

        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        using var textBrush = new SolidBrush(ColorTranslator.FromHtml("#1a2332"));
        g2.DrawString(title, font, textBrush, 12, 10);
        using var statFont = new Font("Segoe UI", 9);
        g2.DrawString($"{graph.Nodes.Count} nodes, {graph.Links.Count} links",
            statFont, textBrush, 12, h - 24);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
