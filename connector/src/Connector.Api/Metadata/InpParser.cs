using System.Globalization;

namespace Connector.Api.Metadata;

/// <summary>
/// EPANET INP parser producing the same metadata shape as the WS Pro CSV parser
/// (specs/13-metadata-schema.md). INP is the neutral water-distribution exchange
/// format (specs/06), so supporting it directly makes plain EPANET files
/// first-class uploads. Sections: whitespace-delimited columns, ';' comments.
/// </summary>
public static class InpParser
{
    public static ModelMetadata Parse(StreamReader reader)
    {
        var meta = new ModelMetadata { SourceTool = "EPANET-INP" };
        meta.Network.Nodes.ByType.Clear();
        meta.Network.Links.ByType.Clear();

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        double minDia = double.MaxValue, maxDia = double.MinValue;
        bool anyXy = false, anyZ = false, anyDia = false;

        string section = "";
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']').ToUpperInvariant();
                continue;
            }

            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // whitespace split
            double? Num(int i) => i < f.Length &&
                double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

            switch (section)
            {
                case "JUNCTIONS":
                    AddNode(meta, f[0], "junction");
                    if (Num(1) is double elev) { anyZ = true; minZ = Math.Min(minZ, elev); maxZ = Math.Max(maxZ, elev); }
                    if (Num(2) is double demand) meta.AttributeSummary.Junction.TotalBaseDemand += demand;
                    break;

                case "RESERVOIRS":
                    AddNode(meta, f[0], "reservoir");
                    break;

                case "TANKS":
                    AddNode(meta, f[0], "tank");
                    meta.AttributeSummary.Tank.Count++;
                    break;

                case "PIPES":
                    AddLink(meta, f[0], "pipe");
                    if (Num(3) is double len) meta.Network.TotalPipeLength += len;
                    if (Num(4) is double dia)
                    {
                        anyDia = true;
                        minDia = Math.Min(minDia, dia); maxDia = Math.Max(maxDia, dia);
                    }
                    else meta.AttributeSummary.Pipe.MissingDiameterCount++;
                    meta.AttributeSummary.Pipe.MissingMaterialCount++; // INP carries no material attribute
                    break;

                case "PUMPS":
                    AddLink(meta, f[0], "pump");
                    meta.AttributeSummary.Pump.Count++;
                    break;

                case "VALVES":
                    AddLink(meta, f[0], "valve");
                    meta.AttributeSummary.Valve.Count++;
                    if (f.Length > 4)
                        meta.AttributeSummary.Valve.ByType[f[4].ToUpperInvariant()] =
                            meta.AttributeSummary.Valve.ByType.GetValueOrDefault(f[4].ToUpperInvariant()) + 1;
                    break;

                case "COORDINATES":
                    if (Num(1) is double x && Num(2) is double y)
                    {
                        anyXy = true;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                    break;
            }
        }

        if (anyXy) { meta.Extent.MinX = minX; meta.Extent.MinY = minY; meta.Extent.MaxX = maxX; meta.Extent.MaxY = maxY; }
        if (anyZ) { meta.AttributeSummary.Junction.ElevationRange.Min = minZ; meta.AttributeSummary.Junction.ElevationRange.Max = maxZ; }
        if (anyDia) { meta.AttributeSummary.Pipe.DiameterRange.Min = minDia; meta.AttributeSummary.Pipe.DiameterRange.Max = maxDia; }

        meta.ParseWarnings.Add("Parsed from EPANET INP: units follow the file's [OPTIONS] Units setting (not read in v1); INP has no pipe material data.");
        return meta;
    }

    private static void AddNode(ModelMetadata m, string id, string type)
    {
        m.Network.Nodes.Total++;
        m.Network.Nodes.ByType[type] = m.Network.Nodes.ByType.GetValueOrDefault(type) + 1;
        m.NamedElementIndex.Nodes.Add(new NamedElement { Id = id, Type = type });
    }

    private static void AddLink(ModelMetadata m, string id, string type)
    {
        m.Network.Links.Total++;
        m.Network.Links.ByType[type] = m.Network.Links.ByType.GetValueOrDefault(type) + 1;
        m.NamedElementIndex.Links.Add(new NamedElement { Id = id, Type = type });
    }
}
