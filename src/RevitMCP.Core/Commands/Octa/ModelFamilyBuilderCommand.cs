using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Build a parametric 3D family (Generic Model template, any model category) from a spec, flex every
/// type to prove the geometry follows the parameters, save it, and optionally load it.
///
/// Spec (lengths in mm):
///   name, category? (e.g. "Casework", "Furniture", "Specialty Equipment"; default Generic Models),
///   folder?, load? (default true)
///   parameters  [{ name, kind: "length"|"yesno"|"material", instance? (default false = type), default? }]
///   planes      [{ name, axis: "x"|"y"|"z", offset }]  x = constant X (left/right), y = constant Y
///               (front/back), z = constant height. Built-ins: "CX" (X=0), "CY" (Y=0), "LEVEL" (Z=0).
///   dimensions  [{ planes: [a, b], parameter }] or [{ planes: [a, mid, b], equal: true }] — all same axis
///   boxes       [{ x: [planeA, planeB], y: [..], z: [bottom, top], void?: bool, visibleIf?: yesno,
///                  material?: materialParam }]  — every face is locked to its plane
///   types       [{ name, values: { param: value } }]
/// dryRun: builds and flexes but doesn't save or load.
/// </summary>
public sealed class CreateModelFamilyCommand : IRevitCommand
{
    public string Name => "create_model_family";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    private const string Template = @"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Generic Model.rft";
    private const string DefaultRoot = @"H:\Shared drives\OCTA\3 Standards & Library\1 Revit\4 Family Development";
    private const double Mm = 1.0 / 304.8;
    private const double Tol = 0.01 * Mm;

    private sealed class PlaneRef
    {
        public required char Axis;          // 'x', 'y' or 'z'
        public ReferencePlane? Rp;
        public Level? Level;
        public Reference GetReference() => Rp?.GetReference() ?? Level!.GetPlaneReference();
        public double Position => Axis switch
        {
            'x' => Rp!.GetPlane().Origin.X,
            'y' => Rp!.GetPlane().Origin.Y,
            _ => Rp is not null ? Rp.GetPlane().Origin.Z : Level!.Elevation,
        };
    }

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var name = P.Str(p, "name");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new RevitCommandException("invalid_parameter", $"'{name}' isn't a valid file name.");
        // template: "generic" (default) | "window" | "door" | a full .rft path. Window/door templates
        // bring a host wall, the opening cut and their own planes (Left, Right, Sill, Head, Exterior,
        // Interior…) and Width/Height parameters — all usable by name in planes/boxes/dimensions.
        var tplKey = P.StrOrNull(p, "template") ?? "generic";
        var tplDir = Path.GetDirectoryName(Template)!;
        var template = tplKey.ToLowerInvariant() switch
        {
            "generic" => Template,
            "window" => Path.Combine(tplDir, "Metric Window.rft"),
            "door" => Path.Combine(tplDir, "Metric Door.rft"),
            _ => tplKey,
        };
        if (!File.Exists(template)) throw new RevitCommandException("not_found", $"Template missing: {template}");

        var fam = ctx.App.Application.NewFamilyDocument(template)
            ?? throw new RevitCommandException("command_failed", "Revit couldn't create a family document.");
        var warnings = new JsonArray();
        var stage = "start";
        try
        {
            // The template has a floor AND a ceiling plan; collector order varies, and dimensions/locks
            // fail in the ceiling plan — always use the floor plan.
            var plan = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate).OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : 1).First();
            var front = new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation)
                .OrderBy(v => v.Name.Contains("Front") ? 0 : v.Name.Contains("Exterior") ? 1 : 2).FirstOrDefault()
                ?? throw new RevitCommandException("command_failed", "Template has no elevation view.");
            var mgr = fam.FamilyManager;
            var planes = new Dictionary<string, PlaneRef>(StringComparer.OrdinalIgnoreCase);
            var famParams = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            // Re-find parameter handles by name, preferring the family's own over a same-named
            // category built-in. Needed after any regeneration that follows a category change,
            // which invalidates earlier handles.
            void RefreshParams()
            {
                var live = mgr.Parameters.Cast<FamilyParameter>().Where(x => x.Definition is not null).ToList();
                foreach (var key in famParams.Keys.ToList())
                {
                    var match = live.Where(x => x.Definition.Name == key).OrderByDescending(x => x.Id.Value > 0 ? 1 : 0).FirstOrDefault();
                    if (match is not null) famParams[key] = match;
                }
            }
            var boxes = new List<(GenericForm form, PlaneRef[] x, PlaneRef[] y, PlaneRef[] z, string label)>();
            (SymbolicCurve arc, PlaneRef hinge, PlaneRef strike)? swingCheck = null;

            using (var t = new Transaction(fam, "Build family"))
            {
                t.Start();
                { var fho = t.GetFailureHandlingOptions(); fho.SetFailuresPreprocessor(new QuietFailures()); t.SetFailureHandlingOptions(fho); }
                stage = "new type"; if (mgr.CurrentType is null) mgr.NewType(name);

                // Category is set AFTER the parameters (below): categories like Casework bring built-in
                // Width/Depth/Height that are locked as type parameters. Creating our own first (as the
                // OCTA joinery families do) lets them be instance parameters.
                Category? targetCategory = null;
                if (P.StrOrNull(p, "category") is { } catName)
                {
                    foreach (Category c in fam.Settings.Categories)
                        if (c.Name.Equals(catName, StringComparison.OrdinalIgnoreCase)) { targetCategory = c; break; }
                    if (targetCategory is null) throw new RevitCommandException("not_found", $"Category '{catName}' not found.");
                }

                foreach (var rp in new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                {
                    if (rp.Name == "Center (Left/Right)") planes["CX"] = new PlaneRef { Axis = 'x', Rp = rp };
                    else if (rp.Name == "Center (Front/Back)") planes["CY"] = new PlaneRef { Axis = 'y', Rp = rp };
                    // Every named template plane is usable by its own name too (Left, Sill, Exterior…).
                    if (!string.IsNullOrEmpty(rp.Name))
                    {
                        var nrm = rp.Normal;
                        char ax = Math.Abs(nrm.X) > 0.99 ? 'x' : Math.Abs(nrm.Y) > 0.99 ? 'y' : Math.Abs(nrm.Z) > 0.99 ? 'z' : '?';
                        if (ax != '?') planes.TryAdd(rp.Name, new PlaneRef { Axis = ax, Rp = rp });
                    }
                }
                var level = new FilteredElementCollector(fam).OfClass(typeof(Level)).Cast<Level>().First();
                planes["LEVEL"] = new PlaneRef { Axis = 'z', Level = level };

                if (P.BoolOr(p, "inspectTemplate", false))
                {
                    var info = new JsonObject
                    {
                        ["template"] = template,
                        ["planes"] = new JsonArray(planes.Select(kv => (JsonNode)new JsonObject
                        {
                            ["name"] = kv.Key, ["axis"] = kv.Value.Axis.ToString(), ["positionMm"] = Math.Round(kv.Value.Position / Mm, 1),
                        }).ToArray()),
                        ["parameters"] = new JsonArray(mgr.Parameters.Cast<FamilyParameter>().Where(x => x.Definition is not null)
                            .Select(x => (JsonNode)$"{x.Definition.Name}{(x.IsInstance ? " (instance)" : "")}{(string.IsNullOrEmpty(x.Formula) ? "" : " = " + x.Formula)}").ToArray()),
                        ["views"] = new JsonArray(new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate)
                            .Select(v => (JsonNode)$"{v.ViewType}: {v.Name}").ToArray()),
                        ["category"] = fam.OwnerFamily.FamilyCategory?.Name,
                    };
                    t.RollBack();
                    return info;
                }

                // Pass 1: length / yes-no parameters, then the category, then (pass 2) materials —
                // changing category invalidates material parameters created before it.
                void AddParams(bool materials)
                {
                foreach (var node in p["parameters"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject o) continue;
                    var pn = P.Str(o, "name");
                    var kind = (P.StrOrNull(o, "kind") ?? "length").ToLowerInvariant();
                    if ((kind == "material") != materials) continue;
                    var (spec, group) = kind switch
                    {
                        "yesno" => (SpecTypeId.Boolean.YesNo, GroupTypeId.Graphics),
                        "material" => (SpecTypeId.Reference.Material, GroupTypeId.Materials),
                        _ => (SpecTypeId.Length, GroupTypeId.Geometry),
                    };
                    // Categories like Casework already have Width/Depth/Height: reuse them (schedules/tags use them).
                    var wantInstance = P.BoolOr(o, "instance", false);
                    var fp = mgr.get_Parameter(pn) ?? mgr.AddParameter(pn, group, spec, wantInstance);
                    // Built-ins (e.g. Casework Width/Depth/Height) start as type parameters.
                    if (wantInstance && !fp.IsInstance) mgr.MakeInstance(fp);
                    famParams[pn] = fp;
                    if (o["default"] is not null && kind != "material" && o["formula"] is null) SetFamParam(mgr, fp, o["default"]);
                }
                }
                stage = "parameters"; AddParams(materials: false);
                if (targetCategory is not null)
                {
                    stage = "category"; try { fam.OwnerFamily.FamilyCategory = targetCategory; }
                    catch (Exception ex)
                    {
                        throw new RevitCommandException("invalid_parameter",
                            $"Couldn't set category '{targetCategory.Name}' after creating parameters: {ex.Message}");
                    }
                    // Changing category invalidates parameter handles, and a lookup by name can return the
                    // category's BUILT-IN parameter of the same name (e.g. Casework Width). Re-find each one,
                    // preferring the family's own (user-defined, positive id) parameter.
                    var all = mgr.Parameters.Cast<FamilyParameter>().Where(x => x.Definition is not null).ToList();
                    if (P.BoolOr(p, "debugParams", false))
                        throw new RevitCommandException("debug", "After category change: " + string.Join("; ",
                            all.Select(x => $"{x.Definition.Name}#{x.Id.Value}{(x.IsInstance ? "(i)" : "(t)")}")));
                    foreach (var key in famParams.Keys.ToList())
                    {
                        var match = all.Where(x => x.Definition.Name == key)
                            .OrderByDescending(x => x.Id.Value > 0 ? 1 : 0).FirstOrDefault();
                        if (match is not null) famParams[key] = match;
                    }
                }
                stage = "material parameters"; AddParams(materials: true);

                stage = "planes";
                foreach (var node in P.Arr(p, "planes"))
                {
                    if (node is not JsonObject o) continue;
                    var pn = P.Str(o, "name");
                    var axis = char.ToLowerInvariant(P.Str(o, "axis")[0]);
                    var off = P.Dbl(o, "offset") * Mm;
                    ReferencePlane rp = axis switch
                    {
                        'x' => fam.FamilyCreate.NewReferencePlane(new XYZ(off, -1, 0), new XYZ(off, 1, 0), XYZ.BasisZ, plan),
                        'y' => fam.FamilyCreate.NewReferencePlane(new XYZ(-1, off, 0), new XYZ(1, off, 0), XYZ.BasisZ, plan),
                        'z' => fam.FamilyCreate.NewReferencePlane(new XYZ(-1, 0, off), new XYZ(1, 0, off), XYZ.BasisY, front),
                        _ => throw new RevitCommandException("invalid_parameter", $"Plane '{pn}' axis must be x, y or z."),
                    };
                    rp.Name = pn;
                    planes[pn] = new PlaneRef { Axis = axis, Rp = rp };
                }
                // Reference types: named references (Left/Right/Front/Back/Top/Bottom) give the
                // placed family drag handles on instance sizes and clean snapping/dimensioning.
                // Any plane, including the built-in CX/CY, can be given one via "references".
                foreach (var node in P.Arr(p, "planes"))
                    if (node is JsonObject o && P.StrOrNull(o, "reference") is { } rt)
                        SetReferenceType(planes[P.Str(o, "name")].Rp!, rt);
                if (p["references"] is JsonObject refMap)
                    foreach (var kv in refMap)
                        if (planes.TryGetValue(kv.Key, out var pr) && pr.Rp is not null)
                            SetReferenceType(pr.Rp, P.StrFrom(kv.Value, kv.Key));
                fam.Regenerate();

                RefreshParams();
                stage = "dimensions";
                foreach (var node in p["dimensions"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject o) continue;
                    var names = P.Arr(o, "planes").Select((n, i) => P.StrFrom(n, $"planes[{i}]")).ToList();
                    stage = $"dimension {string.Join(" / ", names)} ({P.StrOrNull(o, "parameter") ?? "equal"})";
                    var ps = names.Select(n => Get(planes, n)).ToList();
                    var axis = ps[0].Axis;
                    if (ps.Any(x => x.Axis != axis))
                        throw new RevitCommandException("invalid_parameter", $"Dimension planes {string.Join(", ", names)} must share an axis.");
                    var dimStage = stage;
                    stage = dimStage + ": references";
                    var refs = new ReferenceArray();
                    foreach (var x in ps) refs.Append(x.GetReference() ?? throw new RevitCommandException("command_failed", $"A plane in {string.Join(", ", names)} has no reference."));
                    var (view, line) = axis switch
                    {
                        'x' => ((View)plan, Line.CreateBound(new XYZ(-1, -2, 0), new XYZ(1, -2, 0))),
                        'y' => (plan, Line.CreateBound(new XYZ(-2, -1, 0), new XYZ(-2, 1, 0))),
                        _ => (front, Line.CreateBound(new XYZ(-2, 0, -1), new XYZ(-2, 0, 1))),
                    };
                    stage = dimStage + $": create in {view.Name} ({view.ViewType})";
                    var dim = fam.FamilyCreate.NewLinearDimension(view, line, refs)
                        ?? throw new RevitCommandException("command_failed", $"Revit couldn't dimension {string.Join(", ", names)} in {view.Name}.");
                    stage = dimStage + ": label";
                    if (P.StrOrNull(o, "parameter") is { } lp && famParams.TryGetValue(lp, out var lpar))
                        stage += $" (param id {lpar.Id.Value}, instance {lpar.IsInstance}, def {(lpar.Definition is null ? "NULL" : lpar.Definition.Name)}, formula '{lpar.Formula}')";
                    if (P.BoolOr(o, "equal", false)) dim.AreSegmentsEqual = true;
                    else if (P.StrOrNull(o, "parameter") is { } lab)
                        dim.FamilyLabel = famParams.TryGetValue(lab, out var fp) ? fp
                            : throw new RevitCommandException("not_found", $"Dimension label '{lab}' isn't in parameters.");
                }
                fam.Regenerate();

                RefreshParams();
                stage = "formulas";
                // Formulas (after every parameter exists): e.g. "and(Top Rail On Flat, not(Solid Top))".
                foreach (var node in p["parameters"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject o || P.StrOrNull(o, "formula") is not { } formula) continue;
                    var fp = famParams[P.Str(o, "name")];
                    try { mgr.SetFormula(fp, formula); }
                    catch (Exception ex)
                    {
                        throw new RevitCommandException("invalid_parameter", $"Formula for '{fp.Definition.Name}' rejected: {ex.Message}");
                    }
                }
                fam.Regenerate();

                RefreshParams();
                stage = "boxes"; int bi = 0;
                foreach (var node in P.Arr(p, "boxes"))
                {
                    if (node is not JsonObject o) continue;
                    bi++;
                    var xs = Pair(planes, o, "x", 'x');
                    var ys = Pair(planes, o, "y", 'y');
                    var zs = Pair(planes, o, "z", 'z');
                    double x0 = Math.Min(xs[0].Position, xs[1].Position), x1 = Math.Max(xs[0].Position, xs[1].Position);
                    double y0 = Math.Min(ys[0].Position, ys[1].Position), y1 = Math.Max(ys[0].Position, ys[1].Position);
                    double z0 = Math.Min(zs[0].Position, zs[1].Position), z1 = Math.Max(zs[0].Position, zs[1].Position);
                    if (x1 - x0 < Tol || y1 - y0 < Tol || z1 - z0 < Tol)
                        throw new RevitCommandException("invalid_parameter", $"Box {bi} has zero size at the default values.");

                    var loop = new CurveArray();
                    var c = new[] { new XYZ(x0, y0, z0), new XYZ(x1, y0, z0), new XYZ(x1, y1, z0), new XYZ(x0, y1, z0) };
                    for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(c[i], c[(i + 1) % 4]));
                    var profile = new CurveArrArray();
                    profile.Append(loop);
                    var sketch = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z0)));
                    bool isVoid = P.BoolOr(o, "void", false);
                    var ext = fam.FamilyCreate.NewExtrusion(!isVoid, profile, sketch, z1 - z0);

                    if (P.StrOrNull(o, "visibleIf") is { } vis)
                        mgr.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM),
                            famParams.TryGetValue(vis, out var vp) ? vp : throw new RevitCommandException("not_found", $"visibleIf '{vis}' isn't a parameter."));
                    if (P.StrOrNull(o, "material") is { } mat && !isVoid)
                        mgr.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM),
                            famParams.TryGetValue(mat, out var mp) ? mp : throw new RevitCommandException("not_found", $"material '{mat}' isn't a parameter."));
                    // Detail level: "fine" (construction panels), "coarse-medium" (simple envelope), default all.
                    var detail = (P.StrOrNull(o, "detail") ?? "all").ToLowerInvariant();
                    if (detail != "all")
                    {
                        var detailVis = new FamilyElementVisibility(FamilyElementVisibilityType.Model)
                        {
                            IsShownInCoarse = detail.Contains("coarse"),
                            IsShownInMedium = detail.Contains("medium"),
                            IsShownInFine = detail.Contains("fine"),
                        };
                        ext.SetVisibility(detailVis);
                    }
                    boxes.Add((ext, xs, ys, zs, $"box {bi}"));
                }
                fam.Regenerate();

                // Revolves (turned posts, finials, pots, pipes): profile [[r,z],...] mm about a vertical
                // axis at center [xPlane, yPlane] (default CX/CY), z from zBase plane (default LEVEL).
                // Fixed geometry (not flexed) — heritage profiles are traced, not parametric.
                stage = "revolves";
                foreach (var node in p["revolves"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject o) continue;
                    var cxName = (o["center"] as JsonArray)?[0]?.GetValue<string>() ?? "CX";
                    var cyName = (o["center"] as JsonArray)?[1]?.GetValue<string>() ?? "CY";
                    double cx = Get(planes, cxName).Position, cy = Get(planes, cyName).Position;
                    double zb = Get(planes, P.StrOrNull(o, "zBase") ?? "LEVEL").Position;
                    var prof = P.Arr(o, "profile").Select(n => (JsonArray)n!)
                        .Select(a => new XYZ(cx + P.DblFrom(a[0], "r") * Mm, cy, zb + P.DblFrom(a[1], "z") * Mm)).ToList();
                    var loop = new CurveArray();
                    for (int i = 0; i < prof.Count; i++)
                    {
                        var a = prof[i]; var b = prof[(i + 1) % prof.Count];
                        if (a.DistanceTo(b) > fam.Application.ShortCurveTolerance) loop.Append(Line.CreateBound(a, b));
                    }
                    var arr = new CurveArrArray(); arr.Append(loop);
                    var sk = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(cx, cy, zb)));
                    var axis = Line.CreateBound(new XYZ(cx, cy, zb), new XYZ(cx, cy, zb + 1));
                    var rev = fam.FamilyCreate.NewRevolution(true, arr, sk, axis, 0, 2 * Math.PI);
                    if (P.StrOrNull(o, "material") is { } mat2 && famParams.TryGetValue(mat2, out var mp2))
                        mgr.AssociateElementParameterToFamilyParameter(rev.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), mp2);
                }

                // Profile extrusions drawn in elevation (lacework, brackets, fretwork): each item is one
                // closed loop [[x,z],...] in mm (x from CX, z from LEVEL), extruded between two y planes.
                // A loop may carry holes: { loops: [outer, hole, hole...] }. Fixed geometry, not flexed.
                stage = "profile extrusions";
                int extrusionIndex = 0;
                foreach (var node in p["extrusions"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject o) continue;
                    var ys = P.Arr(o, "y");
                    double y0 = Get(planes, P.StrFrom(ys[0], "y")).Position, y1 = Get(planes, P.StrFrom(ys[1], "y")).Position;
                    double ya = Math.Min(y0, y1), depth = Math.Abs(y1 - y0);
                    var arr = new CurveArrArray();
                    foreach (var loopNode in P.Arr(o, "loops"))
                    {
                        var pts = ((JsonArray)loopNode!).Select(a => (JsonArray)a!)
                            .Select(a => new XYZ(P.DblFrom(a[0], "x") * Mm, ya, P.DblFrom(a[1], "z") * Mm)).ToList();
                        var ca = new CurveArray();
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                            if (a.DistanceTo(b) > fam.Application.ShortCurveTolerance) ca.Append(Line.CreateBound(a, b));
                        }
                        arr.Append(ca);
                    }
                    var sk = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, ya, 0)));
                    Extrusion? ext = null;
                    try
                    {
                        ext = fam.FamilyCreate.NewExtrusion(true, arr, sk, depth);
                        // Validate now: a bad profile (self-crossing loop) otherwise fails the whole
                        // transaction at commit and takes every other element with it.
                        using (var st = new SubTransaction(fam))
                        {
                            st.Start();
                            fam.Regenerate();
                            st.Commit();
                        }
                        if (ext.get_Geometry(new Options()) is not { } eg || !eg.OfType<Solid>().Any(s => s.Volume > 0))
                            throw new InvalidOperationException("no solid produced");
                        // the sketch normal is +Y: make sure the solid sits between the two planes
                        if (ext.get_BoundingBox(null) is { } bb && bb.Min.Y < ya - Tol)
                            ext.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM)?.Set(0);
                        if (P.StrOrNull(o, "material") is { } em && famParams.TryGetValue(em, out var emp))
                            mgr.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), emp);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"extrusion {extrusionIndex}: dropped ({ex.Message})");
                        try { if (ext is not null && ext.IsValidObject) fam.Delete(ext.Id); } catch { }
                    }
                    extrusionIndex++;
                }

                // Door swing in plan: leaf line on the hinge plane + 90° arc, radius labelled with the
                // leaf-width parameter so it follows the type width. { hinge, strike, leafFace, direction (+1/-1), parameter }
                if (p["swing"] is JsonObject sw)
                {
                    stage = "swing";
                    var hinge = Get(planes, P.Str(sw, "hinge")); var strike = Get(planes, P.Str(sw, "strike"));
                    var face = Get(planes, P.Str(sw, "leafFace"));
                    double dir = P.DblOr(sw, "direction", -1) >= 0 ? 1 : -1;
                    double hx = hinge.Position, sx = strike.Position, fy = face.Position, rad = Math.Abs(sx - hx);
                    var sk = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    var c0 = new XYZ(hx, fy, 0);
                    var open = new XYZ(hx, fy + dir * rad, 0);
                    var shut = new XYZ(sx, fy, 0);
                    var mid = c0 + ((shut - c0).Normalize() + (open - c0).Normalize()).Normalize() * rad;
                    var leaf = fam.FamilyCreate.NewSymbolicCurve(Line.CreateBound(c0, open), sk);
                    var arc = fam.FamilyCreate.NewSymbolicCurve(Arc.Create(shut, open, mid), sk);
                    fam.Regenerate();
                    try { fam.FamilyCreate.NewAlignment(plan, hinge.GetReference(), leaf.GeometryCurve.Reference); }
                    catch (Exception ex) { warnings.Add("swing: couldn't lock leaf line: " + ex.Message); }
                    if (P.StrOrNull(sw, "parameter") is { } swp && famParams.TryGetValue(swp, out var swParam))
                    {
                        try
                        {
                            var rd = fam.FamilyCreate.NewRadialDimension(plan, arc.GeometryCurve.Reference, mid);
                            rd.FamilyLabel = swParam;
                        }
                        catch (Exception ex) { warnings.Add("swing: couldn't label the arc radius: " + ex.Message); }
                    }
                    swingCheck = (arc, hinge, strike);
                }

                stage = "face locks";
                // Lock every face to its plane: side faces in plan, top/bottom in the front elevation.
                foreach (var (form, xs, ys, zs, label) in boxes)
                {
                    foreach (var face in Faces(form))
                    {
                        var n = face.FaceNormal;
                        var o = face.Origin;
                        PlaneRef? target = null;
                        View? view = null;
                        if (Math.Abs(n.X) > 0.99) { target = xs.FirstOrDefault(x => Math.Abs(x.Position - o.X) < Tol); view = plan; }
                        else if (Math.Abs(n.Y) > 0.99) { target = ys.FirstOrDefault(y => Math.Abs(y.Position - o.Y) < Tol); view = front; }
                        else if (Math.Abs(n.Z) > 0.99) { target = zs.FirstOrDefault(z => Math.Abs(z.Position - o.Z) < Tol); view = front; }
                        if (target is null || view is null) continue;
                        // Y faces show as lines in plan, not in the front view.
                        if (Math.Abs(n.Y) > 0.99) view = plan;
                        try { fam.FamilyCreate.NewAlignment(view, target.GetReference(), face.Reference); }
                        catch (Exception ex) { warnings.Add($"{label}: couldn't lock face ({n.X:0},{n.Y:0},{n.Z:0}): {ex.Message}"); }
                    }
                }
                if (t.Commit() != TransactionStatus.Committed)
                    throw new RevitCommandException("command_failed", "Revit rolled back the family geometry at commit (an invalid shape or constraint). Warnings: " + string.Join("; ", warnings.Select(w => w?.ToString())));
            }

            RefreshParams();
            stage = "types"; var flex = new JsonArray();
            bool allPassed = true;
            using (var t = new Transaction(fam, "Types"))
            {
                t.Start();
                var typeNodes = (p["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                if (typeNodes.Count == 0) typeNodes.Add(new JsonObject { ["name"] = name });
                bool first = true;
                foreach (var tn in typeNodes)
                {
                    var tName = P.Str(tn, "name");
                    if (first) mgr.RenameCurrentType(tName); else mgr.CurrentType = mgr.NewType(tName);
                    first = false;
                    // A new type copies the previous one; start every type from the declared defaults
                    // so a value left out means "default", not "whatever the last type had".
                    foreach (var dn in (p["parameters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        if (dn["default"] is not null && famParams.TryGetValue(P.Str(dn, "name"), out var dfp))
                            SetFamParam(mgr, dfp, dn["default"]);
                    if (tn["values"] is JsonObject vals)
                        foreach (var kv in vals)
                            SetFamParam(mgr, famParams.TryGetValue(kv.Key, out var fp) ? fp
                                : throw new RevitCommandException("not_found", $"Type value '{kv.Key}' isn't a parameter."), kv.Value);
                    fam.Regenerate();
                    var errs = CheckBoxes();
                    allPassed &= errs.Count == 0;
                    flex.Add(new JsonObject { ["type"] = tName, ["passed"] = errs.Count == 0, ["errors"] = new JsonArray(errs.Select(e => (JsonNode)e).ToArray()) });
                }

                stage = "scenarios";
                // Scenarios: instance-parameter combinations (tick boxes, sizes) flexed on the first type
                // and then reset, so every combination a user can pick is proven, not just the types.
                var firstType = typeNodes[0];
                foreach (var sn in (p["scenarios"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var sName = P.Str(sn, "name");
                    mgr.CurrentType = mgr.Types.Cast<FamilyType>().First(ft => ft.Name == P.Str(firstType, "name"));
                    if (sn["values"] is JsonObject vals)
                        foreach (var kv in vals)
                            SetFamParam(mgr, famParams.TryGetValue(kv.Key, out var fp) ? fp
                                : throw new RevitCommandException("not_found", $"Scenario value '{kv.Key}' isn't a parameter."), kv.Value);
                    fam.Regenerate();
                    var errs = CheckBoxes();
                    allPassed &= errs.Count == 0;
                    flex.Add(new JsonObject { ["scenario"] = sName, ["passed"] = errs.Count == 0, ["errors"] = new JsonArray(errs.Select(e => (JsonNode)e).ToArray()) });
                    // Reset to the declared defaults + the first type's own values.
                    foreach (var dn in (p["parameters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        if (dn["default"] is not null && famParams.TryGetValue(P.Str(dn, "name"), out var dfp))
                            SetFamParam(mgr, dfp, dn["default"]);
                    if (firstType["values"] is JsonObject fv)
                        foreach (var kv in fv) SetFamParam(mgr, famParams[kv.Key], kv.Value);
                    fam.Regenerate();
                }
                t.Commit();

                List<string> CheckBoxes()
                {
                    var errs = new List<string>();
                    foreach (var (form, xs, ys, zs, label) in boxes)
                    {
                        var bb = form.get_BoundingBox(null);
                        if (bb is null) continue; // hidden by a yes/no parameter in this type
                        void Expect(string what, double got, double want)
                        {
                            if (Math.Abs(got - want) > Tol) errs.Add($"{label} {what}: off by {Math.Round((got - want) / Mm, 2)} mm");
                        }
                        Expect("left", bb.Min.X, Math.Min(xs[0].Position, xs[1].Position));
                        Expect("right", bb.Max.X, Math.Max(xs[0].Position, xs[1].Position));
                        Expect("front", bb.Min.Y, Math.Min(ys[0].Position, ys[1].Position));
                        Expect("back", bb.Max.Y, Math.Max(ys[0].Position, ys[1].Position));
                        Expect("bottom", bb.Min.Z, Math.Min(zs[0].Position, zs[1].Position));
                        Expect("top", bb.Max.Z, Math.Max(zs[0].Position, zs[1].Position));
                    }
                    // Swing: arc radius must equal the leaf width and its centre must sit on the hinge.
                    if (swingCheck is { } sc && sc.arc.GeometryCurve is Arc a)
                    {
                        double want = Math.Abs(sc.strike.Position - sc.hinge.Position);
                        if (Math.Abs(a.Radius - want) > Tol) errs.Add($"swing radius off by {Math.Round((a.Radius - want) / Mm, 1)} mm");
                        if (Math.Abs(a.Center.X - sc.hinge.Position) > Tol) errs.Add($"swing centre off the hinge by {Math.Round((a.Center.X - sc.hinge.Position) / Mm, 1)} mm");
                    }
                    return errs;
                }
            }

            string? savedPath = null;
            long? familyId = null;
            stage = "save/load";
            if (!ctx.DryRun && allPassed)
            {
                var folder = P.StrOrNull(p, "folder") ?? Path.Combine(DefaultRoot, name);
                Directory.CreateDirectory(folder);
                savedPath = Path.Combine(folder, name + ".rfa");
                fam.SaveAs(savedPath, new SaveAsOptions { OverwriteExistingFile = true });
                if (P.BoolOr(p, "load", true) && !doc.IsFamilyDocument)
                {
                    doc.LoadFamily(savedPath, new OverwriteFamilyLoad(), out var family);
                    familyId = family?.Id.Value;
                }
            }

            return new JsonObject
            {
                ["affected"] = familyId is { } fid ? Affected.Created(fid) : Affected.Of(),
                ["name"] = name,
                ["category"] = fam.OwnerFamily.FamilyCategory?.Name,
                ["flexPassed"] = allPassed,
                ["flex"] = flex,
                ["savedPath"] = savedPath,
                ["loadedFamilyId"] = familyId,
                ["warnings"] = warnings,
                ["note"] = allPassed ? (ctx.DryRun ? "Dry run: not saved." : null)
                    : "Flex test failed: nothing saved. Usually a face that isn't dimensioned to a parameter or locked.",
            };
        }
        catch (Exception ex) when (ex is not RevitCommandException)
        {
            // Unexpected internal failure: report where, so it can be fixed rather than guessed at.
            var frame = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("CreateModelFamilyCommand"))?.Trim();
            throw new RevitCommandException("command_failed", $"{ex.GetType().Name} during '{stage}': {ex.Message}");
        }
        finally
        {
            fam.Close(false);
        }
    }

    private static void SetReferenceType(ReferencePlane rp, string type)
    {
        var t = type.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        FamilyInstanceReferenceType v = t switch
        {
            "left" => FamilyInstanceReferenceType.Left,
            "right" => FamilyInstanceReferenceType.Right,
            "front" => FamilyInstanceReferenceType.Front,
            "back" => FamilyInstanceReferenceType.Back,
            "top" => FamilyInstanceReferenceType.Top,
            "bottom" => FamilyInstanceReferenceType.Bottom,
            "centerleftright" or "centrelr" or "centerlr" => FamilyInstanceReferenceType.CenterLeftRight,
            "centerfrontback" or "centrefb" or "centerfb" => FamilyInstanceReferenceType.CenterFrontBack,
            "centerelevation" => FamilyInstanceReferenceType.CenterElevation,
            "strong" => FamilyInstanceReferenceType.StrongReference,
            "weak" => FamilyInstanceReferenceType.WeakReference,
            "not" or "none" => FamilyInstanceReferenceType.NotAReference,
            _ => throw new RevitCommandException("invalid_parameter", $"Unknown reference type '{type}'."),
        };
        var prm = rp.get_Parameter(BuiltInParameter.ELEM_REFERENCE_NAME)
            ?? throw new RevitCommandException("command_failed", $"Plane '{rp.Name}' has no reference setting.");
        prm.Set((int)v);
    }

    private static PlaneRef Get(Dictionary<string, PlaneRef> planes, string n) =>
        planes.TryGetValue(n, out var x) ? x
            : throw new RevitCommandException("not_found", $"Plane '{n}' isn't defined. Planes: {string.Join(", ", planes.Keys)}");

    private static PlaneRef[] Pair(Dictionary<string, PlaneRef> planes, JsonObject o, string key, char axis)
    {
        var a = P.Arr(o, key);
        if (a.Count != 2) throw new RevitCommandException("invalid_parameter", $"Box '{key}' must be two plane names.");
        var r = new[] { Get(planes, P.StrFrom(a[0], key)), Get(planes, P.StrFrom(a[1], key)) };
        if (r.Any(x => x.Axis != axis))
            throw new RevitCommandException("invalid_parameter", $"Box '{key}' planes must be {axis}-axis planes.");
        return r;
    }

    private static IEnumerable<PlanarFace> Faces(GenericForm form)
    {
        var opts = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
        foreach (var g in form.get_Geometry(opts))
            if (g is Solid s)
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && pf.Reference is not null) yield return pf;
    }

    private static void SetFamParam(FamilyManager mgr, FamilyParameter fp, JsonNode? value)
    {
        if (!string.IsNullOrEmpty(fp.Formula)) return; // driven by a formula
        var dt = fp.Definition.GetDataType();
        if (dt == SpecTypeId.Boolean.YesNo)
            mgr.Set(fp, value is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? 1 : 0) : P.IntFrom(value, fp.Definition.Name));
        else if (dt == SpecTypeId.Length)
            mgr.Set(fp, P.DblFrom(value, fp.Definition.Name) * Mm);
    }

    private sealed class OverwriteFamilyLoad : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }
}
