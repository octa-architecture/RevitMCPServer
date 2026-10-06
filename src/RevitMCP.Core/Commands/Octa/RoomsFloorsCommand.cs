using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Rooms and floors from surveyed floor levels: for each point {x,y,ffl,name?} (metres, internal)
/// the enclosed wall circuit on the level becomes a room (in the given phase), and a floor is made
/// from the room boundary with its top at exactly the surveyed FFL. Params: points, levelName,
/// phase? "Existing", floorTypeName? (first floor type), makeFloors? (true).
/// </summary>
public sealed class CreateRoomsAndFloorsCommand : IRevitCommand
{
    public string Name => "create_rooms_and_floors";
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
        var phase = doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(P.StrOrNull(p, "phase") ?? "Existing", StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found", "Phase not found.");
        var floorType = (P.StrOrNull(p, "floorTypeName") is { } fn
            ? new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(t => t.Name == fn) : null)
            ?? new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t => !t.IsFoundationSlab);
        var makeFloors = P.BoolOr(p, "makeFloors", true);

        // replace=true: clear this level's rooms, room separation lines and floors (in this phase)
        // from a previous run first, inside the same transaction.
        int cleared = 0;
        if (P.BoolOr(p, "replace", false))
        {
            var old = new List<ElementId>();
            old.AddRange(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Cast<SpatialElement>().Where(r => r.LevelId == level.Id || r.Location is null).Select(r => r.Id));
            old.AddRange(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomSeparationLines).WhereElementIsNotElementType()
                .Where(e => e is CurveElement ce && Math.Abs(ce.GeometryCurve.GetEndPoint(0).Z - level.Elevation) < 0.1).Select(e => e.Id));
            old.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
                .Where(f => f.LevelId == level.Id && f.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() == phase.Id).Select(f => f.Id));
            foreach (var id in old) { try { if (doc.GetElement(id) is not null) { doc.Delete(id); cleared++; } } catch { } }
            doc.Regenerate();
        }

        // Door/window gaps leave walls unclosed: bridge collinear gaps with room separation lines.
        var sepIds = new List<long>();
        var gap = P.DblOr(p, "closeGapsM", 1.5) * ft;
        if (gap > 0)
        {
            var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.GenLevel?.Id == level.Id && v.ViewType == ViewType.FloorPlan)
                ?? throw new RevitCommandException("not_found", "Need a floor plan of the level for room separation lines.");
            var lines = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.LevelId == level.Id && w.Location is LocationCurve { Curve: Line })
                // chimney breasts and other thick stubs are not openings: leave them out of the gap rules
                .Where(w => w.Width <= 0.35 * ft)
                .Select(w => (Line)((LocationCurve)w.Location).Curve).ToList();
            var arr = new CurveArray();
            var done = new HashSet<string>();
            for (int i = 0; i < lines.Count; i++)
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var a = lines[i]; var b = lines[j];
                    if (Math.Abs(a.Direction.CrossProduct(b.Direction).Z) > 0.03) continue;         // parallel
                    var off = (b.GetEndPoint(0) - a.GetEndPoint(0)).CrossProduct(a.Direction).Z;
                    if (Math.Abs(off) > 0.35) continue;                                                // collinear-ish
                    // closest pair of ends
                    XYZ? best0 = null, best1 = null; double bd = double.MaxValue;
                    foreach (var ea in new[] { a.GetEndPoint(0), a.GetEndPoint(1) })
                        foreach (var eb in new[] { b.GetEndPoint(0), b.GetEndPoint(1) })
                        {
                            var d = Flat(ea).DistanceTo(Flat(eb));
                            if (d < bd) { bd = d; best0 = ea; best1 = eb; }
                        }
                    if (bd < 0.05 || bd > gap) continue;
                    // the gap must lie between the walls, not overlap them
                    var mid = (best0! + best1!) / 2;
                    if (a.Distance(mid) < bd / 2 - 0.01 || b.Distance(mid) < bd / 2 - 0.01) continue;
                    var key = $"{Math.Round(mid.X, 2)},{Math.Round(mid.Y, 2)}";
                    if (!done.Add(key)) continue;
                    arr.Append(Line.CreateBound(new XYZ(best0!.X, best0.Y, level.Elevation), new XYZ(best1!.X, best1.Y, level.Elevation)));
                }
            // Free wall ends facing another wall across an opening (e.g. a door between a wall end
            // and a perpendicular wall): extend straight ahead to the first wall within the gap.
            foreach (var a in lines)
                for (int end = 0; end < 2; end++)
                {
                    var e = a.GetEndPoint(end);
                    if (lines.Any(o => !ReferenceEquals(o, a) && o.Distance(e) < 0.05)) continue;   // already joined
                    var dir = end == 1 ? a.Direction : a.Direction.Negate();
                    XYZ? hit = null; double hd = double.MaxValue;
                    foreach (var o in lines)
                    {
                        if (ReferenceEquals(o, a)) continue;
                        // 2D ray (e + t·dir, 0.02 < t ≤ gap) against segment o
                        XYZ o0 = o.GetEndPoint(0), o1 = o.GetEndPoint(1);
                        double sx = o1.X - o0.X, sy = o1.Y - o0.Y;
                        double den = dir.X * sy - dir.Y * sx;
                        if (Math.Abs(den) < 1e-9) continue;
                        double t = ((o0.X - e.X) * sy - (o0.Y - e.Y) * sx) / den;
                        double u = ((o0.X - e.X) * dir.Y - (o0.Y - e.Y) * dir.X) / den;
                        if (t <= 0.02 || t > gap || u < -0.01 || u > 1.01) continue;
                        if (t < hd) { hd = t; hit = new XYZ(e.X + dir.X * t, e.Y + dir.Y * t, e.Z); }
                    }
                    if (hit is null || hd < 0.05) continue;
                    var key = $"{Math.Round((e.X + hit.X) / 2, 2)},{Math.Round((e.Y + hit.Y) / 2, 2)}";
                    if (!done.Add(key)) continue;
                    arr.Append(Line.CreateBound(new XYZ(e.X, e.Y, level.Elevation), new XYZ(hit.X, hit.Y, level.Elevation)));
                }
            // Remaining free ends: join each to the nearest other free end within the gap.
            var free = lines.SelectMany(a => new[] { a.GetEndPoint(0), a.GetEndPoint(1) }
                    .Where(e => !lines.Any(o => !ReferenceEquals(o, a) && o.Distance(e) < 0.05)))
                .ToList();
            var paired = new HashSet<int>();
            for (int i = 0; i < free.Count; i++)
            {
                if (paired.Contains(i)) continue;
                int bj = -1; double bd = gap;
                for (int j = 0; j < free.Count; j++)
                {
                    if (j == i || paired.Contains(j)) continue;
                    var d = Flat(free[i]).DistanceTo(Flat(free[j]));
                    if (d > 0.05 && d < bd) { bd = d; bj = j; }
                }
                if (bj < 0) continue;
                var key = $"{Math.Round((free[i].X + free[bj].X) / 2, 2)},{Math.Round((free[i].Y + free[bj].Y) / 2, 2)}";
                if (!done.Add(key)) continue;
                paired.Add(i); paired.Add(bj);
                arr.Append(Line.CreateBound(new XYZ(free[i].X, free[i].Y, level.Elevation), new XYZ(free[bj].X, free[bj].Y, level.Elevation)));
            }
            if (!arr.IsEmpty)
            {
                var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));
                var made = doc.Create.NewRoomBoundaryLines(sp, arr, plan);
                foreach (ModelCurve mc in made)
                {
                    mc.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
                    sepIds.Add(mc.Id.Value);
                }
            }
        }
        doc.Regenerate();

        // One room per enclosed circuit that holds a survey point.
        var pts = P.Arr(p, "points").Select(n => (JsonObject)n!).Select(o => (
            pt: new XYZ(P.Dbl(o, "x") * ft, P.Dbl(o, "y") * ft, level.Elevation + 1.0),
            ffl: P.Dbl(o, "ffl"), name: P.StrOrNull(o, "name"))).ToList();
        var topo = doc.get_PlanTopology(level, phase);
        var rooms = new List<(Room room, List<(XYZ pt, double ffl, string? name)> hits)>();
        foreach (PlanCircuit c in topo.Circuits)
        {
            if (c.IsRoomLocated) continue;
            var room = doc.Create.NewRoom(phase);
            doc.Create.NewRoom(room, c);
            doc.Regenerate();
            // Survey labels sit beside their point: accept a label within ~0.6 m of the room.
            var near = new[] { XYZ.Zero, new XYZ(0.6 * ft, 0, 0), new XYZ(-0.6 * ft, 0, 0), new XYZ(0, 0.6 * ft, 0), new XYZ(0, -0.6 * ft, 0) };
            var hits = pts.Where(q => near.Any(d => room.IsPointInRoom(q.pt + d))).ToList();
            if (hits.Count == 0 || room.Area < 0.5) { doc.Delete(room.Id); continue; }
            rooms.Add((room, hits));
        }

        var outRooms = new JsonArray(); var created = new List<long>(); var unmatched = new JsonArray();
        foreach (var q in pts.Where(q => rooms.All(r => !r.hits.Contains(q))))
            unmatched.Add($"{q.name ?? "point"} FFL {q.ffl} at ({q.pt.X * P.FeetToMeters:N2},{q.pt.Y * P.FeetToMeters:N2}) is not inside an enclosed room");
        var opts = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        foreach (var (room, hits) in rooms)
        {
            var ffl = hits.Average(h => h.ffl);
            if (hits.FirstOrDefault(h => h.name is not null).name is { } nm) room.Name = nm;
            created.Add(room.Id.Value);
            var row = new JsonObject { ["roomId"] = room.Id.Value, ["name"] = room.Name, ["areaM2"] = Math.Round(room.Area * P.FeetToMeters * P.FeetToMeters, 2), ["ffl"] = Math.Round(ffl, 3), ["surveyPoints"] = hits.Count };
            if (makeFloors)
            {
                var loops = room.GetBoundarySegments(opts);
                if (loops.Count > 0)
                {
                    try
                    {
                        // Outer boundary only; tiny gaps between segments are closed by rebuilding
                        // the loop from consecutive segment start points.
                        var segs = loops[0].Select(s => s.GetCurve()).ToList();
                        var poly = segs.Select(c => c.GetEndPoint(0)).ToList();
                        var cl = new CurveLoop();
                        for (int k = 0; k < poly.Count; k++)
                        {
                            var a = poly[k]; var b = poly[(k + 1) % poly.Count];
                            if (a.DistanceTo(b) > doc.Application.ShortCurveTolerance) cl.Append(Line.CreateBound(a, b));
                        }
                        var curveLoops = new List<CurveLoop> { cl };
                        var floor = Floor.Create(doc, curveLoops, floorType.Id, level.Id);
                        floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(ffl * ft - level.ProjectElevation);
                        floor.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
                        floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"FFL {ffl:0.000} (survey)");
                        created.Add(floor.Id.Value); row["floorId"] = floor.Id.Value;
                    }
                    catch (Exception ex) { row["floorError"] = ex.Message; }
                }
            }
            outRooms.Add(row);
        }
        created.AddRange(sepIds);
        return new JsonObject { ["affected"] = Affected.Created(created), ["rooms"] = outRooms, ["unmatched"] = unmatched, ["separationLines"] = sepIds.Count, ["cleared"] = cleared };
    }

    private static XYZ Flat(XYZ q) => new(q.X, q.Y, 0);
}
