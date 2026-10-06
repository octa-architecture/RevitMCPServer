using System;
using System.Collections.Generic;
using System.Linq;

// Building footprints from survey wall linework + a roof height grid (point-cloud "max" per cell).
// Walls drawn with gaps at openings are closed morphologically, enclosed regions are found, and a
// region counts as a building when the roof grid stands well above the ground over most of it.
public static class Footprints
{
    public sealed record Building(double[][] Outline, double AreaM2, double GroundZ, double EaveZ, double RidgeZ, double Coverage);

    public static List<Building> Find(List<double[]> segs, List<double[]> roof, List<double[]> ground,
        double cell = 0.1, double close = 0.65, double wallGrow = 0.3, double minHeight = 2.0, double minArea = 8, double minCoverage = 0.5)
    {
        double minX = segs.Min(s => Math.Min(s[0], s[2])) - 1, maxX = segs.Max(s => Math.Max(s[0], s[2])) + 1;
        double minY = segs.Min(s => Math.Min(s[1], s[3])) - 1, maxY = segs.Max(s => Math.Max(s[1], s[3])) + 1;
        int W = (int)Math.Ceiling((maxX - minX) / cell) + 1, H = (int)Math.Ceiling((maxY - minY) / cell) + 1;
        int Ix(double x) => (int)Math.Round((x - minX) / cell);
        int Iy(double y) => (int)Math.Round((y - minY) / cell);
        double X(int i) => minX + i * cell;
        double Y(int j) => minY + j * cell;

        var wall = new bool[W, H];
        foreach (var s in segs)
        {
            int n = (int)Math.Ceiling(Math.Sqrt(Math.Pow(s[2] - s[0], 2) + Math.Pow(s[3] - s[1], 2)) / (cell / 2)) + 1;
            for (int k = 0; k <= n; k++)
            {
                double t = (double)k / n;
                wall[Ix(s[0] + (s[2] - s[0]) * t), Iy(s[1] + (s[3] - s[1]) * t)] = true;
            }
        }
        // Frame at the survey extent so buildings cut off by the survey edge still close.
        for (int i = 0; i < W; i++) { wall[i, 0] = wall[i, H - 1] = true; }
        for (int j = 0; j < H; j++) { wall[0, j] = wall[W - 1, j] = true; }

        int r = (int)Math.Round(close / cell);
        var thick = Dilate(wall, r);

        // Connected regions of free space.
        var label = new int[W, H];
        var regions = new List<List<(int, int)>>();
        for (int i = 0; i < W; i++)
            for (int j = 0; j < H; j++)
            {
                if (thick[i, j] || label[i, j] != 0) continue;
                var cells = new List<(int, int)>(); var q = new Queue<(int, int)>();
                q.Enqueue((i, j)); label[i, j] = regions.Count + 1;
                while (q.Count > 0)
                {
                    var (a, b) = q.Dequeue(); cells.Add((a, b));
                    foreach (var (da, db) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int na = a + da, nb = b + db;
                        if (na < 0 || nb < 0 || na >= W || nb >= H || thick[na, nb] || label[na, nb] != 0) continue;
                        label[na, nb] = regions.Count + 1; q.Enqueue((na, nb));
                    }
                }
                regions.Add(cells);
            }

        // Height lookups (nearest grid sample within 0.4 m).
        var roofIdx = Index(roof, 0.5); var groundIdx = Index(ground, 1.0);
        double? Near(Dictionary<(int, int), List<double[]>> idx, double size, double x, double y, double maxD)
        {
            double best = double.MaxValue; double? z = null;
            int cx = (int)Math.Floor(x / size), cy = (int)Math.Floor(y / size);
            for (int a = cx - 1; a <= cx + 1; a++) for (int b = cy - 1; b <= cy + 1; b++)
                if (idx.TryGetValue((a, b), out var l)) foreach (var p in l)
                {
                    var d = (p[0] - x) * (p[0] - x) + (p[1] - y) * (p[1] - y);
                    if (d < best && d < maxD * maxD) { best = d; z = p[2]; }
                }
            return z;
        }

        var result = new List<Building>();
        int grow = (int)Math.Round((close + wallGrow) / cell);
        foreach (var cells in regions)
        {
            double area = cells.Count * cell * cell;
            if (Environment.GetEnvironmentVariable("FP_DEBUG") == "1" && area > 2) Console.Error.WriteLine($"region area {area:N1} at {X(cells[0].Item1):N1},{Y(cells[0].Item2):N1}");
            if (area < minArea || area > 2000) continue;
            if (cells.Any(c => c.Item1 <= r + 1 || c.Item2 <= r + 1 || c.Item1 >= W - r - 2 || c.Item2 >= H - r - 2) && area > 600) continue;
            // Ground under the region: lowest nearby ground sample.
            var gz = new List<double>(); var rz = new List<double>();
            for (int k = 0; k < cells.Count; k += 4)
            {
                var (a, b) = cells[k];
                var g = Near(groundIdx, 1.0, X(a), Y(b), 1.5); if (g.HasValue) gz.Add(g.Value);
                var z = Near(roofIdx, 0.5, X(a), Y(b), 0.4); if (z.HasValue) rz.Add(z.Value);
            }
            if (gz.Count == 0) continue;
            gz.Sort(); rz.Sort();
            double ground0 = gz[gz.Count / 2];
            var high = rz.Where(z => z - ground0 > minHeight).ToList();
            double coverage = (double)high.Count / Math.Max(1, cells.Count / 4);
            if (coverage < minCoverage) continue;
            double eave = high.Count > 0 ? high[(int)(high.Count * 0.15)] : 0, ridge = high.Count > 0 ? high[(int)(high.Count * 0.98)] : 0;

            // Footprint mask: region grown back out to the walls' outer face.
            var mask = new bool[W, H];
            foreach (var (a, b) in cells) mask[a, b] = true;
            mask = Dilate(mask, grow);
            var outline = Simplify(Trace(mask, W, H).Select(p => new[] { X(p.Item1) - cell / 2, Y(p.Item2) - cell / 2 }).ToList(), 0.12);
            if (outline.Count < 3) continue;
            result.Add(new Building(outline.Select(p => new[] { Math.Round(p[0], 3), Math.Round(p[1], 3) }).ToArray(),
                Math.Round(Area(outline), 1), Math.Round(ground0, 3), Math.Round(eave, 3), Math.Round(ridge, 3), Math.Round(coverage, 2)));
        }
        return result;
    }

    static Dictionary<(int, int), List<double[]>> Index(List<double[]> pts, double size)
    {
        var d = new Dictionary<(int, int), List<double[]>>();
        foreach (var p in pts)
        {
            var k = ((int)Math.Floor(p[0] / size), (int)Math.Floor(p[1] / size));
            if (!d.TryGetValue(k, out var l)) d[k] = l = new List<double[]>();
            l.Add(p);
        }
        return d;
    }

    static bool[,] Dilate(bool[,] m, int r)
    {
        int W = m.GetLength(0), H = m.GetLength(1);
        var o = new bool[W, H];
        var offs = new List<(int, int)>();
        // square kernel: buildings are drawn square to the sheet, so this keeps their corners square
        for (int a = -r; a <= r; a++) for (int b = -r; b <= r; b++) offs.Add((a, b));
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            if (!m[i, j]) continue;
            // only boundary cells need stamping
            bool edge = i == 0 || j == 0 || i == W - 1 || j == H - 1 || !m[i - 1, j] || !m[i + 1, j] || !m[i, j - 1] || !m[i, j + 1];
            if (!edge) { o[i, j] = true; continue; }
            foreach (var (a, b) in offs)
            {
                int x = i + a, y = j + b;
                if (x >= 0 && y >= 0 && x < W && y < H) o[x, y] = true;
            }
        }
        return o;
    }

    // Outer boundary of the largest blob, as cell-corner coordinates (square tracing on corners).
    static List<(int, int)> Trace(bool[,] m, int W, int H)
    {
        bool In(int i, int j) => i >= 0 && j >= 0 && i < W && j < H && m[i, j];
        // Directed boundary edges with the inside on the left, keyed by start corner.
        var next = new Dictionary<(int, int), (int, int)>();
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            if (!m[i, j]) continue;
            if (!In(i, j - 1)) next[(i, j)] = (i + 1, j);
            if (!In(i + 1, j)) next[(i + 1, j)] = (i + 1, j + 1);
            if (!In(i, j + 1)) next[(i + 1, j + 1)] = (i, j + 1);
            if (!In(i - 1, j)) next[(i, j + 1)] = (i, j);
        }
        List<(int, int)> best = new();
        var seen = new HashSet<(int, int)>();
        foreach (var start in next.Keys.ToList())
        {
            if (seen.Contains(start)) continue;
            var loop = new List<(int, int)>(); var c = start;
            while (seen.Add(c) && next.TryGetValue(c, out var n)) { loop.Add(c); c = n; }
            if (loop.Count > best.Count) best = loop;
        }
        return best;
    }

    static List<double[]> Simplify(List<double[]> pts, double tol)
    {
        if (pts.Count < 4) return pts;
        // Douglasâ€“Peucker on the closed ring, split at the two farthest points.
        int far = 0; double fd = 0;
        for (int i = 1; i < pts.Count; i++) { var d = Dist(pts[0], pts[i]); if (d > fd) { fd = d; far = i; } }
        var a = DP(pts.GetRange(0, far + 1), tol); var b = DP(pts.GetRange(far, pts.Count - far).Append(pts[0]).ToList(), tol);
        var ring = a.Take(a.Count - 1).Concat(b.Take(b.Count - 1)).ToList();
        return ring;
    }

    static List<double[]> DP(List<double[]> p, double tol)
    {
        if (p.Count < 3) return p;
        int idx = 0; double dmax = 0;
        for (int i = 1; i < p.Count - 1; i++) { var d = SegDist(p[i], p[0], p[^1]); if (d > dmax) { dmax = d; idx = i; } }
        if (dmax <= tol) return new List<double[]> { p[0], p[^1] };
        var l = DP(p.GetRange(0, idx + 1), tol); var r = DP(p.GetRange(idx, p.Count - idx), tol);
        return l.Take(l.Count - 1).Concat(r).ToList();
    }

    static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
    static double SegDist(double[] p, double[] a, double[] b)
    {
        double dx = b[0] - a[0], dy = b[1] - a[1], L2 = dx * dx + dy * dy;
        if (L2 < 1e-12) return Dist(p, a);
        double t = Math.Clamp(((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / L2, 0, 1);
        return Dist(p, new[] { a[0] + t * dx, a[1] + t * dy });
    }
    static double Area(List<double[]> p) { double s = 0; for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += a[0] * b[1] - b[0] * a[1]; } return Math.Abs(s / 2); }
}



