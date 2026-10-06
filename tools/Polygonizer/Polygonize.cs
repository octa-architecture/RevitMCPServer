using System;
using System.Collections.Generic;
using System.Linq;

// Turns loose CAD linework into closed faces (planar-graph face walk), so building outlines
// drawn as separate line segments become polygons. Used client-side by the OCTA site scripts.
public static class Polygonize
{
    // segs: [x1,y1,x2,y2]; returns CCW faces (list of [x,y]) with area in [minArea, maxArea].
    public static List<double[][]> Faces(List<double[]> segs, double tol, double minArea, double maxArea)
    {
        // 1. Split every segment at intersections / T-junctions with the others.
        var pieces = new List<(double ax, double ay, double bx, double by)>();
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var ts = new List<double> { 0, 1 };
            for (int j = 0; j < segs.Count; j++)
            {
                if (i == j) continue;
                var t = segs[j];
                if (Intersect(s, t, tol, out var u)) ts.Add(u);
                // T-junction: an endpoint of t lying on s
                foreach (var (px, py) in new[] { (t[0], t[1]), (t[2], t[3]) })
                {
                    var w = Project(s, px, py, out var d);
                    if (d < tol && w > 1e-6 && w < 1 - 1e-6) ts.Add(w);
                }
            }
            ts.Sort();
            for (int k = 0; k + 1 < ts.Count; k++)
            {
                if (ts[k + 1] - ts[k] < 1e-9) continue;
                double x1 = s[0] + (s[2] - s[0]) * ts[k], y1 = s[1] + (s[3] - s[1]) * ts[k];
                double x2 = s[0] + (s[2] - s[0]) * ts[k + 1], y2 = s[1] + (s[3] - s[1]) * ts[k + 1];
                if (Math.Abs(x2 - x1) + Math.Abs(y2 - y1) > tol) pieces.Add((x1, y1, x2, y2));
            }
        }
        // 2. Snap endpoints into shared vertices.
        var vx = new List<double>(); var vy = new List<double>();
        int V(double x, double y)
        {
            for (int i = 0; i < vx.Count; i++) if (Math.Abs(vx[i] - x) < tol && Math.Abs(vy[i] - y) < tol) return i;
            vx.Add(x); vy.Add(y); return vx.Count - 1;
        }
        var edges = new HashSet<(int, int)>();
        foreach (var p in pieces)
        {
            int a = V(p.ax, p.ay), b = V(p.bx, p.by);
            if (a != b) { edges.Add((a, b)); edges.Add((b, a)); }
        }
        // 3. Half-edge face walk: at each vertex, outgoing edges sorted by angle.
        var outg = edges.GroupBy(e => e.Item1).ToDictionary(g => g.Key,
            g => g.Select(e => e.Item2).OrderBy(t => Math.Atan2(vy[t] - vy[g.Key], vx[t] - vx[g.Key])).ToList());
        var used = new HashSet<(int, int)>();
        var faces = new List<double[][]>();
        foreach (var start in edges)
        {
            if (used.Contains(start)) continue;
            var loop = new List<int>(); var he = start; int guard = 0;
            while (used.Add(he) && guard++ < 10000)
            {
                loop.Add(he.Item1);
                var (u, v) = he;
                var list = outg[v];
                int idx = list.IndexOf(u);
                // next edge: the one just clockwise from the reverse edge (gives CCW interior faces)
                int w = list[(idx - 1 + list.Count) % list.Count];
                he = (v, w);
            }
            if (loop.Count < 3) continue;
            double area = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                int a = loop[i], b = loop[(i + 1) % loop.Count];
                area += vx[a] * vy[b] - vx[b] * vy[a];
            }
            area /= 2;
            if (area >= minArea && area <= maxArea)
                faces.Add(loop.Select(i => new[] { Math.Round(vx[i], 4), Math.Round(vy[i], 4) }).ToArray());
        }
        return faces;
    }

    static double Project(double[] s, double px, double py, out double dist)
    {
        double dx = s[2] - s[0], dy = s[3] - s[1], L2 = dx * dx + dy * dy;
        double t = L2 < 1e-12 ? 0 : ((px - s[0]) * dx + (py - s[1]) * dy) / L2;
        double cx = s[0] + t * dx, cy = s[1] + t * dy;
        dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        return t;
    }

    static bool Intersect(double[] s, double[] t, double tol, out double u)
    {
        u = 0;
        double rx = s[2] - s[0], ry = s[3] - s[1], qx = t[2] - t[0], qy = t[3] - t[1];
        double den = rx * qy - ry * qx;
        if (Math.Abs(den) < 1e-12) return false;
        double ux = ((t[0] - s[0]) * qy - (t[1] - s[1]) * qx) / den;
        double vv = ((t[0] - s[0]) * ry - (t[1] - s[1]) * rx) / den;
        double Ls = Math.Sqrt(rx * rx + ry * ry), Lt = Math.Sqrt(qx * qx + qy * qy);
        if (ux * Ls < -tol || ux * Ls > Ls + tol || vv * Lt < -tol || vv * Lt > Lt + tol) return false;
        if (ux <= 1e-6 || ux >= 1 - 1e-6) return false;
        u = ux; return true;
    }
}
