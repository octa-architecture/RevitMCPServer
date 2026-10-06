using System;
using System.Collections.Generic;
using System.Linq;

// Wall centrelines from double-line survey walls: pair parallel lines 70â€“450 mm apart that overlap,
// take the centreline over the overlap, then merge collinear pieces of the same thickness.
public static class Walls
{
    public sealed record Wall(double X1, double Y1, double X2, double Y2, double Thickness, double Length);

    public static List<Wall> Find(List<double[]> segs, double minT = 0.07, double maxT = 0.45, double minOverlap = 0.2)
    {
        var s = segs.Where(q => Len(q) > 0.05).ToList();
        var cand = new List<(int i, int j, double d, double t0, double t1)>();
        for (int i = 0; i < s.Count; i++)
            for (int j = i + 1; j < s.Count; j++)
            {
                var a = s[i]; var b = s[j];
                var (ux, uy) = Dir(a); var (vx, vy) = Dir(b);
                if (Math.Abs(ux * vy - uy * vx) > Math.Sin(2 * Math.PI / 180)) continue;   // not parallel
                // perpendicular distance of b's midpoint from a's line
                double mx = (b[0] + b[2]) / 2 - a[0], my = (b[1] + b[3]) / 2 - a[1];
                double d = Math.Abs(mx * -uy + my * ux);
                if (d < minT || d > maxT) continue;
                // Pairs of non-wall lines (window/door jambs on the "Lines" layer) must be wall-thick
                // and long, so glazing frames are not mistaken for walls.
                bool thinPair = a.Length > 4 && b.Length > 4 && a[4] == 0 && b[4] == 0;
                if (thinPair && (d < 0.18 || Len(a) < 0.6 || Len(b) < 0.6)) continue;
                // overlap along a's direction
                double tb0 = (b[0] - a[0]) * ux + (b[1] - a[1]) * uy, tb1 = (b[2] - a[0]) * ux + (b[3] - a[1]) * uy;
                double lo = Math.Max(0, Math.Min(tb0, tb1)), hi = Math.Min(Len(a), Math.Max(tb0, tb1));
                if (hi - lo < minOverlap) continue;
                cand.Add((i, j, d, lo, hi));
            }
        // Nearest partners first; a stretch of line is used by one wall only.
        var used = s.Select(_ => new List<(double, double)>()).ToList();
        var pieces = new List<Wall>();
        foreach (var c in cand.OrderBy(c => c.d))
        {
            var a = s[c.i]; var b = s[c.j];
            var (ux, uy) = Dir(a);
            double freeA = Free(used[c.i], c.t0, c.t1);
            // same interval projected on b
            double pb0 = Proj(b, a[0] + ux * c.t0, a[1] + uy * c.t0), pb1 = Proj(b, a[0] + ux * c.t1, a[1] + uy * c.t1);
            double freeB = Free(used[c.j], Math.Min(pb0, pb1), Math.Max(pb0, pb1));
            if (freeA < 0.6 || freeB < 0.6) continue;
            used[c.i].Add((c.t0, c.t1)); used[c.j].Add((Math.Min(pb0, pb1), Math.Max(pb0, pb1)));
            // centreline: a's interval shifted half the distance towards b
            double side = Math.Sign(((b[0] + b[2]) / 2 - a[0]) * -uy + ((b[1] + b[3]) / 2 - a[1]) * ux);
            double ox = -uy * side * c.d / 2, oy = ux * side * c.d / 2;
            pieces.Add(new Wall(a[0] + ux * c.t0 + ox, a[1] + uy * c.t0 + oy, a[0] + ux * c.t1 + ox, a[1] + uy * c.t1 + oy, c.d, c.t1 - c.t0));
        }
        return SnapEnds(Merge(pieces));
    }

    // Extend/trim wall ends to meet crossing walls (within 0.45 m), so Revit joins them.
    static List<Wall> SnapEnds(List<Wall> w)
    {
        var list = w.ToList();
        for (int i = 0; i < list.Count; i++)
            for (int end = 0; end < 2; end++)
            {
                var a = list[i];
                double ex = end == 0 ? a.X1 : a.X2, ey = end == 0 ? a.Y1 : a.Y2;
                var sa = new[] { a.X1, a.Y1, a.X2, a.Y2 }; var (ux, uy) = Dir(sa);
                double bestD = 0.45; (double x, double y)? best = null;
                foreach (var b in list)
                {
                    if (ReferenceEquals(a, b)) continue;
                    var sb = new[] { b.X1, b.Y1, b.X2, b.Y2 }; var (vx, vy) = Dir(sb);
                    double cross = ux * vy - uy * vx;
                    if (Math.Abs(cross) < 0.5) continue;                          // need a real angle
                    double t = ((b.X1 - ex) * vy - (b.Y1 - ey) * vx) / cross;     // along a from the end
                    double ix = ex + ux * t, iy = ey + uy * t;
                    double ub = Proj(sb, ix, iy);
                    if (ub < -0.45 || ub > Len(sb) + 0.45) continue;
                    if (Math.Abs(t) < bestD) { bestD = Math.Abs(t); best = (ix, iy); }
                }
                if (best is { } p)
                    list[i] = end == 0 ? a with { X1 = p.x, Y1 = p.y } : a with { X2 = p.x, Y2 = p.y };
            }
        return list.Select(x => x with { Length = Math.Round(Math.Sqrt((x.X2 - x.X1) * (x.X2 - x.X1) + (x.Y2 - x.Y1) * (x.Y2 - x.Y1)), 3) }).ToList();
    }

    static List<Wall> Merge(List<Wall> w)
    {
        var list = w.ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < list.Count && !merged; i++)
                for (int j = i + 1; j < list.Count && !merged; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (Math.Abs(a.Thickness - b.Thickness) > 0.03) continue;
                    var sa = new[] { a.X1, a.Y1, a.X2, a.Y2 }; var sb = new[] { b.X1, b.Y1, b.X2, b.Y2 };
                    var (ux, uy) = Dir(sa); var (vx, vy) = Dir(sb);
                    if (Math.Abs(ux * vy - uy * vx) > Math.Sin(1.5 * Math.PI / 180)) continue;
                    double off = Math.Abs((b.X1 - a.X1) * -uy + (b.Y1 - a.Y1) * ux);
                    if (off > 0.04) continue;
                    double t0 = 0, t1 = Len(sa), u0 = Proj(sa, b.X1, b.Y1), u1 = Proj(sa, b.X2, b.Y2);
                    double lo = Math.Min(u0, u1), hi = Math.Max(u0, u1);
                    if (lo > t1 + 0.05 || hi < t0 - 0.05) continue;   // gap: keep separate (openings)
                    double n0 = Math.Min(t0, lo), n1 = Math.Max(t1, hi);
                    double th = (a.Thickness * a.Length + b.Thickness * b.Length) / (a.Length + b.Length);
                    list[i] = new Wall(a.X1 + ux * n0, a.Y1 + uy * n0, a.X1 + ux * n1, a.Y1 + uy * n1, th, n1 - n0);
                    list.RemoveAt(j); merged = true;
                }
        }
        return list.Where(x => x.Length >= 0.25).Select(x => x with
        {
            X1 = Math.Round(x.X1, 4), Y1 = Math.Round(x.Y1, 4), X2 = Math.Round(x.X2, 4), Y2 = Math.Round(x.Y2, 4),
            Thickness = Math.Round(x.Thickness, 3), Length = Math.Round(x.Length, 3),
        }).ToList();
    }

    static double Free(List<(double, double)> used, double a, double b)
    {
        double L = b - a; if (L <= 0) return 0;
        double cov = 0;
        foreach (var (u0, u1) in used) cov += Math.Max(0, Math.Min(b, u1) - Math.Max(a, u0));
        return 1 - Math.Min(1, cov / L);
    }
    static double Len(double[] q) => Math.Sqrt((q[2] - q[0]) * (q[2] - q[0]) + (q[3] - q[1]) * (q[3] - q[1]));
    static (double, double) Dir(double[] q) { var L = Len(q); return ((q[2] - q[0]) / L, (q[3] - q[1]) / L); }
    static double Proj(double[] q, double x, double y) { var (ux, uy) = Dir(q); return (x - q[0]) * ux + (y - q[1]) * uy; }
}

