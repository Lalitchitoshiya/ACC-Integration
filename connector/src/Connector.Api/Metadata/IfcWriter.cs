using System.Globalization;
using System.Numerics;
using System.Text;

namespace Connector.Api.Metadata;

/// <summary>
/// Writes a NetworkGraph as an IFC4 STEP file (specs/14-cad-visualization.md, Track B).
///
/// Structure follows exactly what the FR14.8 spike verified against the live v4
/// translation pipeline (2026-08-27):
///   - Full IfcOwnerHistory chain (the minimal $-everywhere file loaded as EMPTY under
///     the legacy loader; the v4 pipeline + this boilerplate loads correctly)
///   - Spatial tree: IfcProject → IfcSite → IfcBuilding, elements contained in Building
///   - Geometry: plain IfcExtrudedAreaSolid cylinders ('SweptSolid' representation on a
///     'Body' subcontext) — IfcSweptDiskSolid was NOT reliably rendered, extruded solids are
///   - Properties: one Pset per element via IfcRelDefinesByProperties — custom Pset
///     ('Pset_ACCWaterHydraulics') confirmed to surface as a named property group with
///     values in the Viewer's properties panel
///   - Units: project length unit METRE; diameters carry an explicit conversion-based
///     MILLIMETRE unit on the property itself (spike finding: a bare measure displays
///     with the project unit, silently mislabeling mm values as metres)
///
/// The translation job for the produced file MUST use conversionMethod "v4" — the
/// default legacy loader yields an empty model with a success status (spike finding #1).
///
/// Vertical dimension (FR14.13, revised 2026-08-28): X/Y are schematic plan coordinates
/// (not to scale — same as DXF/PNG), while elevations are real metres, so raw elevation
/// as Z would put a ~60-unit-wide network 210 m in the air — geometrically absurd and
/// visually unusable. Instead Z is *relative and exaggerated*: the lowest node sits at
/// Z=0 and the range is scaled to a fraction of the plan extent (standard hydraulic
/// long-section practice). Pipes slope between raised endpoints, so the third dimension
/// is genuinely visible. The TRUE elevation is always carried unscaled in the property
/// set — geometry is indicative, properties remain the source of truth.
/// </summary>
public static class IfcWriter
{
    private const double PipeRadiusScale = 0.0008;  // visual radius (drawing units) per mm of real diameter
    private const double MinPipeRadius = 0.06;
    private const double DefaultPipeRadius = 0.12;  // when diameter unknown
    private const double NodeRadius = 0.30;
    private const double NodeHeight = 0.60;
    // Elevation range is scaled to this fraction of the plan extent — enough relief to
    // read high/low ground at a glance without the model becoming a spike.
    private const double VerticalReliefFraction = 0.25;

    private sealed class Step
    {
        private readonly List<string> _lines = [];
        private int _next = 1;
        public int Add(string def) { _lines.Add($"#{_next}={def};"); return _next++; }
        public string Body => string.Join("\n", _lines);
    }

    private static string Num(double v)
    {
        var s = v.ToString("0.######", CultureInfo.InvariantCulture);
        return s.Contains('.') ? s : s + ".";
    }

    private static string Str(string? s) =>
        s is null ? "$" : "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";

    // IFC compressed GUID: 128 bits → 22 chars of the IFC base64 alphabet, MSB first
    // (first char < 4 by construction). Exact RFC field layout is irrelevant for our
    // purposes — uniqueness + valid alphabet + length are what validators check.
    private const string GuidAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
    private static string NewIfcGuid()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var bi = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[22];
        for (var i = 21; i >= 0; i--) { chars[i] = GuidAlphabet[(int)(bi & 63)]; bi >>= 6; }
        return new string(chars);
    }

    public static byte[]? Render(NetworkGraph graph, string modelName)
    {
        if (graph.Nodes.Count == 0) return null;

        var byId = graph.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var s = new Step();

        // ---- Vertical exaggeration (see class doc) ----
        // Map real elevations onto a Z range proportional to the plan extent, anchored so
        // the lowest node sits at Z=0. Nodes without elevation stay at the base plane.
        var planExtent = Math.Max(
            graph.Nodes.Max(n => n.X) - graph.Nodes.Min(n => n.X),
            graph.Nodes.Max(n => n.Y) - graph.Nodes.Min(n => n.Y));
        var elevations = graph.Nodes.Where(n => n.Elevation is not null).Select(n => n.Elevation!.Value).ToList();
        var minElev = elevations.Count > 0 ? elevations.Min() : 0;
        var elevSpan = elevations.Count > 0 ? elevations.Max() - minElev : 0;
        // Flat network (all nodes same elevation, or none recorded) → no relief, Z stays 0.
        var zScale = elevSpan > 1e-9 && planExtent > 1e-9
            ? planExtent * VerticalReliefFraction / elevSpan
            : 0;
        double ZOf(GraphNode n) => n.Elevation is double e ? (e - minElev) * zScale : 0;

        // ---- Owner history (spike finding #3: required for the file to load) ----
        var person = s.Add("IFCPERSON($,$,'Connector',$,$,$,$,$)");
        var org = s.Add("IFCORGANIZATION($,'ACC Water Connector',$,$,$)");
        var personOrg = s.Add($"IFCPERSONANDORGANIZATION(#{person},#{org},$)");
        var app = s.Add($"IFCAPPLICATION(#{org},'1.0','ACC Water Connector','ACCWATER')");
        var oh = s.Add($"IFCOWNERHISTORY(#{personOrg},#{app},$,.ADDED.,$,$,$,{DateTimeOffset.UtcNow.ToUnixTimeSeconds()})");

        // ---- Units: SI metre project units + a conversion-based millimetre for diameters ----
        var uLen = s.Add("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)");
        var uArea = s.Add("IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.)");
        var uVol = s.Add("IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.)");
        var uAng = s.Add("IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.)");
        var unitAssign = s.Add($"IFCUNITASSIGNMENT((#{uLen},#{uArea},#{uVol},#{uAng}))");

        // ---- Context + Body subcontext ----
        var origin = s.Add("IFCCARTESIANPOINT((0.,0.,0.))");
        var worldPlacement = s.Add($"IFCAXIS2PLACEMENT3D(#{origin},$,$)");
        var ctx = s.Add($"IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,#{worldPlacement},$)");
        var bodyCtx = s.Add($"IFCGEOMETRICREPRESENTATIONSUBCONTEXT('Body','Model',*,*,*,*,#{ctx},$,.MODEL_VIEW.,$)");
        var dirZ = s.Add("IFCDIRECTION((0.,0.,1.))");

        // ---- Spatial tree ----
        var project = s.Add($"IFCPROJECT('{NewIfcGuid()}',#{oh},{Str(modelName)},$,$,$,$,(#{ctx}),#{unitAssign})");
        var sitePlace = s.Add($"IFCLOCALPLACEMENT($,#{worldPlacement})");
        var site = s.Add($"IFCSITE('{NewIfcGuid()}',#{oh},'Network Site',$,$,#{sitePlace},$,$,.ELEMENT.,$,$,$,$,$)");
        s.Add($"IFCRELAGGREGATES('{NewIfcGuid()}',#{oh},$,$,#{project},(#{site}))");
        var bldPlace = s.Add($"IFCLOCALPLACEMENT(#{sitePlace},#{worldPlacement})");
        var building = s.Add($"IFCBUILDING('{NewIfcGuid()}',#{oh},{Str(modelName)},$,$,#{bldPlace},$,$,.ELEMENT.,$,$,$)");
        s.Add($"IFCRELAGGREGATES('{NewIfcGuid()}',#{oh},$,$,#{site},(#{building}))");
        var identityPlace = s.Add($"IFCLOCALPLACEMENT(#{bldPlace},#{worldPlacement})");

        var contained = new List<int>();

        // ---- Helpers ----
        int PropText(string name, string value) =>
            s.Add($"IFCPROPERTYSINGLEVALUE({Str(name)},$,IFCTEXT({Str(value)}),$)");
        int PropLenM(string name, double metres) =>
            s.Add($"IFCPROPERTYSINGLEVALUE({Str(name)},$,IFCLENGTHMEASURE({Num(metres)}),$)");
        void AttachPset(int element, string psetName, List<int> props)
        {
            if (props.Count == 0) return;
            var pset = s.Add($"IFCPROPERTYSET('{NewIfcGuid()}',#{oh},{Str(psetName)},$,({string.Join(",", props.Select(p => $"#{p}"))}))");
            s.Add($"IFCRELDEFINESBYPROPERTIES('{NewIfcGuid()}',#{oh},$,$,(#{element}),#{pset})");
        }

        // Cylinder along an arbitrary segment: circle profile extruded along local Z of a
        // position whose Axis is the segment direction. RefDirection must never be
        // parallel to Axis — computed orthogonally.
        int SolidAlong(double ax, double ay, double az, double bx, double by, double bz, double radius)
        {
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9) { dx = 0; dy = 0; dz = 1; len = 0.01; }
            double ux = dx / len, uy = dy / len, uz = dz / len;
            // orthogonal reference direction
            double rx, ry, rz;
            if (Math.Abs(uz) < 0.9) { rx = -uy; ry = ux; rz = 0; }           // cross(u, Z) rotated — orthogonal in-plane
            else { rx = 1; ry = 0; rz = 0; }
            var rlen = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            rx /= rlen; ry /= rlen; rz /= rlen;

            var pt = s.Add($"IFCCARTESIANPOINT(({Num(ax)},{Num(ay)},{Num(az)}))");
            var axis = s.Add($"IFCDIRECTION(({Num(ux)},{Num(uy)},{Num(uz)}))");
            var refDir = s.Add($"IFCDIRECTION(({Num(rx)},{Num(ry)},{Num(rz)}))");
            var place = s.Add($"IFCAXIS2PLACEMENT3D(#{pt},#{axis},#{refDir})");
            var profile = s.Add($"IFCCIRCLEPROFILEDEF(.AREA.,$,$,{Num(radius)})");
            return s.Add($"IFCEXTRUDEDAREASOLID(#{profile},#{place},#{dirZ},{Num(len)})");
        }

        int ShapeOf(int solid)
        {
            var rep = s.Add($"IFCSHAPEREPRESENTATION(#{bodyCtx},'Body','SweptSolid',(#{solid}))");
            return s.Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{rep}))");
        }

        // ---- Nodes ----
        foreach (var n in graph.Nodes)
        {
            var nz = ZOf(n);
            var solid = SolidAlong(n.X, n.Y, nz - NodeHeight / 2, n.X, n.Y, nz + NodeHeight / 2, NodeRadius);
            var shape = ShapeOf(solid);
            var name = Str($"{char.ToUpperInvariant(n.Type[0])}{n.Type[1..]} {n.Id}");
            var entity = n.Type switch
            {
                "tank" => s.Add($"IFCTANK('{NewIfcGuid()}',#{oh},{name},$,$,#{identityPlace},#{shape},{Str(n.Id)},$)"),
                "reservoir" => s.Add($"IFCTANK('{NewIfcGuid()}',#{oh},{name},'Reservoir (fixed head)',$,#{identityPlace},#{shape},{Str(n.Id)},$)"),
                _ => s.Add($"IFCFLOWFITTING('{NewIfcGuid()}',#{oh},{name},$,$,#{identityPlace},#{shape},{Str(n.Id)})"),
            };
            contained.Add(entity);

            var props = new List<int> { PropText("ElementId", n.Id), PropText("Type", n.Type) };
            if (n.Elevation is double el) props.Add(PropLenM("Elevation", el));
            AttachPset(entity, "Pset_ACCWaterHydraulics", props);
        }

        // ---- Links ----
        foreach (var link in graph.Links)
        {
            if (!byId.TryGetValue(link.UsId, out var a) || !byId.TryGetValue(link.DsId, out var b)) continue;

            var radius = link.Diameter is double diaMm
                ? Math.Max(MinPipeRadius, diaMm * PipeRadiusScale)
                : DefaultPipeRadius;
            // Sloped between the two nodes' exaggerated elevations — SolidAlong already
            // handles arbitrary 3D directions, so pipes tilt with no extra work.
            var solid = SolidAlong(a.X, a.Y, ZOf(a), b.X, b.Y, ZOf(b), radius);
            var shape = ShapeOf(solid);
            var displayId = link.Id ?? $"{link.UsId}-{link.DsId}";
            var name = Str($"Pipe {displayId}");
            // Pumps/valves have their own semantic entities (spec FR14.10). Our extractor
            // does not currently flag link subtype, so v1 emits IfcPipeSegment for all —
            // recorded as a known simplification; revisit when GraphLink carries a type.
            var entity = s.Add($"IFCPIPESEGMENT('{NewIfcGuid()}',#{oh},{name},$,$,#{identityPlace},#{shape},{Str(displayId)},.RIGIDSEGMENT.)");
            contained.Add(entity);

            var props = new List<int> { PropText("ElementId", displayId) };
            if (link.AssetId is not null && link.AssetId != displayId) props.Add(PropText("AssetId", link.AssetId));
            if (link.Length is double lenM) props.Add(PropLenM("Length", lenM));
            // Text, not a length measure: the Viewer normalizes measures to the project
            // unit (a mm-united 355.6 displayed as "0.356 m" — correct but not the
            // display-exact match with WS Pro's "Diameter (mm) 355.6" the spec requires).
            if (link.Diameter is double dMm) props.Add(PropText("Diameter",
                dMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm"));
            if (!string.IsNullOrEmpty(link.Material)) props.Add(PropText("Material", link.Material));
            props.Add(PropText("UpstreamNode", link.UsId));
            props.Add(PropText("DownstreamNode", link.DsId));
            AttachPset(entity, "Pset_ACCWaterHydraulics", props);
        }

        if (contained.Count > 0)
            s.Add($"IFCRELCONTAINEDINSPATIALSTRUCTURE('{NewIfcGuid()}',#{oh},'Network elements',$,({string.Join(",", contained.Select(e => $"#{e}"))}),#{building})");

        var sb = new StringBuilder();
        sb.Append("ISO-10303-21;\nHEADER;\n");
        sb.Append("FILE_DESCRIPTION(('ViewDefinition [ReferenceView_V1.2]'),'2;1');\n");
        sb.Append($"FILE_NAME({Str(modelName + ".ifc")},'{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}',('ACC Water Connector'),('ACC Integration'),'IfcWriter 1.0','ACC Water Connector','');\n");
        sb.Append("FILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n");
        sb.Append(s.Body);
        sb.Append("\nENDSEC;\nEND-ISO-10303-21;\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
