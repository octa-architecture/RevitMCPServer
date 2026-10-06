using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Make a profile family (for wall sweeps, gutters, fascias, railings) from a closed polyline.
/// Params: name, points [[x,y],...] in mm (x = out from the wall/edge, y = up; origin = the attach
/// point), folder?, load? (true). Saved under Family Development unless folder is given.
/// </summary>
public sealed class CreateProfileFamilyCommand : IRevitCommand
{
    public string Name => "create_profile_family";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    private const string Template = @"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Profile.rft";
    private const string Root = @"H:\Shared drives\OCTA\3 Standards & Library\1 Revit\4 Family Development";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var name = P.Str(p, "name");
        var pts = P.Arr(p, "points").Select(n => (JsonArray)n!)
            .Select(a => new XYZ(P.DblFrom(a[0], "x") / 304.8, P.DblFrom(a[1], "y") / 304.8, 0)).ToList();
        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) < 1e-6) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) throw new RevitCommandException("invalid_parameter", "A profile needs at least 3 points.");
        var fam = ctx.App.Application.NewFamilyDocument(Template)
            ?? throw new RevitCommandException("command_failed", "Couldn't create a profile family.");
        try
        {
            var view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);
            using (var t = new Transaction(fam, "Profile"))
            {
                t.Start();
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    if (a.DistanceTo(b) < fam.Application.ShortCurveTolerance) continue;
                    fam.FamilyCreate.NewDetailCurve(view, Line.CreateBound(a, b));
                }
                if (fam.FamilyManager.CurrentType is null) fam.FamilyManager.NewType(name);
                t.Commit();
            }
            var folder = P.StrOrNull(p, "folder") ?? Path.Combine(Root, name);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name + ".rfa");
            fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            long? id = null;
            if (P.BoolOr(p, "load", true))
            {
                using var lt = new Transaction(doc, "Load profile");
                lt.Start();
                doc.LoadFamily(path, new Overwrite(), out var family);
                lt.Commit();
                id = family?.Id.Value;
            }
            return new JsonObject { ["savedPath"] = path, ["familyId"] = id, ["points"] = pts.Count };
        }
        finally { fam.Close(false); }
    }

    // UiAction: we manage the project transaction ourselves (family docs can't be edited inside one).
    public ExecutionKind Execution => ExecutionKind.UiAction;

    private sealed class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }
}

/// <summary>
/// Wall sweep (cornice, string course, skirting) along a wall, using a profile family. A sweep type
/// named typeName is made (or reused) with that profile. Params: wallId, profileFamily, typeName,
/// heightRL (absolute RL of the profile origin) or offsetMm (from the wall base), side? "exterior",
/// flip? (false), wallOffsetMm? (0), materialName?.
/// </summary>
public sealed class CreateWallSweepCommand : IRevitCommand
{
    public string Name => "create_wall_sweep";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public bool ResolveErrorsOnCommit => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var wall = doc.GetElement(new ElementId(P.Long(p, "wallId"))) as Wall
            ?? throw new RevitCommandException("not_found", "wallId is not a wall.");
        var profile = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(s => s.FamilyName == P.Str(p, "profileFamily"))
            ?? throw new RevitCommandException("not_found", $"Profile family '{P.Str(p, "profileFamily")}' isn't loaded.");
        var typeName = P.Str(p, "typeName");
        var sweepTypes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Cornices).WhereElementIsElementType().Cast<ElementType>().ToList();
        var st = sweepTypes.FirstOrDefault(t => t.Name == typeName)
            ?? (sweepTypes.FirstOrDefault() ?? throw new RevitCommandException("not_found", "No wall sweep type in the project to copy.")).Duplicate(typeName);
        var prof = st.LookupParameter("Profile") ?? throw new RevitCommandException("command_failed", "Sweep type has no Profile parameter.");
        prof.Set(profile.Id);
        if (P.StrOrNull(p, "materialName") is { } mn &&
            new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == mn) is { } mat)
            st.LookupParameter("Material")?.Set(mat.Id);

        var lvl = doc.GetElement(wall.LevelId) as Level;
        var baseOff = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
        double dist = p["heightRL"] is not null
            ? P.Dbl(p, "heightRL") * P.MetersToFeet - (lvl?.ProjectElevation ?? 0) - baseOff
            : P.DblOr(p, "offsetMm", 0) / 304.8;
        var info = new WallSweepInfo(WallSweepType.Sweep, false)
        {
            Distance = dist,
            DistanceMeasuredFrom = DistanceMeasuredFrom.Base,
            WallSide = (P.StrOrNull(p, "side") ?? "exterior").Equals("interior", StringComparison.OrdinalIgnoreCase) ? WallSide.Interior : WallSide.Exterior,
            IsProfileFlipped = P.BoolOr(p, "flip", false),
            WallOffset = P.DblOr(p, "wallOffsetMm", 0) / 304.8,
        };
        var sweep = WallSweep.Create(wall, st.Id, info);
        return new JsonObject { ["affected"] = Affected.Created(sweep.Id.Value), ["id"] = sweep.Id.Value, ["type"] = st.Name, ["distanceMm"] = Math.Round(dist * 304.8) };
    }
}

/// <summary>
/// Footprint roof (skillion, hip, flat) from a polygon. Params: points [[x,y]] (m, internal),
/// levelName, baseRL (absolute RL of the roof base at the footprint), slopes? [deg per edge; 0/null =
/// no slope], thicknessMm? (single-layer type "{prefix}{t}"), typePrefix? "EX - Roof ", phase?, comments?.
/// </summary>
public sealed class CreateFootprintRoofCommand : IRevitCommand
{
    public string Name => "create_footprint_roof";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public bool ResolveErrorsOnCommit => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var ft = P.MetersToFeet;
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name.Equals(P.Str(p, "levelName"), StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found", "Level not found.");
        var pts = P.Arr(p, "points").Select(n => (JsonArray)n!).Select(a => new XYZ(P.DblFrom(a[0], "x") * ft, P.DblFrom(a[1], "y") * ft, level.Elevation)).ToList();
        var types = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().ToList();
        var basic = types.FirstOrDefault(t => t.GetCompoundStructure() is not null) ?? throw new RevitCommandException("not_found", "No basic roof type.");
        RoofType rt = basic;
        if (p["thicknessMm"] is not null)
        {
            var tmm = P.Dbl(p, "thicknessMm");
            var tn = $"{P.StrOrNull(p, "typePrefix") ?? "EX - Roof "}{tmm:000}";
            rt = types.FirstOrDefault(t => t.Name == tn) ?? (RoofType)basic.Duplicate(tn);
            rt.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure, tmm / 304.8,
                basic.GetCompoundStructure().GetLayers().FirstOrDefault()?.MaterialId ?? ElementId.InvalidElementId));
        }
        var ca = new CurveArray();
        for (int i = 0; i < pts.Count; i++) ca.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        FootPrintRoof roof; ModelCurveArray map;
        try { roof = doc.Create.NewFootPrintRoof(ca, level, rt, out map); }
        catch (Exception ex)
        {
            throw new RevitCommandException("command_failed",
                $"NewFootPrintRoof failed ({ex.Message}); type '{rt?.Name}' ({rt?.Id.Value}), level '{level.Name}', {pts.Count} edges.");
        }
        if (roof is null || map is null) throw new RevitCommandException("command_failed", "Revit returned no roof.");
        var slopes = p["slopes"] as JsonArray;
        int k = 0;
        foreach (ModelCurve mc in map)
        {
            double deg = slopes is not null && k < slopes.Count && slopes[k] is not null ? P.DblFrom(slopes[k], "slope") : 0;
            roof.set_DefinesSlope(mc, deg > 0);
            if (deg > 0) roof.set_SlopeAngle(mc, Math.Tan(deg * Math.PI / 180));
            k++;
        }
        roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(P.Dbl(p, "baseRL") * ft - level.ProjectElevation);
        if (P.StrOrNull(p, "phase") is { } ph && doc.Phases.Cast<Phase>().FirstOrDefault(x => x.Name == ph) is { } phase)
            roof.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
        if (P.StrOrNull(p, "comments") is { } c) roof.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(c);
        return new JsonObject { ["affected"] = Affected.Created(roof.Id.Value), ["id"] = roof.Id.Value, ["type"] = rt.Name };
    }
}

/// <summary>
/// Gutter (or fascia) on a roof edge, with a profile family. Picks the roof's lowest horizontal edge
/// nearest edgeNear [x,y]. Params: roofId, profileFamily, typeName, edgeNear [x,y] (m), kind? "gutter"|"fascia".
/// </summary>
public sealed class CreateRoofEdgeCommand : IRevitCommand
{
    public string Name => "create_roof_edge";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public bool ResolveErrorsOnCommit => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var ft = P.MetersToFeet;
        var roof = doc.GetElement(new ElementId(P.Long(p, "roofId"))) as RoofBase ?? throw new RevitCommandException("not_found", "roofId is not a roof.");
        var near = P.Arr(p, "edgeNear"); var target = new XYZ(P.DblFrom(near[0], "x") * ft, P.DblFrom(near[1], "y") * ft, 0);
        var edges = new List<(Edge e, Line l)>();
        foreach (var g in roof.get_Geometry(new Options { ComputeReferences = true }))
            if (g is Solid s)
                foreach (Edge e in s.Edges)
                    if (e.AsCurve() is Line l && Math.Abs(l.Direction.Z) < 1e-3 && e.Reference is not null) edges.Add((e, l));
        if (edges.Count == 0) throw new RevitCommandException("command_failed", "No horizontal roof edges found.");
        double minZ = edges.Min(x => x.l.GetEndPoint(0).Z);
        var pick = edges.Where(x => Math.Abs(x.l.GetEndPoint(0).Z - minZ) < 0.3)
            .OrderBy(x => { var m = (x.l.GetEndPoint(0) + x.l.GetEndPoint(1)) / 2; return new XYZ(m.X, m.Y, 0).DistanceTo(target); })
            // the top edge of the fascia face is the one a gutter hangs from: prefer the higher of a near pair
            .ThenByDescending(x => x.l.GetEndPoint(0).Z).First();
        var profile = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(s => s.FamilyName == P.Str(p, "profileFamily")) ?? throw new RevitCommandException("not_found", "Profile family isn't loaded.");
        var refs = new ReferenceArray(); refs.Append(pick.e.Reference);
        var kind = (P.StrOrNull(p, "kind") ?? "gutter").ToLowerInvariant();
        Element made;
        if (kind == "fascia")
        {
            var ftypes = new FilteredElementCollector(doc).OfClass(typeof(FasciaType)).Cast<FasciaType>().ToList();
            var t = ftypes.FirstOrDefault(x => x.Name == P.Str(p, "typeName")) ?? (FasciaType)(ftypes.FirstOrDefault() ?? throw new RevitCommandException("not_found", "No fascia type to copy.")).Duplicate(P.Str(p, "typeName"));
            t.LookupParameter("Profile")?.Set(profile.Id);
            made = doc.Create.NewFascia(t, refs);
        }
        else
        {
            var gtypes = new FilteredElementCollector(doc).OfClass(typeof(GutterType)).Cast<GutterType>().ToList();
            var t = gtypes.FirstOrDefault(x => x.Name == P.Str(p, "typeName")) ?? (GutterType)(gtypes.FirstOrDefault() ?? throw new RevitCommandException("not_found", "No gutter type to copy.")).Duplicate(P.Str(p, "typeName"));
            t.LookupParameter("Profile")?.Set(profile.Id);
            made = doc.Create.NewGutter(t, refs);
        }
        return new JsonObject { ["affected"] = Affected.Created(made.Id.Value), ["id"] = made.Id.Value, ["edgeZ_RL"] = Math.Round(pick.l.GetEndPoint(0).Z * P.FeetToMeters, 3) };
    }
}
