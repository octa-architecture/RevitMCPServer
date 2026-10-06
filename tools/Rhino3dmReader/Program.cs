// Rhino3dmReader - read Rhino .3dm files without Rhino (McNeel rhino3dm / openNURBS).
// Modes:
//   inventory <file> --out <json>
//   export    <file> [--layer <substr>] [--name <substr>] [--block <substr>] [--id <guid prefixes,>] [--bbox x0,y0,z0,x1,y1,z1]
//             [--xform 16 row-major values applied to output] [--max N] [--out <json>]
//   clusters  <file> [--layer <substr>] [--cell <model units>] [--out <json>]   (spatial copies + offsets)
using System.Diagnostics;
using System.Text.Json;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.DocObjects;

var a = args.ToList();
if (a.Count < 2) { Console.Error.WriteLine("usage: inventory|export|clusters <file.3dm> [options]"); return 1; }
string Opt(string k, string? d = null) { int i = a.IndexOf(k); return i >= 0 && i + 1 < a.Count ? a[i + 1] : d!; }
var sw = Stopwatch.StartNew();
var f = File3dm.Read(a[1]) ?? throw new Exception("File3dm.Read returned null");
Console.Error.WriteLine($"read {a[1]} in {sw.Elapsed.TotalSeconds:F1}s");
var M = new Model(f);
object result = a[0] switch
{
    "inventory" => M.Inventory(),
    "export" => M.Export(Opt("--layer"), Opt("--name"), Opt("--block"), Opt("--bbox"), int.Parse(Opt("--max", "500")), Opt("--xform"), Opt("--id")),
    "clusters" => M.Clusters(Opt("--layer"), Opt("--cell") is string c ? double.Parse(c) : -1),
    _ => throw new Exception("unknown mode " + a[0])
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
if (Opt("--out") is string o) { File.WriteAllText(o, json); Console.Error.WriteLine($"wrote {o} ({json.Length / 1024} KB) total {sw.Elapsed.TotalSeconds:F1}s"); }
else Console.WriteLine(json);
return 0;

class Model
{
    readonly File3dm f;
    readonly List<File3dmObject> objs = new();
    readonly Dictionary<Guid, File3dmObject> byId = new();
    readonly Dictionary<Guid, InstanceDefinitionGeometry> idefs = new();
    readonly Dictionary<int, string> layerPath = new();
    readonly Dictionary<Guid, BoundingBox> idefBox = new();

    public Model(File3dm file)
    {
        f = file;
        foreach (var o in f.Objects) { objs.Add(o); byId[o.Id] = o; }
        foreach (var d in f.AllInstanceDefinitions) idefs[d.Id] = d;
        foreach (var l in f.AllLayers) layerPath[l.Index] = l.FullPath;
    }

    static double R(double v) => Math.Round(v, 4);
    static double[]? B(BoundingBox b) => b.IsValid ? new[] { R(b.Min.X), R(b.Min.Y), R(b.Min.Z), R(b.Max.X), R(b.Max.Y), R(b.Max.Z) } : null;
    static double[]? Dims(BoundingBox b) => b.IsValid ? new[] { R(b.Max.X - b.Min.X), R(b.Max.Y - b.Min.Y), R(b.Max.Z - b.Min.Z) } : null;
    static double[] X(Transform t) => Enumerable.Range(0, 16).Select(i => Math.Round(t[i / 4, i % 4], 9)).ToArray();
    string LP(File3dmObject o) => layerPath.TryGetValue(o.Attributes.LayerIndex, out var p) ? p : $"#{o.Attributes.LayerIndex}";
    string IdefName(Guid id) => idefs.TryGetValue(id, out var d) ? d.Name : id.ToString();

    // world bbox of a geometry, recursing into block instances
    BoundingBox Box(GeometryBase g, Transform t, int depth = 0)
    {
        if (g is InstanceReferenceGeometry ir) return depth > 8 ? BoundingBox.Empty : IdefBox(ir.ParentIdefId, t * ir.Xform, depth + 1);
        try { return t.IsIdentity ? g.GetBoundingBox(true) : g.GetBoundingBox(t); } catch { return BoundingBox.Empty; }
    }
    BoundingBox IdefBox(Guid id, Transform t, int depth = 0)
    {
        var bb = BoundingBox.Empty;
        if (!idefs.TryGetValue(id, out var d)) return bb;
        if (t.IsIdentity && idefBox.TryGetValue(id, out var c)) return c;
        foreach (var oid in d.GetObjectIds())
            if (byId.TryGetValue(oid, out var o) && o.Geometry != null) bb.Union(Box(o.Geometry, t, depth));
        if (t.IsIdentity) idefBox[id] = bb;
        return bb;
    }
    IEnumerable<File3dmObject> Top() => objs.Where(o => !o.Attributes.IsInstanceDefinitionObject);

    public object Inventory()
    {
        var s = f.Settings;
        var layers = new Dictionary<int, (int n, BoundingBox bb, Dictionary<string, int> types, Dictionary<string, int> names)>();
        var typeCounts = new Dictionary<string, int>();
        var texts = new List<object>(); var clouds = new List<object>();
        var inst = new Dictionary<Guid, List<object>>();
        foreach (var o in Top())
        {
            var g = o.Geometry; if (g == null) continue;
            var tn = g is InstanceReferenceGeometry ? "InstanceReference" : g.ObjectType.ToString();
            typeCounts[tn] = typeCounts.GetValueOrDefault(tn) + 1;
            var li = o.Attributes.LayerIndex;
            if (!layers.TryGetValue(li, out var L)) L = (0, BoundingBox.Empty, new(), new());
            var bb = Box(g, Transform.Identity); L.bb.Union(bb); L.n++;
            L.types[tn] = L.types.GetValueOrDefault(tn) + 1;
            var nm = g is InstanceReferenceGeometry ir0 ? "[blk] " + IdefName(ir0.ParentIdefId) : (o.Name ?? "");
            if (nm != "") L.names[nm] = L.names.GetValueOrDefault(nm) + 1;
            layers[li] = L;
            if (g is PointCloud pcl) clouds.Add(new { id = o.Id, layer = LP(o), points = pcl.Count, hasColors = pcl.ContainsColors, bbox = B(bb) });
            if (g is AnnotationBase an) texts.Add(new { text = an.PlainText, type = tn, layer = LP(o), at = B(bb) });
            else if (g is TextDot td) texts.Add(new { text = td.Text, type = tn, layer = LP(o), at = new[] { R(td.Point.X), R(td.Point.Y), R(td.Point.Z) } });
            if (g is InstanceReferenceGeometry ir)
            {
                if (!inst.TryGetValue(ir.ParentIdefId, out var lst)) inst[ir.ParentIdefId] = lst = new();
                var t = ir.Xform;
                lst.Add(new { id = o.Id, name = o.Name, layer = LP(o), translation = new[] { R(t.M03), R(t.M13), R(t.M23) }, xform = X(t), bbox = B(bb) });
            }
        }
        // nested references inside definitions
        var nested = new Dictionary<Guid, int>();
        foreach (var o in objs.Where(o => o.Attributes.IsInstanceDefinitionObject && o.Geometry is InstanceReferenceGeometry))
            nested[((InstanceReferenceGeometry)o.Geometry).ParentIdefId] = nested.GetValueOrDefault(((InstanceReferenceGeometry)o.Geometry).ParentIdefId) + 1;
        return new
        {
            file = f.ToString(),
            archiveVersion = f.ArchiveVersion,
            appName = f.ApplicationName,
            created = f.Created, lastEdited = f.LastEdited, createdBy = f.CreatedBy, lastEditedBy = f.LastEditedBy,
            units = s.ModelUnitSystem.ToString(),
            absTolerance = s.ModelAbsoluteTolerance,
            angleTolerance = s.ModelAngleToleranceDegrees,
            modelBasepoint = new[] { s.ModelBasepoint.X, s.ModelBasepoint.Y, s.ModelBasepoint.Z },
            objectCount = objs.Count,
            topLevelObjects = objs.Count(o => !o.Attributes.IsInstanceDefinitionObject),
            typeCounts = typeCounts.OrderByDescending(k => k.Value).ToDictionary(),
            layers = f.AllLayers.OrderBy(l => l.FullPath).Select(l =>
            {
                layers.TryGetValue(l.Index, out var L);
                return new { path = l.FullPath, index = l.Index, visible = l.IsVisible, locked = l.IsLocked, count = L.n, bbox = L.n > 0 ? B(L.bb) : null, types = L.types, names = L.names?.OrderByDescending(k => k.Value).Take(25).ToDictionary() };
            }).ToList(),
            blockDefinitions = f.AllInstanceDefinitions.Select(d =>
            {
                var ids = d.GetObjectIds(); var bb = IdefBox(d.Id, Transform.Identity);
                var dl = ids.Where(byId.ContainsKey).Select(i => LP(byId[i])).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                var dt = ids.Where(byId.ContainsKey).Select(i => byId[i].Geometry?.ObjectType.ToString() ?? "null").GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                return new { d.Name, d.Id, d.Description, d.SourceArchive, objectCount = ids.Length, missing = ids.Count(i => !byId.ContainsKey(i)), bbox = B(bb), dims = Dims(bb), layers = dl, types = dt, topLevelInstances = inst.TryGetValue(d.Id, out var li) ? li.Count : 0, nestedRefs = nested.GetValueOrDefault(d.Id) };
            }).OrderByDescending(x => x.objectCount).ToList(),
            blockInstances = inst.Select(kv => new { definition = IdefName(kv.Key), count = kv.Value.Count, instances = kv.Value.Take(200) }).OrderByDescending(x => x.count).ToList(),
            namedViews = f.AllNamedViews.Select(v => new { v.Name, cam = P(v.Viewport.CameraLocation), dir = V(v.Viewport.CameraDirection), target = P(v.Viewport.TargetPoint), persp = v.Viewport.IsPerspectiveProjection }).ToList(),
            views = f.AllViews.Select(v => new { v.Name, cam = P(v.Viewport.CameraLocation), target = P(v.Viewport.TargetPoint) }).ToList(),
            pointClouds = clouds,
            texts
        };
    }
    static double[] P(Point3d p) => new[] { R(p.X), R(p.Y), R(p.Z) };
    static double[] V(Vector3d p) => new[] { R(p.X), R(p.Y), R(p.Z) };

    static BoundingBox ParseBox(string s) { var v = s.Split(',').Select(double.Parse).ToArray(); return new BoundingBox(v[0], v[1], v[2], v[3], v[4], v[5]); }

    public object Export(string? layer, string? name, string? block, string? bbox, int max, string? xform = null, string? ids = null)
    {
        // --xform: 16 comma-separated row-major values applied to output geometry (e.g. map a copy to survey coords); --id: comma-separated object id prefixes
        var T = Transform.Identity; if (xform != null) { var v = xform.Split(',').Select(double.Parse).ToArray(); for (int i = 0; i < 16; i++) T[i / 4, i % 4] = v[i]; }
        var idList = ids?.Split(',');
        var filt = bbox != null ? ParseBox(bbox) : BoundingBox.Empty;
        var outl = new List<object>(); int matched = 0;
        foreach (var o in Top())
        {
            var g = o.Geometry; if (g == null) continue;
            if (idList != null && !idList.Any(p => o.Id.ToString().StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (layer != null && !LP(o).Contains(layer, StringComparison.OrdinalIgnoreCase)) continue;
            if (name != null && !(o.Name ?? "").Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (block != null && !(g is InstanceReferenceGeometry r && IdefName(r.ParentIdefId).Contains(block, StringComparison.OrdinalIgnoreCase))) continue;
            var bb = Box(g, Transform.Identity);
            if (filt.IsValid && (!bb.IsValid || bb.Max.X < filt.Min.X || bb.Min.X > filt.Max.X || bb.Max.Y < filt.Min.Y || bb.Min.Y > filt.Max.Y || bb.Max.Z < filt.Min.Z || bb.Min.Z > filt.Max.Z)) continue;
            matched++;
            if (outl.Count < max) outl.Add(Obj(o, T, 0));
        }
        return new { units = f.Settings.ModelUnitSystem.ToString(), appliedXform = xform, matched, exported = outl.Count, note = "Breps/extrusions: rhino3dm cannot tessellate; 'renderMesh' is the cached render mesh saved in the file if present.", objects = outl };
    }

    object Obj(File3dmObject o, Transform t, int depth)
    {
        var g = o.Geometry; var bb = Box(g, t);
        var d = new Dictionary<string, object?> { ["id"] = o.Id, ["type"] = g is InstanceReferenceGeometry ? "InstanceReference" : g.ObjectType.ToString(), ["layer"] = LP(o), ["name"] = o.Name, ["bbox"] = B(bb), ["dims"] = Dims(bb) };
        try
        {
            switch (g)
            {
                case InstanceReferenceGeometry ir:
                    var t2 = t * ir.Xform;
                    d["definition"] = IdefName(ir.ParentIdefId); d["xform"] = X(ir.Xform); d["worldXform"] = X(t2);
                    if (depth < 6 && idefs.TryGetValue(ir.ParentIdefId, out var def))
                        d["children"] = def.GetObjectIds().Where(byId.ContainsKey).Select(i => Obj(byId[i], t2, depth + 1)).ToList();
                    break;
                case Mesh m: d["mesh"] = MeshJ(m, t); break;
                case Extrusion e: { var m2 = e.GetMesh(MeshType.Render) ?? e.GetMesh(MeshType.Any); d["renderMesh"] = m2 != null ? MeshJ(m2, t) : null; d["profileCount"] = e.ProfileCount; d["path"] = new[] { P(e.PathStart), P(e.PathEnd) }; break; }
                case Brep b:
                    {
                        var jm = new Mesh(); int fm = 0;
                        foreach (var fc in b.Faces) { var fmsh = fc.GetMesh(MeshType.Render) ?? fc.GetMesh(MeshType.Any); if (fmsh != null) { jm.Append(fmsh); fm++; } }
                        d["faces"] = b.Faces.Count; d["facesWithRenderMesh"] = fm; d["renderMesh"] = fm > 0 ? MeshJ(jm, t) : null; d["isSolid"] = b.IsSolid; break;
                    }
                case Curve c: d["polyline"] = Poly(c, t); d["closed"] = c.IsClosed; d["length"] = R(Len(c)); break;
                case PointCloud pc:
                    {
                        d["pointCount"] = pc.Count; int step = Math.Max(1, pc.Count / 2000); var sp = new List<double[]>();
                        for (int i = 0; i < pc.Count; i += step) { var p = pc.PointAt(i); p.Transform(t); sp.Add(P(p)); }
                        d["samplePoints"] = sp; d["sampleStep"] = step; break;
                    }
                case Rhino.Geometry.Point pt: { var p = pt.Location; p.Transform(t); d["point"] = P(p); break; }
                case AnnotationBase an: d["text"] = an.PlainText; break;
                case TextDot td: d["text"] = td.Text; break;
            }
        }
        catch (Exception ex) { d["error"] = ex.Message; }
        return d;
    }
    static object MeshJ(Mesh m, Transform t)
    {
        var v = new List<double[]>(m.Vertices.Count);
        for (int i = 0; i < m.Vertices.Count; i++) { var p = new Point3d(m.Vertices[i]); p.Transform(t); v.Add(P(p)); }
        var fl = new List<int[]>(m.Faces.Count);
        for (int i = 0; i < m.Faces.Count; i++) { var fc = m.Faces[i]; fl.Add(fc.IsQuad ? new[] { fc.A, fc.B, fc.C, fc.D } : new[] { fc.A, fc.B, fc.C }); }
        return new { vertices = v, faces = fl };
    }
    static double Len(Curve c) { var p = Poly(c, Transform.Identity); double s = 0; for (int i = 1; i < p.Count; i++) s += Math.Sqrt(Math.Pow(p[i][0] - p[i - 1][0], 2) + Math.Pow(p[i][1] - p[i - 1][1], 2) + Math.Pow(p[i][2] - p[i - 1][2], 2)); return s; }
    static List<double[]> Poly(Curve c, Transform t)
    {
        var pts = new List<Point3d>();
        if (c is PolylineCurve pc) for (int i = 0; i < pc.PointCount; i++) pts.Add(pc.Point(i));
        else if (c is LineCurve lc) { pts.Add(lc.PointAtStart); pts.Add(lc.PointAtEnd); }
        else if (c is PolyCurve pl) { for (int s = 0; s < pl.SegmentCount; s++) { var sp = Poly(pl.SegmentCurve(s), Transform.Identity); pts.AddRange(sp.Skip(s > 0 ? 1 : 0).Select(q => new Point3d(q[0], q[1], q[2]))); } }
        else if (c.TryGetPolyline(out var pll)) pts.AddRange(pll);
        else { int n = c.IsLinear() ? 1 : Math.Clamp(c.SpanCount * (c.Degree == 1 ? 1 : 8), 8, 128); var dm = c.Domain; for (int i = 0; i <= n; i++) pts.Add(c.PointAt(dm.ParameterAt((double)i / n))); }
        return pts.Select(p => { p.Transform(t); return P(p); }).ToList();
    }

    // Spatial clustering of top-level objects (grid flood-fill on bbox centres) and offset matching between clusters.
    public object Clusters(string? layer, double cell)
    {
        double us = Rhino.RhinoMath.UnitScale(f.Settings.ModelUnitSystem, Rhino.UnitSystem.Meters);
        if (cell <= 0) cell = 3.0 / us;
        var items = Top().Where(o => o.Geometry != null && (layer == null || LP(o).Contains(layer, StringComparison.OrdinalIgnoreCase)))
            .Select(o => (o, bb: Box(o.Geometry, Transform.Identity))).Where(x => x.bb.IsValid && x.bb.Diagonal.Length * us < 2000).ToList();
        var cells = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < items.Count; i++) { var c = items[i].bb.Center; var k = ((long)Math.Floor(c.X / cell), (long)Math.Floor(c.Y / cell)); if (!cells.TryGetValue(k, out var l)) cells[k] = l = new(); l.Add(i); }
        var seen = new HashSet<(long, long)>(); var clusters = new List<List<int>>();
        foreach (var k in cells.Keys)
        {
            if (!seen.Add(k)) continue;
            var cl = new List<int>(); var q = new Queue<(long, long)>(); q.Enqueue(k);
            while (q.Count > 0) { var c = q.Dequeue(); cl.AddRange(cells[c]); for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) { var n = (c.Item1 + dx, c.Item2 + dy); if (cells.ContainsKey(n) && seen.Add(n)) q.Enqueue(n); } }
            clusters.Add(cl);
        }
        clusters = clusters.Where(c => c.Count >= 20).OrderByDescending(c => c.Count).ToList();
        string Sig(int i) { var x = items[i]; var dd = x.bb.Max - x.bb.Min; double r = 0.01 / us; return $"{x.o.Geometry.ObjectType}|{Math.Round(dd.X / r)}|{Math.Round(dd.Y / r)}|{Math.Round(dd.Z / r)}"; }
        var sigs = clusters.Select(c => c.GroupBy(Sig).ToDictionary(g => g.Key, g => g.Select(i => items[i].bb.Min).ToList())).ToList();
        var res = clusters.Select((c, ci) =>
        {
            var bb = BoundingBox.Empty; foreach (var i in c) bb.Union(items[i].bb);
            var lay = c.GroupBy(i => LP(items[i].o)).OrderByDescending(g => g.Count()).Take(30).ToDictionary(g => g.Key, g => g.Count());
            // offset vs. each larger cluster: vote on translation of uniquely-sized objects
            var matches = new List<object>();
            for (int cj = 0; cj < ci; cj++)
            {
                var votes = new Dictionary<(long, long, long), int>(); int common = 0; double q = 0.05 / us;
                foreach (var kv in sigs[ci]) if (sigs[cj].TryGetValue(kv.Key, out var other) && kv.Value.Count <= 6 && other.Count <= 6)
                    { common++; foreach (var p in kv.Value) foreach (var p2 in other) { var k = ((long)Math.Round((p.X - p2.X) / q), (long)Math.Round((p.Y - p2.Y) / q), (long)Math.Round((p.Z - p2.Z) / q)); votes[k] = votes.GetValueOrDefault(k) + 1; } }
                if (votes.Count == 0) continue;
                var best = votes.OrderByDescending(v => v.Value).First();
                if (best.Value < 5) continue;
                matches.Add(new { vsCluster = cj, offset = new[] { R(best.Key.Item1 * q), R(best.Key.Item2 * q), R(best.Key.Item3 * q) }, votes = best.Value, sharedSizeSignatures = common, signaturesHere = sigs[ci].Count });
            }
            return new { cluster = ci, count = c.Count, bbox = B(bb), dims = Dims(bb), layers = lay, matches };
        }).ToList();
        return new { units = f.Settings.ModelUnitSystem.ToString(), cell, objects = items.Count, clusters = res };
    }
}
