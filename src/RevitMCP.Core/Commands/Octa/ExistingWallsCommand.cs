using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Model surveyed walls in one go: each wall is a centreline with its surveyed thickness and top RL.
/// Wall types are found or made per thickness ("{prefix}NNN", single layer, duplicated from a basic
/// wall), so existing walls keep their measured widths. Params (metres, internal): walls
/// [{x1,y1,x2,y2,thicknessMm,topRL,baseRL?}], levelName, phase? "Existing", typePrefix? "EX - Wall ",
/// baseTypeName? (a basic wall type to copy), roundMm? (10).
/// </summary>
public sealed class CreateWallsBatchCommand : IRevitCommand
{
    public string Name => "create_walls_batch";
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
        var prefix = P.StrOrNull(p, "typePrefix") ?? "EX - Wall ";
        var round = P.DblOr(p, "roundMm", 10);
        var basics = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .Where(t => t.Kind == WallKind.Basic).ToList();
        var baseType = (P.StrOrNull(p, "baseTypeName") is { } bn ? basics.FirstOrDefault(t => t.Name == bn) : null)
            ?? basics.FirstOrDefault(t => t.Name.StartsWith("Generic", StringComparison.OrdinalIgnoreCase))
            ?? basics.First();
        var phase = doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(P.StrOrNull(p, "phase") ?? "Existing", StringComparison.OrdinalIgnoreCase));

        var types = new Dictionary<int, WallType>();
        var createdTypes = new List<long>();
        WallType TypeFor(double mm)
        {
            int w = (int)(Math.Round(mm / round) * round);
            if (types.TryGetValue(w, out var t)) return t;
            var name = $"{prefix}{w:000}";
            t = basics.FirstOrDefault(x => x.Name == name);
            if (t is null)
            {
                t = (WallType)baseType.Duplicate(name);
                var cs = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure, w / 304.8,
                    baseType.GetCompoundStructure()?.GetLayers().FirstOrDefault()?.MaterialId ?? ElementId.InvalidElementId);
                t.SetCompoundStructure(cs);
                basics.Add(t); createdTypes.Add(t.Id.Value);
            }
            return types[w] = t;
        }

        var ids = new List<long>(); var failed = new JsonArray();
        int i = 0;
        foreach (var n in P.Arr(p, "walls"))
        {
            var o = (JsonObject)n!;
            try
            {
                var a = new XYZ(P.Dbl(o, "x1") * ft, P.Dbl(o, "y1") * ft, level.Elevation);
                var b = new XYZ(P.Dbl(o, "x2") * ft, P.Dbl(o, "y2") * ft, level.Elevation);
                var baseOff = o["baseRL"] is not null ? P.Dbl(o, "baseRL") * ft - level.ProjectElevation : 0;
                var height = P.Dbl(o, "topRL") * ft - level.ProjectElevation - baseOff;
                if (height < 0.3) throw new InvalidOperationException("top is below the base");
                var wall = Wall.Create(doc, Line.CreateBound(a, b), TypeFor(P.Dbl(o, "thicknessMm")).Id, level.Id, height, baseOff, false, false);
                wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
                if (phase is not null) wall.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
                if (P.StrOrNull(o, "comments") is { } c) wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(c);
                ids.Add(wall.Id.Value);
            }
            catch (Exception ex) { failed.Add($"wall {i}: {ex.Message}"); }
            i++;
        }
        return new JsonObject
        {
            ["affected"] = Affected.Of(created: ids.Concat(createdTypes)), ["walls"] = ids.Count, ["failed"] = failed,
            ["types"] = new JsonArray(types.OrderBy(k => k.Key).Select(k => (JsonNode)JsonValue.Create(k.Value.Name)!).ToArray()),
            ["wallIds"] = new JsonArray(ids.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        };
    }
}
