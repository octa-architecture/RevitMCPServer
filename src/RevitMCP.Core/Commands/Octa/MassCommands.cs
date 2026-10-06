using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Simple context massing (neighbours, existing outbuildings) as a DirectShape: a footprint
/// extruded from baseZ to topZ, optionally with a gable or skillion roof cut. Heights are absolute
/// (metres, internal Z = AHD on OCTA site models) so they can be set straight from surveyed RLs.
/// Params: points [[x,y],...] (m, internal), baseZ, topZ (wall/parapet/eave top), name,
///   roof? { type: "gable"|"skillion", ridgeZ, axis: "x"|"y" (ridge direction; gable),
///           lowSide: "n"|"s"|"e"|"w" (skillion) },
///   category? "Mass" (default) | "Generic Models", phase? "Existing" (default), comments?.
/// </summary>
public sealed class CreateMassCommand : IRevitCommand
{
    public string Name => "create_mass";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var ft = P.MetersToFeet;
        var pts = P.Arr(p, "points").Select(n => n as JsonArray ?? throw new RevitCommandException("invalid_parameter", "points must be [[x,y],...]"))
            .Select(a => new XYZ(P.DblFrom(a[0], "x") * ft, P.DblFrom(a[1], "y") * ft, 0)).ToList();
        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) < 1e-6) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) throw new RevitCommandException("invalid_parameter", "Need at least 3 footprint points.");
        var baseZ = P.Dbl(p, "baseZ") * ft;
        var topZ = P.Dbl(p, "topZ") * ft;
        var roof = p["roof"] as JsonObject;
        var maxZ = roof is not null ? Math.Max(topZ, P.Dbl(roof, "ridgeZ") * ft) : topZ;
        if (maxZ - baseZ < 0.01) throw new RevitCommandException("invalid_parameter", "topZ must be above baseZ.");

        var body = Prism(pts, baseZ, maxZ);
        if (roof is not null)
        {
            var ridgeZ = P.Dbl(roof, "ridgeZ") * ft;
            double minX = pts.Min(q => q.X) - 1, maxX = pts.Max(q => q.X) + 1, minY = pts.Min(q => q.Y) - 1, maxY = pts.Max(q => q.Y) + 1;
            var type = (P.StrOrNull(roof, "type") ?? "gable").ToLowerInvariant();
            Solid cutter;
            if (type == "gable")
            {
                // Triangular prism: eaves (topZ) on both long edges of the bbox, ridge at the centre line.
                var alongX = (P.StrOrNull(roof, "axis") ?? "x").Equals("x", StringComparison.OrdinalIgnoreCase);
                double a0 = alongX ? minY + 1 : minX + 1, a1 = alongX ? maxY - 1 : maxX - 1, mid = (a0 + a1) / 2;
                double s = (ridgeZ - topZ) / ((a1 - a0) / 2);           // slope per foot
                double e0 = a0 - 1, e1 = a1 + 1, ez = topZ - s;          // extend eave line out 1 ft so the cut covers the footprint
                XYZ P2(double a, double z) => alongX ? new XYZ(minX, a, z) : new XYZ(a, minY, z);
                var low = Math.Min(baseZ, ez) - 1;
                var under = Loop(new List<XYZ> { P2(e0, low), P2(e1, low), P2(e1, ez), P2(mid, ridgeZ), P2(e0, ez) });
                var dir = alongX ? XYZ.BasisX : XYZ.BasisY;
                var len = alongX ? maxX - minX : maxY - minY;
                cutter = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { under }, dir, len);
            }
            else
            {
                // Skillion: a plane from ridgeZ on the high side down to topZ on the low side.
                var lowSide = (P.StrOrNull(roof, "lowSide") ?? "s").ToLowerInvariant();
                var alongY = lowSide is "n" or "s";
                double a0 = alongY ? minY + 1 : minX + 1, a1 = alongY ? maxY - 1 : maxX - 1;
                bool lowAtStart = lowSide is "s" or "w";
                double zStart = lowAtStart ? topZ : ridgeZ, zEnd = lowAtStart ? ridgeZ : topZ;
                XYZ P2(double a, double z) => alongY ? new XYZ(minX, a, z) : new XYZ(a, minY, z);
                var low = baseZ - 1;
                var under = Loop(new List<XYZ> { P2(a0 - 1, low), P2(a1 + 1, low), P2(a1 + 1, zEnd), P2(a0 - 1, zStart) });
                cutter = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { under }, alongY ? XYZ.BasisX : XYZ.BasisY,
                    alongY ? maxX - minX : maxY - minY);
            }
            body = BooleanOperationsUtils.ExecuteBooleanOperation(body, cutter, BooleanOperationsType.Intersect);
        }

        var catName = P.StrOrNull(p, "category") ?? "Mass";
        var bic = catName.Equals("Generic Models", StringComparison.OrdinalIgnoreCase) ? BuiltInCategory.OST_GenericModel : BuiltInCategory.OST_Mass;
        var ds = DirectShape.CreateElement(doc, new ElementId(bic));
        ds.SetShape(new GeometryObject[] { body });
        ds.Name = P.StrOrNull(p, "name") ?? "Context mass";
        var phaseName = P.StrOrNull(p, "phase") ?? "Existing";
        var phase = doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(phaseName, StringComparison.OrdinalIgnoreCase));
        if (phase is not null) ds.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
        var comments = P.StrOrNull(p, "comments") ?? ds.Name;
        ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comments);
        return new JsonObject
        {
            ["affected"] = Affected.Created(ds.Id.Value), ["id"] = ds.Id.Value, ["name"] = ds.Name,
            ["volumeM3"] = Math.Round(body.Volume * Math.Pow(P.FeetToMeters, 3), 2),
            ["topRL"] = Math.Round(maxZ * P.FeetToMeters, 3),
        };
    }

    internal static CurveLoop Loop(IList<XYZ> pts)
    {
        var loop = new CurveLoop();
        for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        return loop;
    }

    private static Solid Prism(List<XYZ> pts, double z0, double z1)
    {
        var loop = Loop(pts.Select(q => new XYZ(q.X, q.Y, z0)).ToList());
        if (!loop.IsCounterclockwise(XYZ.BasisZ)) loop.Flip();
        return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, z1 - z0);
    }
}
