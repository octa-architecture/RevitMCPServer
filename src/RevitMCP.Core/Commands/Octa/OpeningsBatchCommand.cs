using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Place surveyed doors/windows in one go. Each opening: { family, widthMm, heightMm, x, y (centre,
/// metres internal), sillRL (bottom of opening, absolute), outside? [x,y] (a point on the exterior
/// side, to face the family out), params? { name: value } (lengths in mm, yes/no as bool),
/// comments? }. The host is the nearest wall on the level (within 0.4 m); a type "WWWW x HHHH" is
/// made per size; Wall Thickness (if the family has it) is set from the host wall. Never touches
/// Mark (opening numbers belong to Practice OS). Params: openings, levelName, phase? "Existing".
/// </summary>
public sealed class PlaceOpeningsBatchCommand : IRevitCommand
{
    public string Name => "place_openings_batch";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";
    public bool ResolveErrorsOnCommit => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var ft = P.MetersToFeet;
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name.Equals(P.Str(p, "levelName"), StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found", $"Level '{P.Str(p, "levelName")}' not found.");
        var phase = doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(P.StrOrNull(p, "phase") ?? "Existing", StringComparison.OrdinalIgnoreCase));
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
            .Where(w => w.LevelId == level.Id && w.Location is LocationCurve).ToList();
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();

        var rows = new JsonArray(); var created = new List<long>();
        int i = 0;
        foreach (var n in P.Arr(p, "openings"))
        {
            var o = (JsonObject)n!;
            var row = new JsonObject { ["index"] = i++ };
            try
            {
                var famName = P.Str(o, "family");
                double wmm = P.Dbl(o, "widthMm"), hmm = P.Dbl(o, "heightMm");
                var typeName = $"{wmm:0000} x {hmm:0000}";
                var famSyms = symbols.Where(s => s.FamilyName == famName).ToList();
                if (famSyms.Count == 0) throw new InvalidOperationException($"family '{famName}' isn't loaded");
                var sym = famSyms.FirstOrDefault(s => s.Name == typeName);
                if (sym is null)
                {
                    sym = (FamilySymbol)famSyms[0].Duplicate(typeName);
                    SetLength(sym, "Width", wmm); SetLength(sym, "Height", hmm);
                    symbols.Add(sym);
                }
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

                var pt = new XYZ(P.Dbl(o, "x") * ft, P.Dbl(o, "y") * ft, level.Elevation);
                var host = walls.Select(w => (w, d: ((LocationCurve)w.Location).Curve.Distance(new XYZ(pt.X, pt.Y, ((LocationCurve)w.Location).Curve.GetEndPoint(0).Z))))
                    .Where(t => t.d < 0.4 * ft).OrderBy(t => t.d).Select(t => t.w).FirstOrDefault()
                    ?? throw new InvalidOperationException("no wall within 0.4 m");
                var curve = ((LocationCurve)host.Location).Curve;
                var proj = curve.Project(new XYZ(pt.X, pt.Y, curve.GetEndPoint(0).Z)).XYZPoint;
                var at = new XYZ(proj.X, proj.Y, level.Elevation);
                var inst = doc.Create.NewFamilyInstance(at, sym, host, level, StructuralType.NonStructural);
                doc.Regenerate();
                var sill = P.Dbl(o, "sillRL") * ft - level.ProjectElevation;
                inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
                inst.LookupParameter("Wall Thickness")?.Set(host.Width);
                if (phase is not null) inst.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
                if (o["outside"] is JsonArray outPt)
                {
                    var outside = new XYZ(P.DblFrom(outPt[0], "x") * ft, P.DblFrom(outPt[1], "y") * ft, at.Z);
                    if (inst.FacingOrientation.DotProduct(outside - at) < 0) inst.flipFacing();
                }
                if (o["params"] is JsonObject extra)
                    foreach (var kv in extra)
                    {
                        var prm = inst.LookupParameter(kv.Key) ?? inst.Symbol.LookupParameter(kv.Key);
                        if (prm is null || prm.IsReadOnly) continue;
                        if (prm.StorageType == StorageType.Integer) prm.Set(kv.Value is JsonValue jv && jv.TryGetValue<bool>(out var b) ? (b ? 1 : 0) : P.IntFrom(kv.Value, kv.Key));
                        else if (prm.StorageType == StorageType.Double) prm.Set(P.DblFrom(kv.Value, kv.Key) / 304.8);
                        else if (prm.StorageType == StorageType.String) prm.Set(P.StrFrom(kv.Value, kv.Key));
                    }
                if (P.StrOrNull(o, "comments") is { } c) inst.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(c);
                created.Add(inst.Id.Value);
                row["id"] = inst.Id.Value; row["type"] = typeName; row["hostWall"] = host.Id.Value;
                row["wallThicknessMm"] = Math.Round(host.Width * 304.8);
            }
            catch (Exception ex) { row["error"] = ex.Message; }
            rows.Add(row);
        }
        return new JsonObject { ["affected"] = Affected.Created(created), ["placed"] = created.Count, ["openings"] = rows };
    }

    private static void SetLength(ElementType t, string name, double mm)
    {
        var prm = t.LookupParameter(name);
        if (prm is { IsReadOnly: false }) prm.Set(mm / 304.8);
    }
}
