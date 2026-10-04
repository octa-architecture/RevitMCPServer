using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Mirror elements across a plane. The plane is defined by a point on the
/// plane and a normal vector.
///
/// Params:
///   - ids:      long[], required
///   - origin:   { x, y, z? }, point on the mirror plane
///   - normal:   { x, y, z }, normal direction of the plane (e.g. {1,0,0} for YZ mirror)
///   - copy:     bool, default true (false = move, true = copy + mirror)
///   - units:    "meters"|"feet"
/// </summary>
public sealed class MirrorElementCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "mirror_element";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var idsArr = P.Arr(p, "ids");
        var ids = new List<ElementId>();
        for (var i = 0; i < idsArr.Count; i++)
            ids.Add(new ElementId(P.LongFrom(idsArr[i], $"ids[{i}]")));

        var origin = P.Xyz(p, "origin", units);
        var normal = P.Xyz(p, "normal", "feet"); // unitless direction
        if (normal.GetLength() < 1e-9)
            throw new RevitCommandException("invalid_parameter", "Normal vector must be non-zero.");

        var plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), origin);
        var copy = P.BoolOr(p, "copy", true);

        if (copy)
        {
            var newIds = ElementTransformUtils.MirrorElements(doc, ids, plane, true);
            var arr = new JsonArray();
            foreach (var id in newIds) arr.Add(id.Value);
            return new JsonObject
            {
                ["mirrored"] = ids.Count,
                ["copied"] = true,
                ["newIds"] = arr,
                ["affected"] = Affected.Created(newIds.Select(n => n.Value)),
            };
        }
        else
        {
            var anchors = new JsonObject();
            foreach (var id in ids)
                if (doc.GetElement(id) is Element e && ReadBack.Anchor(e) is XYZ a)
                    anchors[id.Value.ToString()] = ReadBack.Xyz(a);
            ElementTransformUtils.MirrorElements(doc, ids, plane, false);
            return new JsonObject
            {
                ["mirrored"] = ids.Count,
                ["copied"] = false,
                ["affected"] = Affected.Modified(ids.Select(n => n.Value)),
                ["changes"] = new JsonObject
                {
                    ["originFeet"] = ReadBack.Xyz(origin),
                    ["normal"] = ReadBack.Xyz(normal.Normalize()),
                    ["beforeAnchorsFeet"] = anchors,
                },
            };
        }
    }
    /// <summary>
    /// With <c>copy</c>: every new id must exist. Without: each element's anchor must be the reflection
    /// of where it stood before, across the requested plane.
    /// </summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result)
    {
        var doc = ctx.RequireDoc();
        if (result["copied"]?.GetValue<bool>() == true) return ReadBack.AffectedInModel(doc, result);
        var changes = result["changes"] as JsonObject;
        var origin = ReadBack.Xyz(changes?["originFeet"]);
        var normal = ReadBack.Xyz(changes?["normal"]);
        if (origin is null || normal is null || changes?["beforeAnchorsFeet"] is not JsonObject anchors)
            return ReadBack.AffectedInModel(doc, result);
        var mismatches = new List<string>();
        foreach (var kv in anchors)
        {
            var id = long.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture);
            var el = doc.GetElement(new ElementId(id));
            if (el is null) { mismatches.Add($"{id}: missing after commit"); continue; }
            if (ReadBack.Xyz(kv.Value) is XYZ before &&
                ReadBack.Point("position", ReadBack.Reflect(before, origin, normal), ReadBack.Anchor(el)) is string m)
                mismatches.Add($"{id}: {m}");
        }
        return ReadBack.FromMismatches(mismatches, $"{anchors.Count} mirrored element(s) re-read");
    }
}
