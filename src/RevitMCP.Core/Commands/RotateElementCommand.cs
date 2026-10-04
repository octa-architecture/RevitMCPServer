using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Rotate an element around a vertical axis through a point.
///
/// Params:
///   - id:         long, required
///   - center:     { x, y, z? }, rotation axis origin
///   - angleDeg:   number, rotation angle in degrees (counter-clockwise)
///   - units:      "meters"|"feet"
/// </summary>
public sealed class RotateElementCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "rotate_element";
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

        var center = P.Xyz(p, "center", units);
        var angleDeg = P.Dbl(p, "angleDeg");
        var angleRad = angleDeg * Math.PI / 180.0;

        var axis = Line.CreateBound(center, center + XYZ.BasisZ);
        var anchorBefore = ReadBack.Anchor(element);
        var angleBefore = ReadBack.PlanAngle(element);
        ElementTransformUtils.RotateElement(doc, id, axis, angleRad);
        return new JsonObject
        {
            ["id"] = id.Value,
            ["affected"] = Affected.Modified(id.Value),
            ["angleDeg"] = angleDeg,
            ["changes"] = new JsonObject
            {
                ["centerFeet"] = ReadBack.Xyz(center),
                ["beforeAnchorFeet"] = anchorBefore is null ? null : ReadBack.Xyz(anchorBefore),
                ["beforePlanAngleRad"] = angleBefore,
            },
            ["changeSummary"] = $"Rotated element {id.Value} ('{element.Name}') by {angleDeg:F1}° around ({center.X:F2}, {center.Y:F2}) ft",
        };
    }
    /// <summary>
    /// The anchor must be the pre-rotation anchor turned about the requested centre, and (when the
    /// element has an orientation) its plan angle must have changed by the requested angle.
    /// </summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result)
    {
        var doc = ctx.RequireDoc();
        var id = result["id"]!.GetValue<long>();
        var el = doc.GetElement(new ElementId(id));
        if (el is null) return VerifyResult.Fail($"element {id} no longer exists after commit");
        var changes = result["changes"] as JsonObject;
        var angle = result["angleDeg"]!.GetValue<double>() * Math.PI / 180.0;
        var mismatches = new List<string>();
        var before = ReadBack.Xyz(changes?["beforeAnchorFeet"]);
        var center = ReadBack.Xyz(changes?["centerFeet"]);
        if (before is not null && center is not null &&
            ReadBack.Point("position", ReadBack.RotateAboutZ(before, center, angle), ReadBack.Anchor(el)) is string pm)
            mismatches.Add(pm);
        var a0 = changes?["beforePlanAngleRad"]?.GetValue<double>();
        var a1 = ReadBack.PlanAngle(el);
        if (a0 is double b && a1 is double now)
        {
            var turned = ReadBack.AngleDelta(now, b);
            if (Math.Abs(ReadBack.AngleDelta(turned, angle)) > ReadBack.AngleTolRad)
                mismatches.Add($"rotation: expected {angle * 180 / Math.PI:0.###}°, stored change {turned * 180 / Math.PI:0.###}°");
        }
        return ReadBack.FromMismatches(mismatches);
    }
}
