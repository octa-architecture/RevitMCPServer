using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Build a parametric 2D detail item family from a spec, flex every type to prove the geometry
/// follows the parameters, save it, and (optionally) load it into the project.
///
/// Spec (lengths in mm):
///   name            family name, e.g. "OCTA-Detail-Angle"
///   lineBased       bool (default false) — uses "Metric Detail Item line based.rft"
///   folder          output folder (default: OCTA shared drive 4 Family Development\&lt;name&gt;)
///   load            bool (default true) — load into the active project after saving
///   parameters      [{ name, kind: "length"|"yesno", instance?: bool (default true), default }]
///   planes          [{ name, axis: "vertical"|"horizontal", offset }] — vertical = constant X.
///                   Built-ins: "CV" = Center (Left/Right) at X=0, "CH" = Center (Front/Back) at Y=0.
///   dimensions      [{ planes: [a, b], parameter }] or [{ planes: [a, mid, b], equal: true }]
///   lines           [{ from: [vPlane, hPlane], to: [vPlane, hPlane], visibleIf?: yesnoParam, style?: "Medium Lines" }]
///                   Each end sits on the crossing of a vertical and a horizontal plane and is locked there.
///   types           [{ name, values: { param: value } }] — yes/no values as true/false
///
/// Returns flex results per type: every line end checked against its planes after regeneration.
/// dryRun: builds and flexes but doesn't save or load.
/// </summary>
public sealed class CreateDetailFamilyCommand : IRevitCommand
{
    public string Name => "create_detail_family";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    private const string TemplateDir = @"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English";
    private const string DefaultRoot = @"H:\Shared drives\OCTA\3 Standards & Library\1 Revit\4 Family Development";
    private const double Mm = 1.0 / 304.8;
    private const double Tol = 0.01 * Mm;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var name = P.Str(p, "name");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new RevitCommandException("invalid_parameter", $"'{name}' isn't a valid file name.");
        var template = Path.Combine(TemplateDir,
            P.BoolOr(p, "lineBased", false) ? "Metric Detail Item line based.rft" : "Metric Detail Item.rft");
        if (!File.Exists(template)) throw new RevitCommandException("not_found", $"Template missing: {template}");

        var fam = ctx.App.Application.NewFamilyDocument(template)
            ?? throw new RevitCommandException("command_failed", "Revit couldn't create a family document.");
        var warnings = new JsonArray();
        try
        {
            var view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate)
                ?? throw new RevitCommandException("command_failed", "Family template has no plan view.");
            var mgr = fam.FamilyManager;
            var planes = new Dictionary<string, (ReferencePlane rp, bool vertical)>(StringComparer.OrdinalIgnoreCase);
            var famParams = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
            var lineSpecs = new List<(DetailCurve curve, string[] from, string[] to)>();

            using (var t = new Transaction(fam, "Build family"))
            {
                t.Start();
                // A fresh family has no type; parameter values need one.
                if (mgr.CurrentType is null) mgr.NewType(name);
                // Built-in centre planes.
                foreach (var rp in new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                {
                    var dir = (rp.FreeEnd - rp.BubbleEnd).Normalize();
                    bool vertical = Math.Abs(dir.Y) > 0.9;
                    if (rp.Name == "Center (Left/Right)") planes["CV"] = (rp, true);
                    else if (rp.Name == "Center (Front/Back)") planes["CH"] = (rp, false);
                    if (!string.IsNullOrEmpty(rp.Name)) planes.TryAdd(rp.Name, (rp, vertical));
                }

                foreach (var node in p["parameters"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject o) continue;
                    var pn = P.Str(o, "name");
                    var kind = (P.StrOrNull(o, "kind") ?? "length").ToLowerInvariant();
                    var spec = kind == "yesno" ? SpecTypeId.Boolean.YesNo : SpecTypeId.Length;
                    var group = kind == "yesno" ? GroupTypeId.Graphics : GroupTypeId.Geometry;
                    var fp = mgr.AddParameter(pn, group, spec, P.BoolOr(o, "instance", true));
                    famParams[pn] = fp;
                    if (o["default"] is not null) SetFamParam(mgr, fp, o["default"]);
                }

                foreach (var node in P.Arr(p, "planes"))
                {
                    if (node is not JsonObject o) continue;
                    var pn = P.Str(o, "name");
                    bool vertical = P.Str(o, "axis").StartsWith("v", StringComparison.OrdinalIgnoreCase);
                    var off = P.Dbl(o, "offset") * Mm;
                    var rp = vertical
                        ? fam.FamilyCreate.NewReferencePlane(new XYZ(off, -1, 0), new XYZ(off, 1, 0), XYZ.BasisZ, view)
                        : fam.FamilyCreate.NewReferencePlane(new XYZ(-1, off, 0), new XYZ(1, off, 0), XYZ.BasisZ, view);
                    rp.Name = pn;
                    planes[pn] = (rp, vertical);
                }
                fam.Regenerate();

                foreach (var node in p["dimensions"] as JsonArray ?? new JsonArray())
                {
                    if (node is not JsonObject o) continue;
                    var names = (P.Arr(o, "planes")).Select((n, i) => P.StrFrom(n, $"planes[{i}]")).ToList();
                    var refs = new ReferenceArray();
                    var ps = names.Select(n => Plane(planes, n)).ToList();
                    if (ps.Select(x => x.vertical).Distinct().Count() != 1)
                        throw new RevitCommandException("invalid_parameter", $"Dimension planes {string.Join(", ", names)} must all be parallel.");
                    foreach (var x in ps) refs.Append(x.rp.GetReference());
                    var dimLine = ps[0].vertical
                        ? Line.CreateBound(new XYZ(-1, -0.5, 0), new XYZ(1, -0.5, 0))
                        : Line.CreateBound(new XYZ(-0.5, -1, 0), new XYZ(-0.5, 1, 0));
                    var dim = fam.FamilyCreate.NewLinearDimension(view, dimLine, refs);
                    if (P.BoolOr(o, "equal", false)) dim.AreSegmentsEqual = true;
                    else if (P.StrOrNull(o, "parameter") is { } lab)
                        dim.FamilyLabel = famParams.TryGetValue(lab, out var fp) ? fp
                            : throw new RevitCommandException("not_found", $"Dimension label '{lab}' isn't in parameters.");
                }
                fam.Regenerate();

                foreach (var node in P.Arr(p, "lines"))
                {
                    if (node is not JsonObject o) continue;
                    var from = Pair(o, "from");
                    var to = Pair(o, "to");
                    var a = Corner(planes, from);
                    var b = Corner(planes, to);
                    if (a.DistanceTo(b) < Tol)
                        throw new RevitCommandException("invalid_parameter", $"Line {string.Join("/", from)} → {string.Join("/", to)} has zero length.");
                    var dc = fam.FamilyCreate.NewDetailCurve(view, Line.CreateBound(a, b));
                    if (P.StrOrNull(o, "style") is { } style) SetStyle(fam, dc, style, warnings);
                    if (P.StrOrNull(o, "visibleIf") is { } vis)
                    {
                        if (!famParams.TryGetValue(vis, out var fp))
                            throw new RevitCommandException("not_found", $"visibleIf '{vis}' isn't in parameters.");
                        mgr.AssociateElementParameterToFamilyParameter(dc.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), fp);
                    }
                    lineSpecs.Add((dc, from, to));
                }
                fam.Regenerate();

                // Lock: a line lying on a plane is aligned to it; every end is aligned to the
                // crossing plane(s) it sits on.
                foreach (var (dc, from, to) in lineSpecs)
                {
                    var curve = dc.GeometryCurve;
                    foreach (var pn in from.Intersect(to, StringComparer.OrdinalIgnoreCase))
                        TryAlign(fam, view, Plane(planes, pn).rp.GetReference(), curve.Reference, warnings, $"line on {pn}");
                    for (int end = 0; end < 2; end++)
                    {
                        var corner = end == 0 ? from : to;
                        var other = end == 0 ? to : from;
                        foreach (var pn in corner.Where(n => !other.Contains(n, StringComparer.OrdinalIgnoreCase)))
                            TryAlign(fam, view, Plane(planes, pn).rp.GetReference(), curve.GetEndPointReference(end), warnings,
                                $"end {end} on {pn}");
                    }
                }
                t.Commit();
            }

            // Types + flex test.
            var flex = new JsonArray();
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
                    if (first && mgr.CurrentType is not null) mgr.RenameCurrentType(tName);
                    else mgr.CurrentType = mgr.NewType(tName);
                    first = false;
                    if (tn["values"] is JsonObject vals)
                        foreach (var kv in vals)
                            SetFamParam(mgr, famParams.TryGetValue(kv.Key, out var fp) ? fp
                                : throw new RevitCommandException("not_found", $"Type value '{kv.Key}' isn't a parameter."), kv.Value);
                    fam.Regenerate();
                    var errs = Check(lineSpecs, planes);
                    allPassed &= errs.Count == 0;
                    flex.Add(new JsonObject { ["type"] = tName, ["passed"] = errs.Count == 0, ["errors"] = new JsonArray(errs.Select(e => (JsonNode)e).ToArray()) });
                }
                t.Commit();
            }

            string? savedPath = null;
            long? familyId = null;
            if (!ctx.DryRun && allPassed)
            {
                var folder = P.StrOrNull(p, "folder") ?? Path.Combine(DefaultRoot, name);
                Directory.CreateDirectory(folder);
                savedPath = Path.Combine(folder, name + ".rfa");
                fam.SaveAs(savedPath, new SaveAsOptions { OverwriteExistingFile = true });
                if (P.BoolOr(p, "load", true) && !doc.IsFamilyDocument)
                {
                    doc.LoadFamily(savedPath, new OverwriteLoadOptions(), out var family);
                    familyId = family?.Id.Value;
                }
            }

            return new JsonObject
            {
                ["affected"] = familyId is { } fid ? Affected.Created(fid) : Affected.Of(),
                ["name"] = name,
                ["flexPassed"] = allPassed,
                ["flex"] = flex,
                ["savedPath"] = savedPath,
                ["loadedFamilyId"] = familyId,
                ["warnings"] = warnings,
                ["note"] = allPassed ? (ctx.DryRun ? "Dry run: not saved." : null)
                    : "Flex test failed: nothing saved. Fix the spec (usually a missing dimension or plane) and retry.",
            };
        }
        finally
        {
            fam.Close(false);
        }
    }

    private static (ReferencePlane rp, bool vertical) Plane(Dictionary<string, (ReferencePlane rp, bool vertical)> planes, string n) =>
        planes.TryGetValue(n, out var x) ? x
            : throw new RevitCommandException("not_found", $"Plane '{n}' isn't defined. Planes: {string.Join(", ", planes.Keys)}");

    private static string[] Pair(JsonObject o, string key)
    {
        var a = P.Arr(o, key);
        if (a.Count != 2) throw new RevitCommandException("invalid_parameter", $"'{key}' must be [verticalPlane, horizontalPlane].");
        return new[] { P.StrFrom(a[0], key + "[0]"), P.StrFrom(a[1], key + "[1]") };
    }

    private static double PlanePos((ReferencePlane rp, bool vertical) x) =>
        x.vertical ? x.rp.BubbleEnd.X : x.rp.BubbleEnd.Y;

    private static XYZ Corner(Dictionary<string, (ReferencePlane rp, bool vertical)> planes, string[] pair)
    {
        var a = Plane(planes, pair[0]);
        var b = Plane(planes, pair[1]);
        if (a.vertical == b.vertical)
            throw new RevitCommandException("invalid_parameter", $"Corner {pair[0]}/{pair[1]} needs one vertical and one horizontal plane.");
        var v = a.vertical ? a : b;
        var h = a.vertical ? b : a;
        return new XYZ(PlanePos(v), PlanePos(h), 0);
    }

    private static List<string> Check(List<(DetailCurve curve, string[] from, string[] to)> lines,
        Dictionary<string, (ReferencePlane rp, bool vertical)> planes)
    {
        var errs = new List<string>();
        foreach (var (dc, from, to) in lines)
        {
            var c = dc.GeometryCurve;
            for (int end = 0; end < 2; end++)
            {
                var want = Corner(planes, end == 0 ? from : to);
                var got = c.GetEndPoint(end);
                if (want.DistanceTo(got) > Tol)
                    errs.Add($"{string.Join("/", from)}→{string.Join("/", to)} end {end}: off by {Math.Round(want.DistanceTo(got) / Mm, 2)} mm");
            }
        }
        return errs;
    }

    private static void TryAlign(Document fam, View view, Reference planeRef, Reference curveRef, JsonArray warnings, string what)
    {
        try { fam.FamilyCreate.NewAlignment(view, planeRef, curveRef); }
        catch (Exception ex) { warnings.Add($"Couldn't lock {what}: {ex.Message}"); }
    }

    private static void SetStyle(Document fam, DetailCurve dc, string style, JsonArray warnings)
    {
        var gs = new FilteredElementCollector(fam).OfClass(typeof(GraphicsStyle)).Cast<GraphicsStyle>()
            .FirstOrDefault(g => g.GraphicsStyleType == GraphicsStyleType.Projection &&
                                 g.Name.Equals(style, StringComparison.OrdinalIgnoreCase));
        if (gs is null) warnings.Add($"Line style '{style}' not found; default used.");
        else dc.LineStyle = gs;
    }

    private static void SetFamParam(FamilyManager mgr, FamilyParameter fp, JsonNode? value)
    {
        if (fp.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
            mgr.Set(fp, value is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? 1 : 0) : P.IntFrom(value, fp.Definition.Name));
        else
            mgr.Set(fp, P.DblFrom(value, fp.Definition.Name) * Mm);
    }

    private sealed class OverwriteLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
