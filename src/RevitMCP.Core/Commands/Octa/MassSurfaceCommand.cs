using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Survey-exact context massing: walls from the footprint down to baseZ, and a roof surface
/// triangulated THROUGH the surveyed roof points (gutter / parapet / ridge RLs), so every surveyed
/// level lies exactly on the model. Params (metres, internal): outline [[x,y,z]] (footprint
/// vertices with their eave/parapet RL), roofPoints? [[x,y,z]] (interior, e.g. ridges), baseZ,
/// name, category? "Mass", phase? "Existing", comments?.
/// </summary>
public sealed class CreateMassSurfaceCommand : IRevitCommand
{
    public string Name => "create_mass_surface";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var ft = P.MetersToFeet;
        List<XYZ> Read(string key) => (p[key] as JsonArray ?? new JsonArray()).Select(n => (JsonArray)n!)
            .Select(a => new XYZ(P.DblFrom(a[0], "x") * ft, P.DblFrom(a[1], "y") * ft, P.DblFrom(a[2], "z") * ft)).ToList();
        var outline = Read("outline");
        if (outline.Count > 1 && Flat(outline[0]).DistanceTo(Flat(outline[^1])) < 1e-6) outline.RemoveAt(outline.Count - 1);
        if (outline.Count < 3) throw new RevitCommandException("invalid_parameter", "outline needs at least 3 points.");
        if (SignedArea(outline) < 0) outline.Reverse();
        var poly2 = outline.Select(Flat).ToList();
        var interior = Read("roofPoints").Where(q => Inside(poly2, q)).ToList();
        var baseZ = P.Dbl(p, "baseZ") * ft;

        // Roof: Delaunay over boundary + interior points, keeping triangles inside the footprint.
        var all = outline.Concat(interior).ToList();
        var tris = Delaunay(all).Where(t =>
        {
            var c = (Flat(all[t.a]) + Flat(all[t.b]) + Flat(all[t.c])) / 3;
            return Inside(poly2, c);
        }).ToList();

        var b = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.AnyGeometry, Fallback = TessellatedShapeBuilderFallback.Mesh, GraphicsStyleId = ElementId.InvalidElementId };
        b.OpenConnectedFaceSet(false);
        foreach (var (a, c2, d) in tris)
        {
            var v = new List<XYZ> { all[a], all[c2], all[d] };
            if (((v[1] - v[0]).CrossProduct(v[2] - v[0])).Z < 0) v.Reverse();   // roof faces point up
            b.AddFace(new TessellatedFace(v, ElementId.InvalidElementId));
        }
        for (int i = 0; i < outline.Count; i++)
        {
            var q0 = outline[i]; var q1 = outline[(i + 1) % outline.Count];
            var wall = new List<XYZ> { new(q0.X, q0.Y, baseZ), new(q1.X, q1.Y, baseZ), q1, q0 };
            b.AddFace(new TessellatedFace(wall, ElementId.InvalidElementId));
        }
        b.AddFace(new TessellatedFace(outline.Select(q => new XYZ(q.X, q.Y, baseZ)).Reverse().ToList(), ElementId.InvalidElementId));
        b.CloseConnectedFaceSet();
        b.Build();
        var shape = b.GetBuildResult();

        var catName = P.StrOrNull(p, "category") ?? "Mass";
        var bic = catName.Equals("Generic Models", StringComparison.OrdinalIgnoreCase) ? BuiltInCategory.OST_GenericModel : BuiltInCategory.OST_Mass;
        var ds = DirectShape.CreateElement(doc, new ElementId(bic));
        ds.SetShape(shape.GetGeometricalObjects());
        ds.Name = P.StrOrNull(p, "name") ?? "Context mass";
        var phaseName = P.StrOrNull(p, "phase") ?? "Existing";
        var phase = doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(phaseName, StringComparison.OrdinalIgnoreCase));
        if (phase is not null) ds.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
        ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(P.StrOrNull(p, "comments") ?? ds.Name);
        return new JsonObject
        {
            ["affected"] = Affected.Created(ds.Id.Value), ["id"] = ds.Id.Value, ["name"] = ds.Name,
            ["triangles"] = tris.Count, ["roofPointsUsed"] = interior.Count, ["outcome"] = shape.Outcome.ToString(),
        };
    }

    private static XYZ Flat(XYZ q) => new(q.X, q.Y, 0);

    private static double SignedArea(IList<XYZ> pts)
    {
        double s = 0;
        for (int i = 0; i < pts.Count; i++) { var a = pts[i]; var c = pts[(i + 1) % pts.Count]; s += a.X * c.Y - c.X * a.Y; }
        return s / 2;
    }

    private static bool Inside(IList<XYZ> poly, XYZ q)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) &&
                q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) c = !c;
        return c;
    }

    // Bowyer–Watson Delaunay triangulation in XY.
    private static List<(int a, int b, int c)> Delaunay(IList<XYZ> pts)
    {
        double minX = pts.Min(q => q.X), minY = pts.Min(q => q.Y), maxX = pts.Max(q => q.X), maxY = pts.Max(q => q.Y);
        double d = Math.Max(maxX - minX, maxY - minY) * 20;
        var v = pts.Select(q => (q.X, q.Y)).ToList();
        int s0 = v.Count; v.Add((minX - d, minY - d)); v.Add((maxX + d * 2, minY - d)); v.Add((minX - d, maxY + d * 2));
        var tris = new List<(int a, int b, int c)> { (s0, s0 + 1, s0 + 2) };
        for (int i = 0; i < pts.Count; i++)
        {
            var (px, py) = v[i];
            var bad = tris.Where(t => InCircle(v[t.a], v[t.b], v[t.c], px, py)).ToList();
            var edges = new List<(int, int)>();
            foreach (var t in bad)
                foreach (var e in new[] { (t.a, t.b), (t.b, t.c), (t.c, t.a) })
                {
                    var rev = edges.FindIndex(x => x.Item1 == e.Item2 && x.Item2 == e.Item1);
                    if (rev >= 0) edges.RemoveAt(rev); else edges.Add(e);
                }
            tris.RemoveAll(t => bad.Contains(t));
            foreach (var (ea, eb) in edges) tris.Add((ea, eb, i));
        }
        return tris.Where(t => t.a < s0 && t.b < s0 && t.c < s0).ToList();
    }

    private static bool InCircle((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, double px, double py)
    {
        // orientation-independent circumcircle test
        double ax = a.X - px, ay = a.Y - py, bx = b.X - px, by = b.Y - py, cx = c.X - px, cy = c.Y - py;
        double det = (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay);
        double orient = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return orient > 0 ? det > 1e-12 : det < -1e-12;
    }
}
