using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Leader geometry helpers. OCTA rule: leaders never cross; each leader is a horizontal
/// shoulder from the text then (only if needed) a vertical drop to the arrow — 90°, never diagonal.
/// Coordinates are taken in the view's own right/up axes so this works in sections and plans.
/// </summary>
internal static class LeaderGeom
{
    public static double U(View v, XYZ p) => p.DotProduct(v.RightDirection);
    public static double V(View v, XYZ p) => p.DotProduct(v.UpDirection);

    /// <summary>
    /// The text block alone (no leaders — get_BoundingBox includes them) in view U/V:
    /// Width/Height are sheet units, scaled to model; Coord is the top edge, and the left /
    /// centre / right edge depending on horizontal alignment.
    /// </summary>
    public static (double left, double right, double top, double bottom) TextBox(View view, TextNote n)
    {
        double w = n.Width * view.Scale, h = n.Height * view.Scale;
        double cu = U(view, n.Coord), cv = V(view, n.Coord);
        double left = n.HorizontalAlignment switch
        {
            HorizontalTextAlignment.Center => cu - w / 2,
            HorizontalTextAlignment.Right => cu - w,
            _ => cu,
        };
        double top = n.VerticalAlignment switch
        {
            VerticalTextAlignment.Middle => cv + h / 2,
            VerticalTextAlignment.Bottom => cv + h,
            _ => cv,
        };
        return (left, left + w, top, top - h);
    }

    /// <summary>Put the elbow straight above/below the arrow, level with the text attachment.</summary>
    public static void Orthogonalise(View view, Leader l)
    {
        var anchor = l.Anchor;
        var end = l.End;
        var du = U(view, end) - U(view, anchor);
        var dv = V(view, end) - V(view, anchor);
        const double e = 1e-4;
        if (Math.Abs(du) < e) { l.Elbow = (anchor + end) / 2; return; }   // tip directly below/above: straight drop
        // Level already: straight horizontal leader, elbow midway on it.
        l.Elbow = Math.Abs(dv) < e
            ? anchor + view.RightDirection * (du / 2)
            : anchor + view.RightDirection * du;
    }

    /// <summary>Set a leader's tip, moving the elbow out of the way first if Revit would refuse.</summary>
    public static void SetEnd(Leader l, XYZ end)
    {
        try { l.End = end; }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            l.Elbow = (l.Anchor + end) / 2;
            l.End = end;
        }
    }

    public static List<(double u1, double v1, double u2, double v2)> Segments(View view, TextNote n)
    {
        var segs = new List<(double, double, double, double)>();
        foreach (Leader l in n.GetLeaders())
        {
            var pts = new[] { l.Anchor, l.Elbow, l.End };
            for (int i = 0; i < 2; i++)
                if (pts[i].DistanceTo(pts[i + 1]) > 1e-6)
                    segs.Add((U(view, pts[i]), V(view, pts[i]), U(view, pts[i + 1]), V(view, pts[i + 1])));
        }
        return segs;
    }

    /// <summary>Count proper crossings between leaders of different notes.</summary>
    public static int Crossings(View view, IList<TextNote> notes)
    {
        var all = notes.Select(n => Segments(view, n)).ToList();
        int count = 0;
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                foreach (var a in all[i])
                    foreach (var b in all[j])
                        if (Cross(a, b)) count++;
        return count;
    }

    internal static bool Cross((double u1, double v1, double u2, double v2) a, (double u1, double v1, double u2, double v2) b)
    {
        static double O(double ax, double ay, double bx, double by, double cx, double cy) =>
            (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        var d1 = O(b.u1, b.v1, b.u2, b.v2, a.u1, a.v1);
        var d2 = O(b.u1, b.v1, b.u2, b.v2, a.u2, a.v2);
        var d3 = O(a.u1, a.v1, a.u2, a.v2, b.u1, b.v1);
        var d4 = O(a.u1, a.v1, a.u2, a.v2, b.u2, b.v2);
        const double e = 1e-9;
        return ((d1 > e && d2 < -e) || (d1 < -e && d2 > e)) && ((d3 > e && d4 < -e) || (d3 < -e && d4 > e));
    }
}

/// <summary>
/// Tidy note leaders in a view to the OCTA rule: no crossings, 90° leaders.
///   - Groups notes by the side their leaders leave from (left/right of the detail).
///   - Orders each group top-to-bottom to match its arrow targets, moves each note level with
///     its target where there's room (straight horizontal leader), otherwise pushes it clear of the
///     note above and uses a 90° elbow. Arrow tips never move.
/// Params: viewId?, noteIds? (default: every note with leaders in the view), gapMm? (paper, default 2),
/// moveNotes? (default true; false = only square up elbows). dryRun shows before/after crossings.
/// </summary>
public sealed class TidyTextLeadersCommand : IRevitCommand
{
    public string Name => "tidy_text_leaders";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = OctaUtil.ResolveView(doc, p);
        var notes = (p["noteIds"] is JsonArray
                ? OctaUtil.Ids(p, "noteIds").Select(id => doc.GetElement(id) as TextNote)
                : new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>())
            .Where(n => n is not null && n.LeaderCount > 0 && n.OwnerViewId == view.Id).Cast<TextNote>().ToList();
        if (notes.Count == 0) return new JsonObject { ["notes"] = 0, ["note"] = "No notes with leaders in this view." };

        int before = LeaderGeom.Crossings(view, notes);
        var gap = P.DblOr(p, "gapMm", 2) / 304.8 * view.Scale;
        bool move = P.BoolOr(p, "moveNotes", true);
        int moved = 0;

        bool align = P.BoolOr(p, "alignColumns", true);
        int inline = 0;
        if (move)
        {
            // Notes inside the drawing (between the leftmost and rightmost arrow tips) stay where
            // they are horizontally and form their own neat group; margin notes form left/right columns.
            // Columns come from where notes actually sit: left edges clustered at the far left are
            // the left column, at the far right the right column; anything else is inside the drawing.
            double LeftEdge(TextNote n) => LeaderGeom.TextBox(view, n).left;
            var band = P.DblOr(p, "columnBandMm", 25) / 304.8 * view.Scale;
            double minL = notes.Min(LeftEdge), maxL = notes.Max(LeftEdge);
            string GroupOf(TextNote n)
            {
                var l = LeftEdge(n);
                if (l <= minL + band) return "left column";
                if (l >= maxL - band && maxL - minL > 2 * band) return "right column";
                return "inline";
            }
            var groups = notes.GroupBy(GroupOf);
            foreach (var g in groups)
            {
                if (g.Key == "inline") inline += g.Count();
                // Column alignment: every note in a margin column shares the column's left text edge.
                if (align && g.Key != "inline")
                {
                    var colU = g.Key == "left column"
                        ? g.Min(n => LeaderGeom.U(view, n.Coord))
                        : g.Min(n => LeaderGeom.U(view, n.Coord));
                    foreach (var n in g)
                    {
                        var du = colU - LeaderGeom.U(view, n.Coord);
                        if (Math.Abs(du) < 1e-6) continue;
                        var ends = n.GetLeaders().Cast<Leader>().Select(l => l.End).ToList();
                        ElementTransformUtils.MoveElement(doc, n.Id, view.RightDirection * du);
                        var ls = n.GetLeaders().Cast<Leader>().ToList();
                        for (int i = 0; i < ls.Count && i < ends.Count; i++) LeaderGeom.SetEnd(ls[i], ends[i]);
                        moved++;
                    }
                }
                if (g.Key == "inline") continue; // inline notes: square leaders only, no reshuffle
                var items = g.Select(n =>
                {
                    var leaders = n.GetLeaders().Cast<Leader>().ToList();
                    var targetV = leaders.Average(l => LeaderGeom.V(view, l.End));
                    var anchorOffset = LeaderGeom.V(view, leaders[0].Anchor) - LeaderGeom.V(view, n.Coord);
                    var box = LeaderGeom.TextBox(view, n);
                    var h = box.top - box.bottom;
                    var topOffset = box.top - LeaderGeom.V(view, n.Coord);
                    var anchorU = LeaderGeom.U(view, leaders[0].Anchor);
                    return (note: n, targetV, anchorOffset, h, topOffset, anchorU,
                        ends: leaders.Select(l => l.End).ToList());
                }).OrderByDescending(x => x.targetV).ToList();

                // Stack notes in a given order: each level with its target if there's room, else
                // pushed below the note above.
                List<double> Layout(IList<int> order)
                {
                    var res = new double[order.Count];
                    double? pb = null;
                    for (int k = 0; k < order.Count; k++)
                    {
                        var it = items[order[k]];
                        var want = it.targetV - it.anchorOffset;
                        var top = want + it.topOffset;
                        if (pb is { } b && top > b - gap) want -= top - (b - gap);
                        res[k] = want;
                        pb = want + it.topOffset - it.h;
                    }
                    return res.ToList();
                }
                // Simulated 90° leader paths for an order → crossing count (no model changes).
                int Score(IList<int> order)
                {
                    var pos = Layout(order);
                    var paths = new List<List<(double, double, double, double)>>();
                    for (int k = 0; k < order.Count; k++)
                    {
                        var it = items[order[k]];
                        var av = pos[k] + it.anchorOffset;
                        var segs = new List<(double, double, double, double)>();
                        foreach (var e in it.ends)
                        {
                            double eu = LeaderGeom.U(view, e), ev = LeaderGeom.V(view, e);
                            segs.Add((it.anchorU, av, eu, av));
                            if (Math.Abs(ev - av) > 1e-6) segs.Add((eu, av, eu, ev));
                        }
                        paths.Add(segs);
                    }
                    int c = 0;
                    for (int i = 0; i < paths.Count; i++)
                        for (int j = i + 1; j < paths.Count; j++)
                            foreach (var a in paths[i]) foreach (var b in paths[j]) if (LeaderGeom.Cross(a, b)) c++;
                    return c;
                }
                // Hill-climb over adjacent swaps, starting from target order; fewest crossings wins.
                var bestOrder = Enumerable.Range(0, items.Count).ToList();
                var bestScore = Score(bestOrder);
                for (int pass = 0; pass < 50 && bestScore > 0; pass++)
                {
                    bool improved = false;
                    for (int i = 0; i + 1 < bestOrder.Count; i++)
                    {
                        var trial = bestOrder.ToList();
                        (trial[i], trial[i + 1]) = (trial[i + 1], trial[i]);
                        var s = Score(trial);
                        if (s < bestScore) { bestOrder = trial; bestScore = s; improved = true; }
                    }
                    if (!improved) break;
                }
                var positions = Layout(bestOrder);

                for (int k = 0; k < bestOrder.Count; k++)
                {
                    var it = items[bestOrder[k]];
                    var wantCoordV = positions[k];
                    var delta = wantCoordV - LeaderGeom.V(view, it.note.Coord);
                    if (Math.Abs(delta) > 1e-6)
                    {
                        ElementTransformUtils.MoveElement(doc, it.note.Id, view.UpDirection * delta);
                        moved++;
                    }
                    // Arrow tips stay where they were.
                    var ls = it.note.GetLeaders().Cast<Leader>().ToList();
                    for (int i = 0; i < ls.Count && i < it.ends.Count; i++) LeaderGeom.SetEnd(ls[i], it.ends[i]);
                }
            }
            doc.Regenerate();
        }

        foreach (var n in notes)
            foreach (Leader l in n.GetLeaders())
                LeaderGeom.Orthogonalise(view, l);
        doc.Regenerate();
        int after = LeaderGeom.Crossings(view, notes);

        return new JsonObject
        {
            ["affected"] = Affected.Modified(notes.Select(n => n.Id.Value)),
            ["viewId"] = view.Id.Value,
            ["notes"] = notes.Count,
            ["moved"] = moved,
            ["inlineNotes"] = inline,
            ["crossingsBefore"] = before,
            ["crossingsAfter"] = after,
            ["note"] = after > 0 ? "Some crossings remain — usually two notes on the same side pointing past each other; consider moving one to the other side." : null,
        };
    }
}
