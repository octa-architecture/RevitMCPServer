using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Create a new project from a template, save it, and open it as the active document.
/// Params: template (path .rte), path (new .rvt), overwrite? (default false).
/// </summary>
public sealed class NewProjectFromTemplateCommand : IRevitCommand
{
    public string Name => "new_project_from_template";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var template = P.Str(ctx.Parameters, "template");
        var path = P.Str(ctx.Parameters, "path");
        if (!File.Exists(template)) throw new RevitCommandException("not_found", $"Template not found: {template}");
        if (File.Exists(path) && !P.BoolOr(ctx.Parameters, "overwrite", false))
            throw new RevitCommandException("name_collision", $"{path} already exists. Pass overwrite=true to replace it.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var doc = ctx.App.Application.NewProjectDocument(template)
            ?? throw new RevitCommandException("command_failed", "Revit couldn't create a project from the template.");
        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
        var ui = ctx.App.OpenAndActivateDocument(path);
        return new JsonObject { ["title"] = ui.Document.Title, ["path"] = ui.Document.PathName };
    }
}

/// <summary>
/// Set the project's shared coordinates: internal origin = (eastingM, northingM, elevationM) in the
/// survey grid, optional angle to true north (degrees). Records a datum note in Project Information
/// comments. Params: eastingM, northingM, elevationM? (0), note?, and either siteRotationDeg
/// (preferred: the same CCW angle given to link_cad rotateDeg) or trueNorthDegrees (raw; = -siteRotationDeg).
/// </summary>
public sealed class SetSharedCoordinatesCommand : IRevitCommand
{
    public string Name => "set_shared_coordinates";
    public bool IsReadOnly => false;
    public string RiskLevel => "high";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var ft = P.MetersToFeet;
        // Rotating the survey CCW by θ to square it to the sheet puts true north at -θ in Revit's
        // ProjectPosition convention (verified at Grant St: both TBMs convert back exactly).
        var angleDeg = p["siteRotationDeg"] is not null ? -P.Dbl(p, "siteRotationDeg") : P.DblOr(p, "trueNorthDegrees", 0);
        var pos = new ProjectPosition(P.Dbl(p, "eastingM") * ft, P.Dbl(p, "northingM") * ft,
            P.DblOr(p, "elevationM", 0) * ft, angleDeg * Math.PI / 180);
        doc.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, pos);
        if (P.StrOrNull(p, "note") is { } note)
            doc.ProjectInformation.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(note);
        return new JsonObject
        {
            ["affected"] = Affected.Modified(doc.ActiveProjectLocation.Id.Value),
            ["eastingM"] = P.Dbl(p, "eastingM"), ["northingM"] = P.Dbl(p, "northingM"),
            ["elevationM"] = P.DblOr(p, "elevationM", 0),
        };
    }
}

/// <summary>
/// Link a DWG/DXF. Params: path, viewId? (plan to link into; link is visible in all views unless
/// thisViewOnly), placement? "origin" (CAD origin = internal origin, default) | "shared",
/// unit? "mm" (default) | "m", thisViewOnly? (false). Returns the link instance id and its layers.
/// </summary>
public sealed class LinkCadCommand : IRevitCommand
{
    public string Name => "link_cad";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var path = P.Str(p, "path");
        if (!File.Exists(path)) throw new RevitCommandException("not_found", $"File not found: {path}");
        var view = p["viewId"] is not null ? OctaUtil.ResolveView(doc, p)
            : new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
        var opts = new DWGImportOptions
        {
            Placement = (P.StrOrNull(p, "placement") ?? "origin").ToLowerInvariant() == "shared" ? ImportPlacement.Shared : ImportPlacement.Origin,
            Unit = (P.StrOrNull(p, "unit") ?? "mm").ToLowerInvariant() switch { "m" => ImportUnit.Meter, "ft" => ImportUnit.Foot, _ => ImportUnit.Millimeter },
            ThisViewOnly = P.BoolOr(p, "thisViewOnly", false),
            ColorMode = ImportColorMode.Preserved,
        };
        if (!doc.Link(path, opts, view, out var id) || id == ElementId.InvalidElementId)
            throw new RevitCommandException("command_failed", $"Revit couldn't link {path}.");
        doc.Regenerate();
        var xf = SiteXf.FromParams(p);
        if (!xf.IsIdentity) { SiteXf.Apply(doc, id, xf); doc.Regenerate(); }
        var inst = doc.GetElement(id) as ImportInstance;
        return new JsonObject
        {
            ["affected"] = Affected.Created(id.Value),
            ["linkId"] = id.Value,
            ["layers"] = new JsonArray(CadUtil.Layers(doc, inst!).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        };
    }
}

internal static class SiteXf
{
    /// <summary>
    /// Survey placement: move the survey so the point "originM" (in the file's own coordinates,
    /// metres) lands on the internal origin, then rotate by rotateDeg (counter-clockwise) about it.
    /// Used so a boundary corner sits at the origin and the site runs square to the sheet.
    /// </summary>
    public static Transform FromParams(JsonObject p)
    {
        var t = Transform.Identity;
        if (p["originM"] is JsonObject o)
            t = Transform.CreateTranslation(-OctaUtil.Point(o, P.MetersToFeet));
        var deg = P.DblOr(p, "rotateDeg", 0);
        if (Math.Abs(deg) > 1e-12)
            t = Transform.CreateRotation(XYZ.BasisZ, deg * Math.PI / 180).Multiply(t);
        if (p["shiftM"] is JsonObject s)
            t = Transform.CreateTranslation(OctaUtil.Point(s, P.MetersToFeet)).Multiply(t);
        return t;
    }

    /// <summary>Apply a placement transform to an already-placed element (move, then rotate about the origin).</summary>
    public static void Apply(Document doc, ElementId id, Transform t)
    {
        var angle = Math.Atan2(t.BasisX.Y, t.BasisX.X);
        // Links arrive pinned: unpin to place, then pin again so the survey can't be nudged by accident.
        var el = doc.GetElement(id);
        el.Pinned = false;
        // t(p) = R·p + o  ==  move by R⁻¹·o, then rotate by R about the internal origin.
        var preMove = Transform.CreateRotation(XYZ.BasisZ, -angle).OfVector(t.Origin);
        if (preMove.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, id, preMove);
        if (Math.Abs(angle) > 1e-12)
            ElementTransformUtils.RotateElement(doc, id, Line.CreateUnbound(XYZ.Zero, XYZ.BasisZ), angle);
        el.Pinned = true;
    }
}

internal static class CadUtil
{
    /// <summary>
    /// Every straight segment in the CAD file as raw endpoints (no Revit curves are created, so
    /// survey-precision tiny segments are kept). Arcs and splines are tessellated.
    /// </summary>
    public static IEnumerable<(XYZ a, XYZ b, string layer)> Segments(Document doc, ImportInstance inst)
    {
        var geo = inst.get_Geometry(new Options { IncludeNonVisibleObjects = true });
        if (geo is null) yield break;
        foreach (var g in geo)
        {
            if (g is not GeometryInstance gi) continue;
            foreach (var o in gi.GetInstanceGeometry())
            {
                var layer = (doc.GetElement(o.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                IList<XYZ>? pts = o switch
                {
                    Line l => new[] { l.GetEndPoint(0), l.GetEndPoint(1) },
                    Curve c => c.Tessellate(),
                    PolyLine pl => pl.GetCoordinates(),
                    _ => null,
                };
                if (pts is null) continue;
                for (int i = 0; i + 1 < pts.Count; i++)
                    if (pts[i].DistanceTo(pts[i + 1]) > 1e-9) yield return (pts[i], pts[i + 1], layer);
            }
        }
    }

    public static IEnumerable<string> Layers(Document doc, ImportInstance inst)
    {
        var cat = inst.Category;
        if (cat is null) return Enumerable.Empty<string>();
        return cat.SubCategories.Cast<Category>().Select(c => c.Name).OrderBy(n => n);
    }
}

/// <summary>
/// Read linework from a linked/imported CAD file, optionally filtered by layer (substring, case-insensitive).
/// Returns per-layer counts and (if layer given) the segments in metres (internal coordinates).
/// Params: linkId, layer?, maxSegments? (2000).
/// </summary>
public sealed class GetCadGeometryCommand : IRevitCommand
{
    public string Name => "get_cad_geometry";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var inst = doc.GetElement(new ElementId(P.Long(p, "linkId"))) as ImportInstance
            ?? throw new RevitCommandException("not_found", "linkId is not a CAD link/import.");
        var filter = P.StrOrNull(p, "layer");
        var max = P.IntOr(p, "maxSegments", 2000);
        var counts = new Dictionary<string, int>();
        var segs = new JsonArray();
        foreach (var (a, b, layer) in CadUtil.Segments(doc, inst))
        {
            counts[layer] = counts.TryGetValue(layer, out var n) ? n + 1 : 1;
            if (filter is null || layer.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 || segs.Count >= max) continue;
            segs.Add(new JsonArray(Math.Round(a.X * P.FeetToMeters, 4), Math.Round(a.Y * P.FeetToMeters, 4), Math.Round(a.Z * P.FeetToMeters, 4),
                Math.Round(b.X * P.FeetToMeters, 4), Math.Round(b.Y * P.FeetToMeters, 4), Math.Round(b.Z * P.FeetToMeters, 4)));
        }
        return new JsonObject
        {
            ["layers"] = new JsonObject(counts.OrderBy(k => k.Key).Select(k => new KeyValuePair<string, JsonNode?>(k.Key, k.Value))),
            ["segments"] = segs,
            ["note"] = "Segments are [x1,y1,z1,x2,y2,z2] in metres, internal coordinates.",
        };
    }
}

/// <summary>
/// Link a point cloud (.rcp/.rcs). Params: path, shiftM? {x,y,z} (metres added to the cloud's own
/// coordinates, e.g. to remove an MGA truncation). Returns instance id and bounding box (metres).
/// </summary>
public sealed class LinkPointCloudCommand : IRevitCommand
{
    public string Name => "link_point_cloud";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var path = P.Str(p, "path");
        if (!File.Exists(path)) throw new RevitCommandException("not_found", $"File not found: {path}");
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var type = PointCloudType.Create(doc, ext, path);
        var inst = PointCloudInstance.Create(doc, type.Id, SiteXf.FromParams(p));
        doc.Regenerate();
        var bb = inst.get_BoundingBox(null);
        return new JsonObject
        {
            ["affected"] = Affected.Created(inst.Id.Value, type.Id.Value),
            ["instanceId"] = inst.Id.Value,
            ["bboxM"] = bb is null ? null : new JsonObject
            {
                ["min"] = new JsonArray(bb.Min.X * P.FeetToMeters, bb.Min.Y * P.FeetToMeters, bb.Min.Z * P.FeetToMeters),
                ["max"] = new JsonArray(bb.Max.X * P.FeetToMeters, bb.Max.Y * P.FeetToMeters, bb.Max.Z * P.FeetToMeters),
            },
        };
    }
}

/// <summary>
/// Sample a (ground) point cloud into a regular grid: per cell, the chosen statistic of point
/// heights (default "median"; "min" for an unclassified cloud). Params: instanceId, minM {x,y},
/// maxM {x,y}, cellM? (0.5), stat? median|min, maxPointsPerCall? (1,000,000).
/// Returns points [x,y,z] in metres (internal coordinates) and stats.
/// </summary>
public sealed class SamplePointCloudGridCommand : IRevitCommand
{
    public string Name => "sample_point_cloud_grid";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var inst = doc.GetElement(new ElementId(P.Long(p, "instanceId"))) as PointCloudInstance
            ?? throw new RevitCommandException("not_found", "instanceId is not a point cloud.");
        double ft = P.MetersToFeet;
        var min = OctaUtil.PointParam(p, "minM", ft);
        var max = OctaUtil.PointParam(p, "maxM", ft);
        var cell = P.DblOr(p, "cellM", 0.5) * ft;
        // stat: median (default) | min | max | pNN (percentile, e.g. p95 = roof tops without stray high points)
        var stat = (P.StrOrNull(p, "stat") ?? "median").ToLowerInvariant();
        double pct = stat switch { "min" => 0, "max" => 100, "median" => 50, _ when stat.StartsWith("p") && double.TryParse(stat[1..], out var v) => v, _ => 50 };

        var tf = inst.GetTotalTransform();
        var inv = tf.Inverse;
        var bbAll = inst.get_BoundingBox(null);
        var maxPts = Math.Clamp(P.IntOr(p, "maxPointsPerCall", 999_999), 1, 999_999);

        static PointCloudFilter BoxFilter(XYZ lo, XYZ hi) => PointCloudFilterFactory.CreateMultiPlaneFilter(new List<Plane>
        {
            Plane.CreateByNormalAndOrigin(XYZ.BasisX, lo), Plane.CreateByNormalAndOrigin(-XYZ.BasisX, hi),
            Plane.CreateByNormalAndOrigin(XYZ.BasisY, lo), Plane.CreateByNormalAndOrigin(-XYZ.BasisY, hi),
            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, lo), Plane.CreateByNormalAndOrigin(-XYZ.BasisZ, hi),
        });

        // Revit's docs are ambiguous about which frame the filter uses: try the box in MODEL
        // coordinates first, then in the cloud's own coordinates (all 8 corners, so a rotated
        // placement is fully covered).
        var mlo = new XYZ(min.X, min.Y, bbAll.Min.Z - 1);
        var mhi = new XYZ(max.X, max.Y, bbAll.Max.Z + 1);
        var pts = inst.GetPoints(BoxFilter(mlo, mhi), cell / 4, maxPts);
        string filterFrame = "model";
        if (pts.Count == 0)
        {
            var corners = new[] { mlo.X, mhi.X }.SelectMany(x => new[] { mlo.Y, mhi.Y }.SelectMany(y =>
                new[] { mlo.Z, mhi.Z }.Select(z => inv.OfPoint(new XYZ(x, y, z))))).ToList();
            var lo = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
            var hi = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
            pts = inst.GetPoints(BoxFilter(lo, hi), cell / 4, maxPts);
            filterFrame = "cloud";
        }

        // Returned points: decide their frame from the data — if most already fall inside the
        // model-space box they're model coordinates, otherwise transform them.
        var raw = pts.Cast<CloudPoint>().Select(cp => new XYZ(cp.X, cp.Y, cp.Z)).ToList();
        int insideAsModel = raw.Take(2000).Count(q => q.X >= mlo.X && q.X <= mhi.X && q.Y >= mlo.Y && q.Y <= mhi.Y);
        bool pointsAreModel = raw.Count > 0 && insideAsModel >= Math.Min(2000, raw.Count) * 0.8;

        var bins = new Dictionary<(int, int), List<double>>();
        int read = 0;
        foreach (var q in raw)
        {
            var w = pointsAreModel ? q : tf.OfPoint(q);
            if (w.X < min.X || w.X > max.X || w.Y < min.Y || w.Y > max.Y) continue;
            int i = (int)Math.Floor((w.X - min.X) / cell), j = (int)Math.Floor((w.Y - min.Y) / cell);
            if (!bins.TryGetValue((i, j), out var list)) bins[(i, j)] = list = new List<double>();
            list.Add(w.Z);
            read++;
        }
        var outPts = new JsonArray();
        foreach (var kv in bins.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
        {
            if (kv.Value.Count < 3) continue;
            kv.Value.Sort();
            var z = kv.Value[(int)Math.Round((kv.Value.Count - 1) * pct / 100.0)];
            double x = min.X + (kv.Key.Item1 + 0.5) * cell, y = min.Y + (kv.Key.Item2 + 0.5) * cell;
            outPts.Add(new JsonArray(Math.Round(x * P.FeetToMeters, 3), Math.Round(y * P.FeetToMeters, 3), Math.Round(z * P.FeetToMeters, 3)));
        }
        return new JsonObject
        {
            ["pointsRead"] = read, ["pointsReturned"] = raw.Count, ["filterFrame"] = filterFrame,
            ["pointsFrame"] = pointsAreModel ? "model" : "cloud", ["cells"] = outPts.Count, ["points"] = outPts,
        };
    }
}

/// <summary>
/// Create a toposolid from points [x,y,z] in metres (internal coordinates, z absolute).
/// Params: points, typeName? (first toposolid type), levelName? (lowest level), name? (Comments).
/// </summary>
public sealed class CreateToposolidCommand : IRevitCommand
{
    public string Name => "create_toposolid";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var arr = P.Arr(p, "points");
        var pts = new List<XYZ>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonArray a || a.Count < 3) throw new RevitCommandException("invalid_parameter", $"points[{i}] must be [x,y,z].");
            pts.Add(new XYZ(P.DblFrom(a[0], "x") * P.MetersToFeet, P.DblFrom(a[1], "y") * P.MetersToFeet, P.DblFrom(a[2], "z") * P.MetersToFeet));
        }
        if (pts.Count < 3) throw new RevitCommandException("invalid_parameter", "Need at least 3 points.");
        var types = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().ToList();
        var type = P.StrOrNull(p, "typeName") is { } tn
            ? types.FirstOrDefault(t => t.Name.Equals(tn, StringComparison.OrdinalIgnoreCase)) ?? throw new RevitCommandException("not_found", $"Toposolid type '{tn}' not found.")
            : types.FirstOrDefault() ?? throw new RevitCommandException("not_found", "No toposolid type in the project.");
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        var level = P.StrOrNull(p, "levelName") is { } ln
            ? levels.FirstOrDefault(l => l.Name.Equals(ln, StringComparison.OrdinalIgnoreCase)) ?? throw new RevitCommandException("not_found", $"Level '{ln}' not found.")
            : levels.First();
        var topo = Toposolid.Create(doc, pts, type.Id, level.Id);
        if (P.StrOrNull(p, "name") is { } nm) topo.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(nm);
        return new JsonObject { ["affected"] = Affected.Created(topo.Id.Value), ["id"] = topo.Id.Value, ["points"] = pts.Count, ["type"] = type.Name, ["level"] = level.Name };
    }
}

/// <summary>
/// Create a property line from a closed polygon of [x,y] points in metres (internal coordinates).
/// Returns segment lengths and bearings (grid, from internal north = +Y) and the enclosed area,
/// so they can be checked against the title. Params: points, name?.
/// </summary>
public sealed class CreatePropertyLineCommand : IRevitCommand
{
    public string Name => "create_property_line";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var arr = P.Arr(ctx.Parameters, "points");
        var pts = arr.Select((n, i) => n is JsonArray a && a.Count >= 2
            ? new XYZ(P.DblFrom(a[0], "x") * P.MetersToFeet, P.DblFrom(a[1], "y") * P.MetersToFeet, 0)
            : throw new RevitCommandException("invalid_parameter", $"points[{i}] must be [x,y].")).ToList();
        if (pts.Count < 3) throw new RevitCommandException("invalid_parameter", "Need at least 3 points.");
        var curves = new List<Curve>();
        var segs = new JsonArray();
        double area2 = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            curves.Add(Line.CreateBound(a, b));
            var d = b - a;
            var brg = (Math.Atan2(d.X, d.Y) * 180 / Math.PI + 360) % 360;
            segs.Add(new JsonObject { ["lengthM"] = Math.Round(d.GetLength() * P.FeetToMeters, 3), ["bearing"] = Dms(brg) });
            area2 += a.X * b.Y - b.X * a.Y;
        }
        var pl = PropertyLine.Create(doc, new List<CurveLoop> { CurveLoop.Create(curves) });
        return new JsonObject
        {
            ["affected"] = Affected.Created(pl.Id.Value),
            ["id"] = pl.Id.Value,
            ["areaM2"] = Math.Round(Math.Abs(area2) / 2 * P.FeetToMeters * P.FeetToMeters, 2),
            ["segments"] = segs,
        };
    }

    private static string Dms(double deg)
    {
        int d = (int)deg; double mf = (deg - d) * 60; int m = (int)mf; int s = (int)Math.Round((mf - m) * 60);
        if (s == 60) { s = 0; m++; } if (m == 60) { m = 0; d++; }
        return $"{d}°{m:00}'{s:00}\"";
    }
}

/// <summary>
/// Survey check: for each test point {name, x, y, rl} (metres, internal coordinates; rl = surveyed
/// level, absolute), find the modelled surface level straight below/above it (toposolid, floor or any
/// element in a 3D view) and report the difference. Params: points, toleranceMm? (10), elementIds? (limit).
/// </summary>
public sealed class SurveyCheckCommand : IRevitCommand
{
    public string Name => "survey_check";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        // The ray only sees what the view shows: a section box (common in templates) clips the
        // terrain, so prefer an explicit viewId, then a 3D view with no section box.
        var views3d = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
            .Where(v => !v.IsTemplate && !v.IsPerspective).ToList();
        var view3d = (p["viewId"] is not null ? doc.GetElement(new ElementId(P.Long(p, "viewId"))) as View3D : null)
            ?? views3d.FirstOrDefault(v => !v.IsSectionBoxActive)
            ?? views3d.FirstOrDefault()
            ?? throw new RevitCommandException("not_found", "Need a 3D view for the survey check.");
        var tol = P.DblOr(p, "toleranceMm", 10);
        ReferenceIntersector ri;
        if (p["elementIds"] is JsonArray)
            ri = new ReferenceIntersector(OctaUtil.Ids(p, "elementIds"), FindReferenceTarget.Face, view3d);
        else
            ri = new ReferenceIntersector(new ElementClassFilter(typeof(Toposolid)), FindReferenceTarget.Face, view3d);
        ri.FindReferencesInRevitLinks = false;

        var rows = new JsonArray();
        int pass = 0, fail = 0, miss = 0;
        foreach (var node in P.Arr(p, "points"))
        {
            if (node is not JsonObject o) continue;
            var x = P.Dbl(o, "x") * P.MetersToFeet; var y = P.Dbl(o, "y") * P.MetersToFeet;
            var rl = P.Dbl(o, "rl");
            var hit = ri.FindNearest(new XYZ(x, y, 10000), -XYZ.BasisZ);
            if (hit is null)
            {
                miss++;
                rows.Add(new JsonObject { ["name"] = P.StrOrNull(o, "name"), ["surveyRL"] = rl, ["modelRL"] = null, ["result"] = "no surface here" });
                continue;
            }
            var z = hit.GetReference().GlobalPoint.Z * P.FeetToMeters;
            var diffMm = Math.Round((z - rl) * 1000, 1);
            bool ok = Math.Abs(diffMm) <= tol;
            if (ok) pass++; else fail++;
            rows.Add(new JsonObject
            {
                ["name"] = P.StrOrNull(o, "name"), ["surveyRL"] = rl, ["modelRL"] = Math.Round(z, 3),
                ["diffMm"] = diffMm, ["result"] = ok ? "ok" : "MISMATCH",
            });
        }
        return new JsonObject { ["toleranceMm"] = tol, ["pass"] = pass, ["mismatch"] = fail, ["noSurface"] = miss, ["rows"] = rows };
    }
}
