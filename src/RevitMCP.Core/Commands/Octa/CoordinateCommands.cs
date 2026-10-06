using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Convert points between internal model coordinates and the project's shared (survey) coordinates.
/// Params: points [[x,y,z],...] in metres, toShared? (default true). Used to prove a survey placement:
/// a TBM's model position must convert back to its surveyed MGA coordinates.
/// </summary>
public sealed class ConvertCoordinatesCommand : IRevitCommand
{
    public string Name => "convert_coordinates";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var sharedToInternal = doc.ActiveProjectLocation.GetTotalTransform();
        var toShared = P.BoolOr(p, "toShared", true);
        var xf = toShared ? sharedToInternal.Inverse : sharedToInternal;
        var outPts = new JsonArray();
        foreach (var n in P.Arr(p, "points"))
        {
            if (n is not JsonArray a || a.Count < 2) continue;
            var q = new XYZ(P.DblFrom(a[0], "x") * P.MetersToFeet, P.DblFrom(a[1], "y") * P.MetersToFeet,
                (a.Count > 2 ? P.DblFrom(a[2], "z") : 0) * P.MetersToFeet);
            var r = xf.OfPoint(q);
            outPts.Add(new JsonArray(Math.Round(r.X * P.FeetToMeters, 4), Math.Round(r.Y * P.FeetToMeters, 4), Math.Round(r.Z * P.FeetToMeters, 4)));
        }
        var pos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        return new JsonObject
        {
            ["toShared"] = toShared,
            ["points"] = outPts,
            ["angleToTrueNorthDeg"] = Math.Round(pos.Angle * 180 / Math.PI, 6),
        };
    }
}
