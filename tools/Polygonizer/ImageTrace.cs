using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;

// Traces a black/white opacity image (white = solid) into profile loops for create_model_family
// "extrusions": each solid region becomes one extrusion with its holes. Coordinates in mm, x to the
// right from the image's left edge, z up from its bottom edge, scaled so the image is heightMm tall.
public static class ImageTrace
{
    public static int Tiles = 1;
    public static string Run(string png, string outJson, string previewPng, double heightMm, int cellPx, double tolPx, double minAreaPx, bool invert)
    {
        using var src = new Bitmap(png);
        int W = src.Width / cellPx, H = src.Height / cellPx;
        var solid = new bool[W, H];
        for (int i = 0; i < W; i++)
            for (int j = 0; j < H; j++)
            {
                double sum = 0; int n = 0;
                for (int a = 0; a < cellPx; a++)
                    for (int b = 0; b < cellPx; b++)
                    {
                        var c = src.GetPixel(i * cellPx + a, j * cellPx + b);
                        sum += c.A < 128 ? 0 : (c.R + c.G + c.B) / 3.0; n++;
                    }
                bool white = sum / n > 127;
                solid[i, H - 1 - j] = invert ? !white : white;   // flip so z goes up
            }
        // Bridge diagonal-only contacts (2x2 checkerboards): they create pinch vertices, i.e.
        // figure-eight loops that Revit rejects as profiles.
        for (int pass = 0; pass < 3; pass++)
            for (int i = 0; i + 1 < W; i++)
                for (int j = 0; j + 1 < H; j++)
                {
                    bool a = solid[i, j], b = solid[i + 1, j], c = solid[i, j + 1], d = solid[i + 1, j + 1];
                    if (a && d && !b && !c) solid[i + 1, j] = true;
                    else if (b && c && !a && !d) solid[i, j] = true;
                }
        // Optional tiling: cut thin empty seams so each tile is traced as separate, simpler solids.
        if (Tiles > 1)
            for (int t = 1; t < Tiles; t++)
            {
                int ci = W * t / Tiles, cj = H * t / Tiles;
                for (int j = 0; j < H; j++) solid[ci, j] = false;
                for (int i = 0; i < W; i++) solid[i, cj] = false;
            }
        bool S(int i, int j) => i >= 0 && j >= 0 && i < W && j < H && solid[i, j];

        // Directed boundary edges with solid on the left (outer loops CCW, holes CW).
        var next = new Dictionary<(int, int), List<(int, int)>>();
        void Edge((int, int) a, (int, int) b) { if (!next.TryGetValue(a, out var l)) next[a] = l = new(); l.Add(b); }
        for (int i = 0; i < W; i++)
            for (int j = 0; j < H; j++)
            {
                if (!solid[i, j]) continue;
                if (!S(i, j - 1)) Edge((i, j), (i + 1, j));
                if (!S(i + 1, j)) Edge((i + 1, j), (i + 1, j + 1));
                if (!S(i, j + 1)) Edge((i + 1, j + 1), (i, j + 1));
                if (!S(i - 1, j)) Edge((i, j + 1), (i, j));
            }
        var loops = new List<List<double[]>>();
        while (next.Count > 0)
        {
            var start = next.Keys.First(); var cur = start; var loop = new List<double[]>();
            (int, int) prevDir = (0, 0);
            while (next.TryGetValue(cur, out var outs) && outs.Count > 0)
            {
                // at a pinch vertex (two outgoing), prefer the left turn to keep loops simple
                var pick = outs.Count == 1 ? outs[0] : outs.OrderBy(o => Turn(prevDir, (o.Item1 - cur.Item1, o.Item2 - cur.Item2))).First();
                outs.Remove(pick); if (outs.Count == 0) next.Remove(cur);
                loop.Add(new double[] { cur.Item1, cur.Item2 });
                prevDir = (pick.Item1 - cur.Item1, pick.Item2 - cur.Item2);
                cur = pick;
                if (cur == start) break;
            }
            if (loop.Count >= 4) loops.Add(Simplify(loop, tolPx));
        }
        loops = loops.Where(l => l.Count >= 3 && Math.Abs(Area(l)) >= minAreaPx).ToList();
        var outers = loops.Where(l => Area(l) > 0).ToList();
        var holes = loops.Where(l => Area(l) < 0).ToList();
        double mm = heightMm / H;
        var shapes = outers.Select(o => new List<List<double[]>> { o }).ToList();
        foreach (var h in holes)
        {
            var p = h[0];
            var owner = shapes.Where(s => Inside(s[0], p)).OrderBy(s => Math.Abs(Area(s[0]))).FirstOrDefault();
            owner?.Add(h);
        }
        var arr = new JsonArray();
        foreach (var s in shapes)
            arr.Add(new JsonObject
            {
                ["y"] = new JsonArray("PI", "PO"), ["material"] = "Lacework Material",
                ["loops"] = new JsonArray(s.Select(l => (JsonNode)new JsonArray(l.Select(q => (JsonNode)new JsonArray(Math.Round(q[0] * mm, 1), Math.Round(q[1] * mm, 1))).ToArray())).ToArray()),
            });
        System.IO.File.WriteAllText(outJson, arr.ToJsonString());

        using var bmp = new Bitmap(W * 2 + 20, H * 2 + 20); using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var iron = new SolidBrush(Color.FromArgb(50, 45, 40));
        foreach (var s in shapes)
            for (int k = 0; k < s.Count; k++)
                g.FillPolygon(k == 0 ? iron : Brushes.White, s[k].Select(q => new PointF((float)(10 + q[0] * 2), (float)(10 + (H - q[1]) * 2))).ToArray());
        bmp.Save(previewPng);
        return $"{W}x{H} cells, {shapes.Count} solids, {holes.Count} holes, {shapes.Sum(s => s.Sum(l => l.Count))} vertices; {W * mm:0} x {heightMm:0} mm";
    }

    static int Turn((int, int) a, (int, int) b) => a == (0, 0) ? 0 : -(a.Item1 * b.Item2 - a.Item2 * b.Item1);   // left turn first

    static double Area(List<double[]> p) { double s = 0; for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += a[0] * b[1] - b[0] * a[1]; } return s / 2; }

    static bool Inside(List<double[]> poly, double[] q)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i][1] > q[1]) != (poly[j][1] > q[1]) && q[0] < (poly[j][0] - poly[i][0]) * (q[1] - poly[i][1]) / (poly[j][1] - poly[i][1]) + poly[i][0]) c = !c;
        return c;
    }

    static List<double[]> Simplify(List<double[]> ring, double tol)
    {
        if (ring.Count < 8) return ring;
        int far = 0; double fd = 0;
        for (int i = 1; i < ring.Count; i++) { var d = Dist(ring[0], ring[i]); if (d > fd) { fd = d; far = i; } }
        var a = DP(ring.GetRange(0, far + 1), tol); var b = DP(ring.GetRange(far, ring.Count - far).Append(ring[0]).ToList(), tol);
        return a.Take(a.Count - 1).Concat(b.Take(b.Count - 1)).ToList();
    }
    static List<double[]> DP(List<double[]> p, double tol)
    {
        if (p.Count < 3) return p;
        int idx = 0; double dmax = 0;
        for (int i = 1; i < p.Count - 1; i++) { var d = SegDist(p[i], p[0], p[^1]); if (d > dmax) { dmax = d; idx = i; } }
        if (dmax <= tol) return new() { p[0], p[^1] };
        var l = DP(p.GetRange(0, idx + 1), tol); var r = DP(p.GetRange(idx, p.Count - idx), tol);
        return l.Take(l.Count - 1).Concat(r).ToList();
    }
    static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
    static double SegDist(double[] p, double[] a, double[] b)
    {
        double dx = b[0] - a[0], dz = b[1] - a[1], L2 = dx * dx + dz * dz;
        if (L2 < 1e-12) return Dist(p, a);
        double t = Math.Clamp(((p[0] - a[0]) * dx + (p[1] - a[1]) * dz) / L2, 0, 1);
        return Dist(p, new[] { a[0] + t * dx, a[1] + t * dz });
    }
}
