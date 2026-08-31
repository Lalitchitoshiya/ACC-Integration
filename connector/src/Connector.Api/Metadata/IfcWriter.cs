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
/// Vertical dimension (FR14.13, revised twice — final: true scale, 2026-08-28):
/// Z is the real elevation in metres, offset so the lowest node sits at Z=0. No
/// exaggeration factor.
///
/// An earlier revision scaled the elevation range to a fraction of the plan extent
/// (vertical exaggeration). That was abandoned because tanks/reservoirs sit far above
/// the pipe network (Net1: tank +49 m, reservoir +33 m, but all nine junctions within
/// 6 m of each other) — scaling to fit the tank squashed the entire junction network
/// into the bottom ~12% of the view, which read as wrong against WS Pro's GeoPlan.
/// True scale keeps proportions honest and directly comparable: the tank genuinely
/// towers, and junction relief is genuinely subtle because in reality it is.
///
/// The absolute elevation is also carried unmodified in the property set, so geometry
/// and properties agree.
/// </summary>
public static class IfcWriter
{
    private const double PipeRadiusScale = 0.0008;  // visual radius (drawing units) per mm of real diameter
    private const double MinPipeRadius = 0.06;
    private const double DefaultPipeRadius = 0.12;  // when diameter unknown
    private const double NodeRadius = 0.30;
    private const double NodeHeight = 0.60;

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

        // ---- Vertical: true scale, no exaggeration (see class doc) ----
        // Z is the real elevation in metres, offset so the lowest node sits at Z=0
        // (keeps the model on the ground plane rather than floating at absolute datum).
        // Nodes without a recorded elevation stay at the base plane.
        var elevations = graph.Nodes.Where(n => n.Elevation is not null).Select(n => n.Elevation!.Value).ToList();
        var minElev = elevations.Count > 0 ? elevations.Min() : 0;
        double ZOf(GraphNode n) => n.Elevation is double e ? e - minElev : 0;

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
            // Same rule as links: only worth a property when it says something the
            // ElementId doesn't. Without this, nodes carried no asset identity at all,
            // leaving node_id as the only key back to WS Pro.
            if (n.AssetId is not null && n.AssetId != n.Id) props.Add(PropText("AssetId", n.AssetId));
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
            // Semantic entity per link kind (spec FR14.10). Previously every link emitted
            // as IfcPipeSegment, so a pump station read "Pipe 9-10.1" in the ACC viewer.
            // IfcPump/IfcValve/IfcFlowMeter are all IFC4 MEP distribution elements and
            // take the same constructor shape as IfcPipeSegment minus the type enum.
            var entity = link.Kind switch
            {
                LinkKind.Pump =>
                    s.Add($"IFCPUMP('{NewIfcGuid()}',#{oh},{Str($"Pump {displayId}")},$,$,#{identityPlace},#{shape},{Str(displayId)},.NOTDEFINED.)"),
                LinkKind.Valve =>
                    s.Add($"IFCVALVE('{NewIfcGuid()}',#{oh},{Str($"Valve {displayId}")},$,$,#{identityPlace},#{shape},{Str(displayId)},.NOTDEFINED.)"),
                LinkKind.Meter =>
                    s.Add($"IFCFLOWMETER('{NewIfcGuid()}',#{oh},{Str($"Meter {displayId}")},$,$,#{identityPlace},#{shape},{Str(displayId)},.NOTDEFINED.)"),
                // No open-channel entity exists in IFC4; CULVERT is the closest standard
                // predefined type for a conduit that is not a pressurised pipe.
                LinkKind.OpenChannel =>
                    s.Add($"IFCPIPESEGMENT('{NewIfcGuid()}',#{oh},{Str($"Channel {displayId}")},$,$,#{identityPlace},#{shape},{Str(displayId)},.CULVERT.)"),
                _ =>
                    s.Add($"IFCPIPESEGMENT('{NewIfcGuid()}',#{oh},{Str($"Pipe {displayId}")},$,$,#{identityPlace},#{shape},{Str(displayId)},.RIGIDSEGMENT.)"),
            };
            contained.Add(entity);

            var props = new List<int> { PropText("ElementId", displayId), PropText("Type", link.Kind) };
            if (link.AssetId is not null && link.AssetId != displayId) props.Add(PropText("AssetId", link.AssetId));
            if (link.Length is double lenM) props.Add(PropLenM("Length", lenM));
            // Text, not a length measure: the Viewer normalizes measures to the project
            // unit (a mm-united 355.6 displayed as "0.356 m" — correct but not the
            // display-exact match with WS Pro's "Diameter (mm) 355.6" the spec requires).
            // A non-round conduit reports its cross-section instead of a bore it lacks.
            if (link.CrossSection is string xs) props.Add(PropText("CrossSection", xs));
            else if (link.Diameter is double dMm) props.Add(PropText("Diameter",
                dMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm"));
            if (!string.IsNullOrEmpty(link.Material)) props.Add(PropText("Material", link.Material));
            // Pump-station fields, in place of the Length/Diameter a pump doesn't have.
            // Text with an explicit unit for the same reason Diameter is (see above): the
            // Viewer renormalizes real length measures to the project unit.
            if (link.DutyHead is double head) props.Add(PropText("DutyHead",
                head.ToString("0.##", CultureInfo.InvariantCulture) + " m"));
            if (link.PowerConsumption is double kw) props.Add(PropText("PowerConsumption",
                kw.ToString("0.##", CultureInfo.InvariantCulture) + " kW"));
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
