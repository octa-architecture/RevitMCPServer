using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Translate one element by a vector.
///
/// Parameters:
///   - id:          long, required
///   - translation: { x, y, z? }, required (in user units)
///   - units:       "meters"|"feet", default "meters"
/// </summary>
public sealed class MoveElementCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "move_element";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var units = P.Units(p);

        var id = new ElementId(P.Long(p, "id"));
        var element = doc.GetElement(id)
            ?? throw new RevitCommandException("not_found", $"No element with id {id.Value}.");

        var translation = P.Xyz(p, "translation", units);

        // Anchor (location point / curve midpoint) moves exactly by the translation — the read-back
        // compares against it after the commit.
        var anchorBefore = ReadBack.Anchor(element);

        // Capture before-position from bounding box.
        var bbBefore = element.get_BoundingBox(null);
        var beforeCenter = bbBefore is not null
            ? new JsonObject { ["x"] = (bbBefore.Min.X + bbBefore.Max.X) / 2, ["y"] = (bbBefore.Min.Y + bbBefore.Max.Y) / 2, ["z"] = (bbBefore.Min.Z + bbBefore.Max.Z) / 2 }
            : null;

        ElementTransformUtils.MoveElement(doc, id, translation);

        var bbAfter = element.get_BoundingBox(null);
        var afterCenter = bbAfter is not null
            ? new JsonObject { ["x"] = (bbAfter.Min.X + bbAfter.Max.X) / 2, ["y"] = (bbAfter.Min.Y + bbAfter.Max.Y) / 2, ["z"] = (bbAfter.Min.Z + bbAfter.Max.Z) / 2 }
            : null;

        return new JsonObject
        {
            ["id"] = id.Value,
            ["affected"] = Affected.Modified(id.Value),
            ["name"] = element.Name,
            ["translationFeet"] = new JsonObject
            {
                ["x"] = translation.X,
                ["y"] = translation.Y,
                ["z"] = translation.Z,
            },
            ["changes"] = new JsonObject
            {
                ["beforeCenter"] = beforeCenter,
                ["afterCenter"] = afterCenter,
                ["beforeAnchorFeet"] = anchorBefore is null ? null : ReadBack.Xyz(anchorBefore),
            },
            ["changeSummary"] = $"Moved element {id.Value} ('{element.Name}') by ({translation.X:F2}, {translation.Y:F2}, {translation.Z:F2}) ft",
        };
    }
    /// <summary>
    /// The anchor must sit at its pre-move position plus the translation, compared in feet whatever
    /// <c>units</c> the caller used — a constrained or hosted element that did not move fully fails.
    /// </summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result)
    {
        var doc = ctx.RequireDoc();
        var id = result["id"]!.GetValue<long>();
        var el = doc.GetElement(new ElementId(id));
        if (el is null) return VerifyResult.Fail($"element {id} no longer exists after commit");
        var before = ReadBack.Xyz((result["changes"] as JsonObject)?["beforeAnchorFeet"]);
        var t = ReadBack.Xyz(result["translationFeet"]);
        if (before is null || t is null) return VerifyResult.Skip("element has no location to compare");
        var m = ReadBack.Point("position", before + t, ReadBack.Anchor(el));
        return m is null ? VerifyResult.Pass() : VerifyResult.Fail(m);
    }
}
