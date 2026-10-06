using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Replace "fake" leaders — a detail line from a note to its target plus two short arrowhead
/// strokes — with real text leaders to the same arrow tip, then delete the drawn lines.
/// Only notes WITHOUT leaders are considered. A line counts as a note's leader when one end is
/// within maxGapMm (paper) of the note's box and it is the longest such line; arrowhead strokes are
/// lines shorter than arrowMaxMm (paper) with an end at the tip.
/// Params: viewId?, maxGapMm? (3), arrowMaxMm? (4). Use dryRun to see the matches first.
/// </summary>
public sealed class ConvertLineLeadersCommand : IRevitCommand
{
    public string Name => "convert_line_leaders";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = OctaUtil.ResolveView(doc, p);
        double paper = view.Scale / 304.8; // 1 mm on paper, in model feet
        var gap = P.DblOr(p, "maxGapMm", 3) * paper;
        var arrowMax = P.DblOr(p, "arrowMaxMm", 4) * paper;

        var lines = new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement)).OfType<DetailCurve>()
            .Where(c => c.GeometryCurve is Line).ToList();
        var shortLines = lines.Where(l => l.GeometryCurve.Length <= arrowMax).ToList();
        var used = new HashSet<ElementId>();
        var converted = new JsonArray();
        var skipped = new JsonArray();
        var toDelete = new List<ElementId>();

        // Arrowhead strokes ending at a point (a fake leader always has two).
        List<DetailCurve> ArrowsAt(XYZ tip) => shortLines.Where(l =>
            l.GeometryCurve.GetEndPoint(0).DistanceTo(tip) < paper * 0.3 ||
            l.GeometryCurve.GetEndPoint(1).DistanceTo(tip) < paper * 0.3).ToList();

        // Every (note, line) candidate: line has one end near the note's box and arrowheads at the other.
        var notes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
            .Where(n => n.LeaderCount == 0).ToList();
        var candidates = new List<(TextNote note, DetailCurve line, XYZ tip, double dist, List<DetailCurve> arrows, double uMid)>();
        foreach (var note in notes)
        {
            var box = LeaderGeom.TextBox(view, note);
            double uMin = box.left, uMax = box.right, vMin = box.bottom, vMax = box.top;
            double DistToBox(XYZ q)
            {
                var u = LeaderGeom.U(view, q); var v = LeaderGeom.V(view, q);
                var du = Math.Max(0, Math.Max(uMin - u, u - uMax));
                var dv = Math.Max(0, Math.Max(vMin - v, v - vMax));
                return Math.Sqrt(du * du + dv * dv);
            }
            foreach (var l in lines)
            {
                var c = l.GeometryCurve;
                if (c.Length <= arrowMax) continue;
                var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
                double da = DistToBox(a), db = DistToBox(b);
                if (Math.Min(da, db) > gap || Math.Max(da, db) <= gap) continue;
                var tip = da < db ? b : a;
                var arrows = ArrowsAt(tip);
                if (arrows.Count < 2) continue; // no arrowhead: it's drawing linework, not a leader
                candidates.Add((note, l, tip, Math.Min(da, db), arrows, (uMin + uMax) / 2));
            }
        }

        // Greedy best-first assignment: closest line to its note wins; each note and line used once.
        var doneNotes = new HashSet<ElementId>();
        foreach (var cnd in candidates.OrderBy(c => c.dist))
        {
            if (doneNotes.Contains(cnd.note.Id) || used.Contains(cnd.line.Id) || cnd.arrows.Any(a => used.Contains(a.Id))) continue;
            doneNotes.Add(cnd.note.Id);
            used.Add(cnd.line.Id);
            toDelete.Add(cnd.line.Id);
            foreach (var a in cnd.arrows) { used.Add(a.Id); toDelete.Add(a.Id); }
            var note = cnd.note;
            var tip = cnd.tip;
            var arrows = cnd.arrows;
            var best = (line: cnd.line, a: XYZ.Zero, b: XYZ.Zero);
            var right = LeaderGeom.U(view, tip) > cnd.uMid;
            var leader = note.AddLeader(right ? TextNoteLeaderTypes.TNLT_STRAIGHT_R : TextNoteLeaderTypes.TNLT_STRAIGHT_L);
            leader.End = tip;
            LeaderGeom.Orthogonalise(view, leader);
            converted.Add(new JsonObject
            {
                ["noteId"] = note.Id.Value,
                ["text"] = Short(note.Text),
                ["leaderLine"] = best.line.Id.Value,
                ["arrowStrokes"] = arrows.Count,
            });
        }
        foreach (var n in notes.Where(n => !doneNotes.Contains(n.Id)))
            skipped.Add(new JsonObject { ["noteId"] = n.Id.Value, ["text"] = Short(n.Text), ["reason"] = "no drawn leader (line with arrowhead) found — label or already clean" });

        if (toDelete.Count > 0) doc.Delete(toDelete);
        return new JsonObject
        {
            ["affected"] = Affected.Of(modified: converted.Select(c => c!["noteId"]!.GetValue<long>()), deleted: toDelete.Select(i => i.Value)),
            ["viewId"] = view.Id.Value,
            ["converted"] = converted.Count,
            ["linesDeleted"] = toDelete.Count,
            ["notes"] = converted,
            ["skipped"] = skipped,
        };
    }

    private static string Short(string s)
    {
        s = s.Replace("\r", " ").Trim();
        return s.Length > 40 ? s[..40] + "…" : s;
    }
}
