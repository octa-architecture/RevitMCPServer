using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Cast-iron verandah lacework from a terrestrial laser scan -> pierced 3D panels (mesh JSON for the Revit importer).
//   Polygonizer lace <in.csv> <outPrefix> <name> [--x 2.94] [--thickness 0.012] [--zmin 37.3] [--zmax 37.9]
//                    [--ymin ..] [--ymax ..] [--xsearch 2.0,3.4] [--px 0.004] [--posts y1,y2,..] [--postwidth 0.11]
//                    [--dp 0.003] [--minarea 100] [--close 1]
// CSV: x,y,z,r,g,b in model metres (z = RL). The frieze is assumed to be a vertical plane of constant x (street to -x).
// Pipeline: find the plane (x histogram scored by vertical (y,z) coverage), thickness, lace band (z), posts (y),
// rasterise (y,z) occupancy, close, threshold, despeckle, trace contours with holes (pixel-crack tracing, edge
// midpoints = marching squares), Douglas-Peucker, earcut front/back caps + side walls, one mesh per bay.
public static partial class Lacework
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<double[]> Load(string csv)
    {
        var pts = new List<double[]>();
        foreach (var line in File.ReadLines(csv))
        {
            var s = line.Split(',');
            if (s.Length < 3) continue;
            if (!double.TryParse(s[0], NumberStyles.Float, Inv, out var x)) continue;
            pts.Add(new[] { x, double.Parse(s[1], Inv), double.Parse(s[2], Inv) });
        }
        return pts;
    }

    sealed class Opts
    {
        public double? X, Thick, Zmin, Zmax, Ymin, Ymax, Px, PostWidth;
        public double XsLo = double.NegativeInfinity, XsHi = 3.4, Dp = 0.003, MinAreaMm2 = 100, MinHoleMm2 = 40;
        public int Close = 1;
        public List<double> Posts;
        public bool NoFold; public double? Period, FoldThr; public string Merge;
    }

    static Opts Parse(string[] a, int from)
    {
        var o = new Opts();
        for (int k = from; k < a.Length - 1; k++)
        {
            string v = a[k + 1];
            double D() => double.Parse(v, Inv);
            switch (a[k])
            {
                case "--x": o.X = D(); break;
                case "--thickness": o.Thick = D(); break;
                case "--zmin": o.Zmin = D(); break;
                case "--zmax": o.Zmax = D(); break;
                case "--ymin": o.Ymin = D(); break;
                case "--ymax": o.Ymax = D(); break;
                case "--px": o.Px = D(); break;
                case "--nofold": o.NoFold = v != "0"; break;
                case "--period": o.Period = D(); break;
                case "--merge": o.Merge = v; break;
                case "--foldthr": o.FoldThr = D(); break;
                case "--postwidth": o.PostWidth = D(); break;
                case "--dp": o.Dp = D(); break;
                case "--minarea": o.MinAreaMm2 = D(); break;
                case "--minhole": o.MinHoleMm2 = D(); break;
                case "--close": o.Close = int.Parse(v); break;
                case "--xsearch": var s = v.Split(','); o.XsLo = double.Parse(s[0], Inv); o.XsHi = double.Parse(s[1], Inv); break;
                case "--posts": o.Posts = v.Length == 0 || v == "none" ? new List<double>() : v.Split(',').Select(t => double.Parse(t, Inv)).ToList(); break;
                default: continue;
            }
            k++;
        }
        return o;
    }

    // ------------------------------------------------------------------------------------------------ main
    public static string Run(string[] args)
    {
        string csv = args[1], prefix = args[2], name = args[3];
        var o = Parse(args, 4);
        var log = new StringBuilder();
        void L(string s) { Console.WriteLine(s); log.AppendLine(s); }
        var all = Load(csv);
        var pts = all.Where(p => (o.Ymin is null || p[1] >= o.Ymin) && (o.Ymax is null || p[1] <= o.Ymax)).ToList();
        L($"{name}: {pts.Count} points  x {pts.Min(p => p[0]):F3}..{pts.Max(p => p[0]):F3}  y {pts.Min(p => p[1]):F3}..{pts.Max(p => p[1]):F3}  z {pts.Min(p => p[2]):F3}..{pts.Max(p => p[2]):F3}");
        var warnings = new List<string>();

        // ---- 1a. plane x: 5 mm bins, scored by the number of distinct 20x20 mm (y,z) cells within +-10 mm.
        var zsel = pts.Where(p => (o.Zmin is null || p[2] >= o.Zmin - 0.02) && (o.Zmax is null || p[2] <= o.Zmax + 0.02)).ToList();
        double planeX;
        double xLo = Math.Max(zsel.Min(p => p[0]), o.XsLo), xHi = Math.Min(zsel.Max(p => p[0]), o.XsHi);
        int nb = Math.Max(1, (int)((xHi - xLo) / 0.005) + 1);
        var cells = new HashSet<long>[nb];
        for (int k = 0; k < nb; k++) cells[k] = new HashSet<long>();
        var hist = new int[nb];
        foreach (var p in zsel)
        {
            if (p[0] < xLo - 0.01 || p[0] > xHi + 0.01) continue;
            long key = (long)Math.Floor(p[1] / 0.02) * 100000 + (long)Math.Floor(p[2] / 0.02);
            int b0 = (int)Math.Floor((p[0] - 0.010 - xLo) / 0.005), b1 = (int)Math.Floor((p[0] + 0.010 - xLo) / 0.005);
            for (int b = Math.Max(0, b0); b <= Math.Min(nb - 1, b1); b++) cells[b].Add(key);
            int bh = (int)Math.Floor((p[0] - xLo) / 0.005); if (bh >= 0 && bh < nb) hist[bh]++;
        }
        var score = cells.Select(c => c.Count).ToArray();
        if (o.X is double fx) planeX = fx;
        else
        {
            int best = Array.IndexOf(score, score.Max());
            // refine: centroid of point x within +-15 mm of the best bin
            double c0 = xLo + (best + 0.5) * 0.005;
            var near = zsel.Where(p => Math.Abs(p[0] - c0) <= 0.015).Select(p => p[0]).ToList();
            planeX = near.Count > 0 ? Median(near) : c0;
        }
        L($"  x-histogram (5 mm bins, top vertical-coverage scores):");
        foreach (var k in Enumerable.Range(0, nb).OrderByDescending(k => score[k]).Take(6).OrderBy(k => k))
            L($"    x {xLo + (k + 0.5) * 0.005:F3}  pts {hist[k],6}  yz-cells {score[k],6}");
        L($"  plane x = {planeX:F3}{(o.X is null ? " (auto)" : " (given)")}");

        // ---- 1b. thickness: robust x spread of points near the plane (central 80 %).
        var slab0 = zsel.Where(p => Math.Abs(p[0] - planeX) <= 0.03).Select(p => p[0] - planeX).OrderBy(v => v).ToList();
        double thickMeas = slab0.Count > 20 ? Pct(slab0, 0.9) - Pct(slab0, 0.1) : 0;
        double thick = o.Thick ?? (thickMeas >= 0.008 && thickMeas <= 0.03 ? thickMeas : 0.012);
        L($"  measured x spread (P10..P90) {thickMeas * 1000:F1} mm -> thickness used {thick * 1000:F1} mm{(o.Thick is null && (thickMeas < 0.008 || thickMeas > 0.03) ? " (default; measurement implausible)" : "")}");
        double halfWin = thick / 2 + 0.010;
        var plane = pts.Where(p => Math.Abs(p[0] - planeX) <= halfWin).ToList();

        // ---- 1c. band z: per 5 mm z row, fraction of 20 mm y-columns occupied (over the plane's y extent).
        var ys = plane.Select(p => p[1]).OrderBy(v => v).ToList();
        double yA = o.Ymin ?? Pct(ys, 0.005), yB = o.Ymax ?? Pct(ys, 0.995);
        double zA = plane.Min(p => p[2]), zB = plane.Max(p => p[2]);
        int nzr = (int)((zB - zA) / 0.005) + 1, nyc = Math.Max(1, (int)((yB - yA) / 0.02) + 1);
        var occ = new bool[nzr, nyc];
        foreach (var p in plane)
        {
            int r = (int)((p[2] - zA) / 0.005), c = (int)((p[1] - yA) / 0.02);
            if (c >= 0 && c < nyc) occ[r, c] = true;
        }
        var frac = new double[nzr];
        for (int r = 0; r < nzr; r++) { int n = 0; for (int c = 0; c < nyc; c++) if (occ[r, c]) n++; frac[r] = (double)n / nyc; }
        var fs = new double[nzr];
        for (int r = 0; r < nzr; r++) { double s = 0; int n = 0; for (int d = -2; d <= 2; d++) if (r + d >= 0 && r + d < nzr) { s += frac[r + d]; n++; } fs[r] = s / n; }
        L("  z profile in plane (fraction of 20 mm y-columns occupied, 10 mm steps):");
        for (int r = nzr - 1; r >= 0; r -= 2) L($"    z {zA + (r + 0.5) * 0.005:F3}  {fs[r]:F2}  {new string('#', (int)(fs[r] * 50))}");
        double bandLo, bandHi;
        {
            // longest run with smoothed fraction >= 0.5 * (90th percentile of the profile), at least 0.2
            double thr = Math.Max(0.12, 0.45 * fs.OrderBy(v => v).ElementAt((int)(0.9 * (nzr - 1))));
            var fsm = (double[])fs.Clone();   // bridge dips shorter than 30 mm
            for (int r = 0; r < nzr; r++) if (fs[r] < thr) { int a = r; while (a < nzr && fs[a] < thr) a++; if (r > 0 && a < nzr && a - r <= 6) for (int k = r; k < a; k++) fsm[k] = thr; r = a - 1; }
            int bs = 0, be = -1, cs = -1;
            for (int r = 0; r <= nzr; r++)
            {
                bool on = r < nzr && fsm[r] >= thr;
                if (on && cs < 0) cs = r;
                if (!on && cs >= 0) { if (r - cs > be - bs + 1) { bs = cs; be = r - 1; } cs = -1; }
            }
            bandLo = zA + bs * 0.005; bandHi = zA + (be + 1) * 0.005;
            // A solid beam / fascia face on top reads as rows with ~full occupancy: trim a solid cap thicker than 60 mm.
            int top = be; while (top > bs && fs[top] > 0.9) top--;
            if ((be - top) * 0.005 > 0.06) { L($"  trimmed solid cap {bandHi - (zA + (top + 1) * 0.005):F3} m at top of band (beam face)"); bandHi = zA + (top + 1) * 0.005; }
        }
        if (o.Zmin is double zmn) bandLo = zmn;
        if (o.Zmax is double zmx) bandHi = zmx;
        L($"  lace band RL {bandLo:F3} .. {bandHi:F3}  (depth {(bandHi - bandLo) * 1000:F0} mm){(o.Zmin is null && o.Zmax is null ? " (auto)" : " (given/partly given)")}");
        if (bandHi - bandLo < 0.12) warnings.Add($"Detected band is only {(bandHi - bandLo) * 1000:F0} mm deep - probably not lacework (a gutter/fascia/beam face). Check the input z range.");
        if (Math.Abs(bandLo - zA) < 0.011) warnings.Add($"Band bottom coincides with the bottom of the data (RL {zA:F3}): the lace probably continues below the exported slab.");

        // ---- 1d. posts: dense vertical columns in y. Look in a +-120 mm x slab below the band (posts run to the deck),
        // falling back to the band itself (columns that are fully occupied through the band).
        double spacing = EstimateSpacing(plane.Where(p => p[2] >= bandLo && p[2] <= bandHi).ToList());
        double cw = Math.Clamp(spacing * 3, 0.015, 0.04);
        var posts = new List<(double y, double w)>();
        double postW = o.PostWidth ?? 0.11;
        if (o.Posts != null) posts = o.Posts.Select(y => (y, postW)).ToList();
        else
        {
            var below = pts.Where(p => Math.Abs(p[0] - planeX) <= 0.12 && p[2] < bandLo - 0.55 && p[2] > bandLo - 1.6 && p[1] >= yA - 0.1 && p[1] <= yB + 0.1).ToList();
            bool useBelow = below.Count > 200 && below.Max(p => p[2]) - below.Min(p => p[2]) > 0.3;
            var src = useBelow ? below : plane.Where(p => p[2] >= bandLo && p[2] <= bandHi).ToList();
            double zs0 = useBelow ? below.Min(p => p[2]) : bandLo, zs1 = useBelow ? below.Max(p => p[2]) : bandHi;
            int nzz = Math.Max(1, (int)((zs1 - zs0) / cw) + 1), nyy = (int)((yB - yA + 0.2) / cw) + 1;
            var oc = new bool[nyy, nzz];
            foreach (var p in src)
            {
                int c = (int)((p[1] - (yA - 0.1)) / cw), r = (int)((p[2] - zs0) / cw);
                if (c >= 0 && c < nyy && r >= 0 && r < nzz) oc[c, r] = true;
            }
            var colf = new double[nyy];
            for (int c = 0; c < nyy; c++) { int n = 0; for (int r = 0; r < nzz; r++) if (oc[c, r]) n++; colf[c] = (double)n / nzz; }
            double thr = useBelow ? 0.45 : 0.97;
            L($"    post search: column {cw * 1000:F0} mm, top column fractions {string.Join(" ", Enumerable.Range(0, nyy).OrderByDescending(c => colf[c]).Take(8).OrderBy(c => c).Select(c => $"y{yA - 0.1 + (c + 0.5) * cw:F2}:{colf[c]:F2}"))}");
            int s = -1;
            for (int c = 0; c <= nyy; c++)
            {
                bool on = c < nyy && colf[c] >= thr;
                if (on && s < 0) s = c;
                if (!on && s >= 0)
                {
                    double w = (c - s) * cw, yc = yA - 0.1 + (s + c) * cw / 2;
                    if (w >= 0.05 && w <= 0.35) posts.Add((yc, Math.Max(w, 0.06)));
                    else if (w > 0.35 && useBelow) L($"    (ignored wide vertical surface y {yA - 0.1 + s * cw:F3}..{yA - 0.1 + c * cw:F3} - wall?)");
                    s = -1;
                }
            }
            L($"  posts searched {(useBelow ? $"below the band (RL {zs0:F2}..{zs1:F2}, x +-120 mm)" : "within the band only (no data below the band)")}");
        }
        // merge posts closer than 150 mm
        posts = posts.OrderBy(p => p.y).Aggregate(new List<(double y, double w)>(), (acc, p) =>
        {
            if (acc.Count > 0 && p.y - acc[^1].y < 0.15) { var q = acc[^1]; double lo = Math.Min(q.y - q.w / 2, p.y - p.w / 2), hi = Math.Max(q.y + q.w / 2, p.y + p.w / 2); acc[^1] = ((lo + hi) / 2, hi - lo); }
            else acc.Add(p);
            return acc;
        });
        L($"  posts: {(posts.Count == 0 ? "none found" : string.Join(", ", posts.Select(p => $"y {p.y:F3} (w {p.w * 1000:F0})")))}");

        // ---- 2. raster
        var band = plane.Where(p => p[2] >= bandLo && p[2] <= bandHi && p[1] >= yA && p[1] <= yB).ToList();
        spacing = EstimateSpacing(band);
        double px = o.Px ?? (o.NoFold ? Math.Max(0.004, Math.Round(spacing * 0.7 / 0.001) * 0.001) : 0.004);
        L($"  {band.Count} points in band slab; estimated sample spacing on iron {spacing * 1000:F1} mm -> pixel {px * 1000:F0} mm");
        if (spacing > 0.006) warnings.Add($"Sample spacing on the lace is ~{spacing * 1000:F0} mm (needs <= ~4 mm for 15-30 mm piercings): piercings below ~{2 * spacing * 1000:F0} mm cannot be resolved; result is a coarse approximation.");
        double y0 = yA, z0 = bandLo;
        int W = (int)Math.Ceiling((yB - yA) / px), H = (int)Math.Ceiling((bandHi - bandLo) / px);
        var cnt = new int[W, H];
        foreach (var p in band)
        {
            int i = (int)((p[1] - y0) / px), j = (int)((p[2] - z0) / px);
            if (i >= 0 && i < W && j >= 0 && j < H) cnt[i, j]++;
        }
        var nz = new List<int>(); foreach (var v in cnt) if (v > 0) nz.Add(v);
        double meanNz = nz.Count > 0 ? nz.Average() : 0;
        int thrCount = meanNz >= 4 ? 2 : 1;
        var raw = new bool[W, H];
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++) raw[i, j] = cnt[i, j] >= thrCount;
        var mask = raw;
        for (int k = 0; k < o.Close; k++) mask = Dilate(mask);
        for (int k = 0; k < o.Close; k++) mask = Erode(mask, keepBorder: true);
        // posts out
        var postCol = new bool[W];
        foreach (var p in posts)
            for (int i = Math.Max(0, (int)((p.y - p.w / 2 - y0) / px)); i <= Math.Min(W - 1, (int)((p.y + p.w / 2 - y0) / px)); i++) postCol[i] = true;
        for (int i = 0; i < W; i++) if (postCol[i]) for (int j = 0; j < H; j++) mask[i, j] = false;
        int minPx = (int)Math.Ceiling(o.MinAreaMm2 / (px * px * 1e6)), minHolePx = (int)Math.Ceiling(o.MinHoleMm2 / (px * px * 1e6));
        int removedSpecks = RemoveSmall(mask, true, minPx), filledHoles = RemoveSmall(mask, false, minHolePx);
        L($"  occupancy threshold >= {thrCount} pts/pixel (mean {meanNz:F1} on occupied pixels); close radius {o.Close}; removed {removedSpecks} specks < {o.MinAreaMm2:F0} mm2, filled {filledHoles} pinholes < {o.MinHoleMm2:F0} mm2");

        // resolution: per 40 mm cell, spacing on iron = sqrt(iron area / points); resolved if <= 5 mm.
        int cellPx = Math.Max(1, (int)Math.Round(0.04 / px)), cellsIron = 0, cellsRes = 0;
        for (int ci = 0; ci < W; ci += cellPx) for (int cj = 0; cj < H; cj += cellPx)
        {
            int ironPx = 0, n = 0;
            for (int i = ci; i < Math.Min(W, ci + cellPx); i++) for (int j = cj; j < Math.Min(H, cj + cellPx); j++) { if (mask[i, j]) ironPx++; n += cnt[i, j]; }
            if (ironPx == 0) continue;
            cellsIron++;
            if (n > 0 && Math.Sqrt(ironPx * px * px / n) <= 0.005) cellsRes++;
        }
        double resolved = cellsIron > 0 ? (double)cellsRes / cellsIron : 0;
        int ironTot = 0; foreach (var v in mask) if (v) ironTot++;
        double fill = (double)ironTot / Math.Max(1, W * H - postCol.Count(c => c) * H);
        L($"  iron fill {fill:P0} of band; {resolved:P0} of 40 mm iron cells have <= 5 mm sample spacing (resolved)");
        if (fill > 0.85) warnings.Add($"Mask is {fill:P0} solid - reads as a solid band, not pierced lace.");

        // ---- 2b. repeat folding: lacework is a repeating casting. Find the period along y, fold all repeats into one
        // module (density x repeats), threshold/trace that, then tile it back along each bay.
        FoldInfo fold = null;
        if (!o.NoFold)
        {
            fold = Fold(band, posts, postCol, W, H, px, y0, z0, o, L, prefix);
            if (fold != null)
            {
                mask = fold.Tiled;
                RemoveSmall(mask, true, minPx); RemoveSmall(mask, false, minHolePx);
                ironTot = 0; foreach (var v in mask) if (v) ironTot++;
                fill = (double)ironTot / Math.Max(1, W * H - postCol.Count(c => c) * H);
                L($"  tiled module: iron fill {fill:P0} of band");
            }
            else warnings.Add("No clear repeat period found - traced the raw (unfolded) raster.");
        }

        // ---- bays: split at posts; extents trimmed to iron.
        var bays = new List<(int i0, int i1)>();
        { int s = -1; for (int i = 0; i <= W; i++) { bool on = i < W && !postCol[i]; if (on && s < 0) s = i; if (!on && s >= 0) { bays.Add((s, i - 1)); s = -1; } } }
        var bayOut = new List<BayResult>();
        foreach (var (i0, i1) in bays)
        {
            int a = i0, b = i1;
            while (a <= b && !ColAny(mask, a, H)) a++;
            while (b >= a && !ColAny(mask, b, H)) b--;
            if (b - a + 1 < (int)(0.10 / px)) continue;   // < 100 mm of iron: offcut, not a bay
            var loops = Trace(mask, a, b, H);
            var poly = loops.Select(lp => Simplify(lp.Select(q => new[] { y0 + q[0] * px, z0 + q[1] * px }).ToList(), o.Dp)).Where(l => l.Count >= 3).ToList();
            var outers = poly.Where(l => SignedArea(l) > 0).ToList();
            var holes = poly.Where(l => SignedArea(l) < 0).ToList();
            outers = outers.Where(l => SignedArea(l) * 1e6 >= o.MinAreaMm2).ToList();
            var r = new BayResult { Y0 = y0 + a * px, Y1 = y0 + (b + 1) * px, Outers = outers };
            foreach (var oL in outers) r.Holes.Add(new List<List<double[]>>());
            foreach (var h in holes)
            {
                var hp = h[0]; int bestK = -1; double bestA = double.MaxValue;
                for (int k = 0; k < outers.Count; k++)
                {
                    double ar = SignedArea(outers[k]);
                    if (ar < bestA && PointIn(outers[k], hp)) { bestA = ar; bestK = k; }
                }
                if (bestK >= 0) r.Holes[bestK].Add(h);
            }
            BuildMesh(r, planeX, thick);
            bayOut.Add(r);
        }

        // ---- outputs
        string dir = Path.GetDirectoryName(Path.GetFullPath(prefix + "-x"));
        Directory.CreateDirectory(dir);
        var json = new StringBuilder("[");
        for (int k = 0; k < bayOut.Count; k++)
        {
            var r = bayOut[k];
            if (k > 0) json.Append(',');
            json.Append("{\"name\":").Append(Q($"NEIGHBOUR {name} - lacework frieze bay {k + 1} (traced from scan)"))
                .Append(",\"category\":\"Generic Models\",\"vertices\":[");
            for (int v = 0; v < r.V.Count; v++) { if (v > 0) json.Append(','); json.Append('[').Append(F(r.V[v][0])).Append(',').Append(F(r.V[v][1])).Append(',').Append(F(r.V[v][2])).Append(']'); }
            json.Append("],\"faces\":[");
            for (int f = 0; f < r.F.Count; f++) { if (f > 0) json.Append(','); json.Append('[').Append(r.F[f][0]).Append(',').Append(r.F[f][1]).Append(',').Append(r.F[f][2]).Append(']'); }
            json.Append("],\"comments\":").Append(Q($"Cast-iron lace frieze traced from TLS ({Path.GetFileName(csv)}): plane x {planeX:F3}, {thick * 1000:F0} mm thick, RL {bandLo:F3}-{bandHi:F3}, y {r.Y0:F3}..{r.Y1:F3}. Raster {px * 1000:F0} mm, sample spacing ~{spacing * 1000:F0} mm, {resolved:P0} of band resolved. Approximate - not a measured casting pattern.")).Append('}');
        }
        json.Append(']');
        File.WriteAllText(prefix + "-lace.json", json.ToString());

        int tri = bayOut.Sum(r => r.F.Count), nOuter = bayOut.Sum(r => r.Outers.Count), nHoles = bayOut.Sum(r => r.Holes.Sum(h => h.Count));
        var sum = new StringBuilder();
        sum.Append("{\n");
        sum.Append($"  \"name\": {Q(name)},\n  \"source\": {Q(Path.GetFullPath(csv))},\n");
        sum.Append($"  \"planeX\": {F(planeX)},\n  \"thickness\": {F(thick)},\n  \"thicknessMeasuredP10P90\": {F(thickMeas)},\n");
        sum.Append($"  \"bandZBottom\": {F(bandLo)},\n  \"bandZTop\": {F(bandHi)},\n  \"yExtent\": [{F(yA)}, {F(yB)}],\n");
        sum.Append($"  \"posts\": [{string.Join(", ", posts.Select(p => $"{{\"y\": {F(p.y)}, \"width\": {F(p.w)}}}"))}],\n");
        sum.Append($"  \"bays\": [{string.Join(", ", bayOut.Select((r, k) => $"{{\"bay\": {k + 1}, \"y0\": {F(r.Y0)}, \"y1\": {F(r.Y1)}, \"outerLoops\": {r.Outers.Count}, \"holes\": {r.Holes.Sum(h => h.Count)}, \"triangles\": {r.F.Count}}}"))}],\n");
        sum.Append($"  \"outerLoops\": {nOuter},\n  \"holes\": {nHoles},\n  \"triangles\": {tri},\n");
        sum.Append($"  \"pixelSize\": {F(px)},\n  \"sampleSpacing\": {F(spacing)},\n  \"ironFill\": {F(fill)},\n  \"resolvedFraction\": {F(resolved)},\n");
        sum.Append($"  \"warnings\": [{string.Join(", ", warnings.Select(Q))}]\n}}\n");
        File.WriteAllText(prefix + "-summary.json", sum.ToString());

        SaveMask(prefix + "-mask.png", raw, mask, postCol, W, H, px, y0, z0);
        SavePreview(prefix + "-preview.png", bayOut, yA, yB, bandLo, bandHi, posts);
        L($"  bays {bayOut.Count}: {string.Join("; ", bayOut.Select((r, k) => $"#{k + 1} y {r.Y0:F3}..{r.Y1:F3} loops {r.Outers.Count}/{r.Holes.Sum(h => h.Count)} holes, {r.F.Count} tris"))}");
        L($"  total: {nOuter} outer loops, {nHoles} holes, {tri} triangles");
        foreach (var w in warnings) L($"  WARNING: {w}");
        File.WriteAllText(prefix + "-log.txt", log.ToString());
        return $"-> {prefix}-lace.json, -summary.json, -mask.png, -preview.png";
    }

    sealed class BayResult
    {
        public double Y0, Y1;
        public List<List<double[]>> Outers;
        public List<List<List<double[]>>> Holes = new();
        public List<double[]> V = new();
        public List<int[]> F = new();
    }

    // ------------------------------------------------------------------------------------------------ helpers
    static string F(double v) => Math.Round(v, 5).ToString("0.#####", Inv);
    static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);
    static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
    static double Pct(List<double> sorted, double q) => sorted[Math.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
    static bool ColAny(bool[,] m, int i, int H) { for (int j = 0; j < H; j++) if (m[i, j]) return true; return false; }

    // Sample spacing on the iron: 1 mm grid occupancy vs point count in 20 mm cells (dense cells = 90th pct).
    static double EstimateSpacing(List<double[]> band)
    {
        // median nearest-neighbour distance in (y,z) over a sample of points (grid hash, 50 mm cells)
        if (band.Count < 50) return 0.05;
        const double G = 0.05;
        var grid = new Dictionary<(int, int), List<double[]>>();
        foreach (var p in band)
        {
            var k = ((int)Math.Floor(p[1] / G), (int)Math.Floor(p[2] / G));
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<double[]>();
            l.Add(p);
        }
        var d = new List<double>(); int step = Math.Max(1, band.Count / 4000);
        for (int n = 0; n < band.Count; n += step)
        {
            var p = band[n]; int gy = (int)Math.Floor(p[1] / G), gz = (int)Math.Floor(p[2] / G); double best = G * G;
            for (int a = -1; a <= 1; a++) for (int b = -1; b <= 1; b++)
                if (grid.TryGetValue((gy + a, gz + b), out var l))
                    foreach (var q in l)
                    {
                        if (ReferenceEquals(q, p)) continue;
                        double dd = (q[1] - p[1]) * (q[1] - p[1]) + (q[2] - p[2]) * (q[2] - p[2]);
                        if (dd > 1e-12 && dd < best) best = dd;
                    }
            d.Add(Math.Sqrt(best));
        }
        d.Sort();
        // NN distance underestimates grid spacing slightly for jittered scans; use the 60th percentile.
        return d[(int)(0.6 * (d.Count - 1))];
    }

    static bool[,] Dilate(bool[,] m)
    {
        int W = m.GetLength(0), H = m.GetLength(1); var r = new bool[W, H];
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            if (!m[i, j]) continue;
            for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
            { int a = i + di, b = j + dj; if (a >= 0 && a < W && b >= 0 && b < H) r[a, b] = true; }
        }
        return r;
    }
    static bool[,] Erode(bool[,] m, bool keepBorder)
    {
        int W = m.GetLength(0), H = m.GetLength(1); var r = new bool[W, H];
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            if (!m[i, j]) continue;
            bool ok = true;
            for (int di = -1; di <= 1 && ok; di++) for (int dj = -1; dj <= 1 && ok; dj++)
            {
                int a = i + di, b = j + dj;
                if (a < 0 || a >= W || b < 0 || b >= H) { if (!keepBorder) ok = false; }
                else if (!m[a, b]) ok = false;
            }
            r[i, j] = ok;
        }
        return r;
    }

    // Remove 4-connected components of value `val` smaller than minPx (for holes: only those not touching the border).
    static int RemoveSmall(bool[,] m, bool val, int minPx)
    {
        int W = m.GetLength(0), H = m.GetLength(1), removed = 0;
        var seen = new bool[W, H]; var st = new Stack<(int, int)>(); var comp = new List<(int, int)>();
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            if (seen[i, j] || m[i, j] != val) continue;
            comp.Clear(); st.Push((i, j)); seen[i, j] = true; bool border = false;
            while (st.Count > 0)
            {
                var (a, b) = st.Pop(); comp.Add((a, b));
                if (a == 0 || b == 0 || a == W - 1 || b == H - 1) border = true;
                foreach (var (da, db) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int x = a + da, y = b + db;
                    if (x >= 0 && x < W && y >= 0 && y < H && !seen[x, y] && m[x, y] == val) { seen[x, y] = true; st.Push((x, y)); }
                }
            }
            if (comp.Count < minPx && (val || !border)) { foreach (var (a, b) in comp) m[a, b] = !val; removed++; }
        }
        return removed;
    }

    // Crack-following boundary tracing on columns i0..i1 of the mask: directed pixel edges with iron on the LEFT,
    // linked into closed loops (right turn preferred at saddles -> 4-connected iron), vertices = edge midpoints
    // (equivalent to marching squares). Outer loops come out CCW (+area), holes CW. Coordinates in pixel units.
    static List<List<double[]>> Trace(bool[,] m, int i0, int i1, int H)
    {
        int W = i1 - i0 + 1;
        bool In(int i, int j) => i >= 0 && i < W && j >= 0 && j < H && m[i0 + i, j];
        int VW = W + 1, VH = H + 1;
        var outE = new byte[VW * VH];   // bit d: outgoing edge in direction d (0 +u, 1 +v, 2 -u, 3 -v)
        int[] du = { 1, 0, -1, 0 }, dv = { 0, 1, 0, -1 };
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            if (!In(i, j)) continue;
            if (!In(i, j - 1)) outE[j * VW + i] |= 1;                // bottom: (i,j) -> (i+1,j)
            if (!In(i + 1, j)) outE[j * VW + i + 1] |= 2;            // right: (i+1,j) -> (i+1,j+1)
            if (!In(i, j + 1)) outE[(j + 1) * VW + i + 1] |= 4;      // top: (i+1,j+1) -> (i,j+1)
            if (!In(i - 1, j)) outE[(j + 1) * VW + i] |= 8;          // left: (i,j+1) -> (i,j)
        }
        var loops = new List<List<double[]>>();
        for (int s = 0; s < outE.Length; s++)
        {
            while (outE[s] != 0)
            {
                int d = 0; while ((outE[s] & (1 << d)) == 0) d++;
                var loop = new List<double[]>();
                int v = s, guard = 0;
                while (true)
                {
                    outE[v] &= (byte)~(1 << d);
                    int u = v % VW, w = v / VW;
                    loop.Add(new[] { i0 + u + du[d] * 0.5, w + dv[d] * 0.5 });
                    v = (w + dv[d]) * VW + (u + du[d]);
                    int nd = -1;
                    foreach (var cand in new[] { (d + 3) % 4, d, (d + 1) % 4 }) if ((outE[v] & (1 << cand)) != 0) { nd = cand; break; }
                    if (nd < 0 || ++guard > 10_000_000) break;
                    d = nd;
                }
                if (loop.Count >= 4) loops.Add(loop);
            }
        }
        return loops;
    }

    // Closed-polyline Douglas-Peucker (split at vertex 0 and the vertex farthest from it).
    static List<double[]> Simplify(List<double[]> pts, double tol)
    {
        int n = pts.Count; if (n < 4) return pts;
        int far = 0; double fd = -1;
        for (int k = 1; k < n; k++) { double d = Dist2(pts[0], pts[k]); if (d > fd) { fd = d; far = k; } }
        var keep = new bool[n]; keep[0] = keep[far] = true;
        Dp(pts, 0, far, tol, keep);
        var tail = pts.Skip(far).Concat(new[] { pts[0] }).ToList();
        var keep2 = new bool[tail.Count]; Dp(tail, 0, tail.Count - 1, tol, keep2);
        for (int k = 1; k < tail.Count - 1; k++) if (keep2[k]) keep[far + k] = true;
        var r = new List<double[]>(); for (int k = 0; k < n; k++) if (keep[k]) r.Add(pts[k]);
        return r;
    }
    static void Dp(List<double[]> p, int a, int b, double tol, bool[] keep)
    {
        if (b <= a + 1) return;
        int idx = -1; double md = tol;
        for (int k = a + 1; k < b; k++) { double d = SegDist(p[k], p[a], p[b]); if (d > md) { md = d; idx = k; } }
        if (idx < 0) return;
        keep[idx] = true; Dp(p, a, idx, tol, keep); Dp(p, idx, b, tol, keep);
    }
    static double Dist2(double[] a, double[] b) => (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]);
    static double SegDist(double[] p, double[] a, double[] b)
    {
        double dx = b[0] - a[0], dy = b[1] - a[1], l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / l2, 0, 1);
        double ex = a[0] + t * dx - p[0], ey = a[1] + t * dy - p[1];
        return Math.Sqrt(ex * ex + ey * ey);
    }
    static double SignedArea(List<double[]> l)
    {
        double s = 0; for (int k = 0, j = l.Count - 1; k < l.Count; j = k++) s += (l[j][0] - l[k][0]) * (l[j][1] + l[k][1]);
        return s / 2;
    }
    static bool PointIn(List<double[]> poly, double[] p)
    {
        bool c = false;
        for (int k = 0, j = poly.Count - 1; k < poly.Count; j = k++)
            if (((poly[k][1] > p[1]) != (poly[j][1] > p[1])) && p[0] < (poly[j][0] - poly[k][0]) * (p[1] - poly[k][1]) / (poly[j][1] - poly[k][1]) + poly[k][0]) c = !c;
        return c;
    }

    // Extrude: front cap at x - t/2 (faces -x, street), back cap at x + t/2, walls along every loop. Loops have iron on
    // the left, so outward = right of travel; (af, bf, bb) is outward with back at larger x.
    static void BuildMesh(BayResult r, double planeX, double t)
    {
        double xf = planeX - t / 2, xb = planeX + t / 2;
        for (int k = 0; k < r.Outers.Count; k++)
        {
            var rings = new List<List<double[]>> { r.Outers[k] }; rings.AddRange(r.Holes[k]);
            int baseF = r.V.Count;
            var flat = new List<double>(); var holeIdx = new List<int>(); int nv = 0;
            foreach (var ring in rings)
            {
                if (ring != rings[0]) holeIdx.Add(nv);
                foreach (var q in ring) { flat.Add(q[0]); flat.Add(q[1]); nv++; }
            }
            for (int v = 0; v < nv; v++) r.V.Add(new[] { xf, flat[2 * v], flat[2 * v + 1] });
            int baseB = r.V.Count;
            for (int v = 0; v < nv; v++) r.V.Add(new[] { xb, flat[2 * v], flat[2 * v + 1] });
            var tris = Earcut.Triangulate(flat.ToArray(), holeIdx.ToArray());
            for (int q = 0; q < tris.Count; q += 3)
            {
                int a = tris[q], b = tris[q + 1], c = tris[q + 2];
                double ar = (flat[2 * b] - flat[2 * a]) * (flat[2 * c + 1] - flat[2 * a + 1]) - (flat[2 * c] - flat[2 * a]) * (flat[2 * b + 1] - flat[2 * a + 1]);
                if (ar == 0) continue;
                if (ar < 0) (b, c) = (c, b);                       // now CCW in (y,z) => normal +x
                r.F.Add(new[] { baseB + a, baseB + b, baseB + c });   // back, faces +x
                r.F.Add(new[] { baseF + a, baseF + c, baseF + b });   // front, faces -x
            }
            int off = 0;
            foreach (var ring in rings)
            {
                for (int v = 0; v < ring.Count; v++)
                {
                    int a = off + v, b = off + (v + 1) % ring.Count;
                    r.F.Add(new[] { baseF + a, baseF + b, baseB + b });
                    r.F.Add(new[] { baseF + a, baseB + b, baseB + a });
                }
                off += ring.Count;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ images
    static void SaveMask(string png, bool[,] raw, bool[,] mask, bool[] postCol, int W, int H, double px, double y0, double z0)
    {
        // Elevation seen from the street (west, looking east): north (+y) on the LEFT, so image u = -(y).
        int s = Math.Max(2, (int)Math.Round(px / 0.0025));             // ~2.5 mm per image pixel
        int rowsW = Math.Min(W, (int)Math.Ceiling(1.5 / px));           // wrap every 1.5 m so motifs stay readable
        int nRows = (W + rowsW - 1) / rowsW, gap = 26;
        int IW = rowsW * s + 20, IH = nRows * (H * s + gap) + 10;
        using var bmp = new Bitmap(IW, IH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var font = new Font("Arial", 9);
        for (int row = 0; row < nRows; row++)
        {
            int oy = row * (H * s + gap) + gap - 6;
            for (int k = 0; k < rowsW; k++)
            {
                int i = W - 1 - (row * rowsW + k); if (i < 0) break;
                for (int j = 0; j < H; j++)
                {
                    Color c = postCol[i] ? Color.FromArgb(255, 215, 215) : Color.White;
                    if (raw[i, j]) c = Color.FromArgb(185, 200, 235);
                    if (mask[i, j]) c = raw[i, j] ? Color.Black : Color.FromArgb(70, 70, 70);
                    g.FillRectangle(new SolidBrush(c), 10 + k * s, oy + (H - 1 - j) * s, s, s);
                }
            }
            double yl = y0 + (W - row * rowsW) * px, yr = y0 + Math.Max(0, W - (row + 1) * rowsW) * px;
            g.DrawString($"y {yl:F2} (left, north) .. {yr:F2}   RL {z0:F3}..{z0 + H * px:F3}   px {px * 1000:F0} mm  black=iron grey=closed blue=raw-only pink=post", font, Brushes.DarkRed, 10, oy - 16);
        }
        bmp.Save(png, ImageFormat.Png);
    }

    static void SavePreview(string png, List<BayResult> bays, double yA, double yB, double zA, double zB, List<(double y, double w)> posts)
    {
        double scale = Math.Min(2400 / Math.Max(0.1, yB - yA), 1000);     // px per metre
        int rowsH = (int)((zB - zA) * scale) + 40;
        double wrap = 1.5; scale = 2400 / wrap / 1.6;                      // 1.5 m per strip
        rowsH = (int)((zB - zA) * scale) + 30;
        int nRows = (int)Math.Ceiling((yB - yA) / wrap);
        int IW = (int)(wrap * scale) + 20, IH = nRows * rowsH + 10;
        using var bmp = new Bitmap(IW, IH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Arial", 9);
        using var penO = new Pen(Color.Black, 1); using var penH = new Pen(Color.Red, 1);
        using var fillB = new SolidBrush(Color.FromArgb(150, 120, 90));
        for (int row = 0; row < nRows; row++)
        {
            double yLeft = yB - row * wrap;                                  // seen from the west: north on the left
            int oy = row * rowsH + 22;
            var st = g.Save();
            g.SetClip(new Rectangle(10, oy, IW - 20, rowsH - 22));
            PointF P(double[] q) => new PointF((float)(10 + (yLeft - q[0]) * scale), (float)(oy + (zB - q[1]) * scale));
            foreach (var r in bays)
                for (int k = 0; k < r.Outers.Count; k++)
                {
                    using var path = new GraphicsPath(FillMode.Alternate);
                    path.AddPolygon(r.Outers[k].Select(P).ToArray());
                    foreach (var h in r.Holes[k]) path.AddPolygon(h.Select(P).ToArray());
                    g.FillPath(fillB, path);
                    g.DrawPolygon(penO, r.Outers[k].Select(P).ToArray());
                    foreach (var h in r.Holes[k]) g.DrawPolygon(penH, h.Select(P).ToArray());
                }
            foreach (var p in posts)
            {
                float x0 = (float)(10 + (yLeft - p.y - p.w / 2) * scale);
                g.FillRectangle(new SolidBrush(Color.FromArgb(60, 0, 0, 255)), x0, oy, (float)(p.w * scale), rowsH - 22);
            }
            g.Restore(st);
            g.DrawString($"y {yLeft:F2} .. {yLeft - wrap:F2} (seen from street, north left)   RL {zA:F3}..{zB:F3}   brown=iron, black=outer loops, red=piercings, blue=posts", font, Brushes.DarkRed, 10, oy - 18);
        }
        bmp.Save(png, ImageFormat.Png);
    }

    // ------------------------------------------------------------------------------------------------ diagnostics
    // lacediag2 <csv> <png> <ia> <ib> <px> xmin xmax ymin ymax zmin zmax : log-density image of a filtered slab
    public static void Diag2(string[] a)
    {
        var pts = Load(a[1]);
        double P(int i) => double.Parse(a[i], Inv);
        var f = pts.Where(p => p[0] >= P(6) && p[0] <= P(7) && p[1] >= P(8) && p[1] <= P(9) && p[2] >= P(10) && p[2] <= P(11)).ToList();
        Console.WriteLine($"{f.Count} pts");
        if (f.Count > 0) Density(f, int.Parse(a[3]), int.Parse(a[4]), P(5), a[2]);
    }

    public static void Density(List<double[]> pts, int ia, int ib, double px, string png)
    {
        double a0 = pts.Min(p => p[ia]), a1 = pts.Max(p => p[ia]), b0 = pts.Min(p => p[ib]), b1 = pts.Max(p => p[ib]);
        int W = (int)((a1 - a0) / px) + 1, H = (int)((b1 - b0) / px) + 1;
        var c = new int[W, H];
        foreach (var p in pts) c[(int)((p[ia] - a0) / px), (int)((p[ib] - b0) / px)]++;
        int mx = 1; foreach (var v in c) mx = Math.Max(mx, v);
        using var bmp = new Bitmap(W, H);
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            int k = c[i, j] == 0 ? 255 : (int)(230 - 230 * Math.Log(1 + c[i, j]) / Math.Log(1 + mx));
            bmp.SetPixel(i, H - 1 - j, Color.FromArgb(k, k, k));
        }
        bmp.Save(png, ImageFormat.Png);
        Console.WriteLine($"{png}: {W}x{H}  a {a0:F3}..{a1:F3}  b {b0:F3}..{b1:F3}");
    }
}


// Port of mapbox/earcut (ISC licence): polygon-with-holes triangulation by ear clipping with hole bridging
// and z-order hashing. data = flat [x0,y0,x1,y1,...]; holeIndices = vertex index where each hole ring starts.
public static class Earcut
{
    sealed class Node
    {
        public int i; public double x, y; public Node prev, next, prevZ, nextZ; public int z; public bool steiner;
        public Node(int i, double x, double y) { this.i = i; this.x = x; this.y = y; }
    }

    public static List<int> Triangulate(double[] data, int[] holeIndices)
    {
        const int dim = 2;
        bool hasHoles = holeIndices != null && holeIndices.Length > 0;
        int outerLen = hasHoles ? holeIndices[0] * dim : data.Length;
        var outerNode = LinkedList(data, 0, outerLen, true);
        var tris = new List<int>();
        if (outerNode == null || outerNode.next == outerNode.prev) return tris;
        if (hasHoles) outerNode = EliminateHoles(data, holeIndices, outerNode);
        double minX = 0, minY = 0, invSize = 0;
        if (data.Length > 80 * dim)
        {
            minX = double.MaxValue; minY = double.MaxValue; double maxX = double.MinValue, maxY = double.MinValue;
            for (int k = 0; k < outerLen; k += dim)
            {
                minX = Math.Min(minX, data[k]); minY = Math.Min(minY, data[k + 1]);
                maxX = Math.Max(maxX, data[k]); maxY = Math.Max(maxY, data[k + 1]);
            }
            invSize = Math.Max(maxX - minX, maxY - minY);
            invSize = invSize != 0 ? 32767 / invSize : 0;
        }
        EarcutLinked(outerNode, tris, minX, minY, invSize, 0);
        return tris;
    }

    static Node LinkedList(double[] data, int start, int end, bool clockwise)
    {
        Node last = null;
        if (clockwise == (SignedArea(data, start, end) > 0))
            for (int k = start; k < end; k += 2) last = InsertNode(k / 2, data[k], data[k + 1], last);
        else
            for (int k = end - 2; k >= start; k -= 2) last = InsertNode(k / 2, data[k], data[k + 1], last);
        if (last != null && Equals(last, last.next)) { RemoveNode(last); last = last.next; }
        return last;
    }

    static Node FilterPoints(Node start, Node end = null)
    {
        if (start == null) return start;
        end ??= start;
        var p = start; bool again;
        do
        {
            again = false;
            if (!p.steiner && (Equals(p, p.next) || Area(p.prev, p, p.next) == 0))
            {
                RemoveNode(p); p = end = p.prev;
                if (p == p.next) break;
                again = true;
            }
            else p = p.next;
        } while (again || p != end);
        return end;
    }

    static void EarcutLinked(Node ear, List<int> tris, double minX, double minY, double invSize, int pass)
    {
        if (ear == null) return;
        if (pass == 0 && invSize != 0) IndexCurve(ear, minX, minY, invSize);
        var stop = ear;
        while (ear.prev != ear.next)
        {
            var prev = ear.prev; var next = ear.next;
            if (invSize != 0 ? IsEarHashed(ear, minX, minY, invSize) : IsEar(ear))
            {
                tris.Add(prev.i); tris.Add(ear.i); tris.Add(next.i);
                RemoveNode(ear);
                ear = next.next; stop = next.next;
                continue;
            }
            ear = next;
            if (ear == stop)
            {
                if (pass == 0) EarcutLinked(FilterPoints(ear), tris, minX, minY, invSize, 1);
                else if (pass == 1)
                {
                    ear = CureLocalIntersections(FilterPoints(ear), tris);
                    EarcutLinked(ear, tris, minX, minY, invSize, 2);
                }
                else if (pass == 2) SplitEarcut(ear, tris, minX, minY, invSize);
                break;
            }
        }
    }

    static bool IsEar(Node ear)
    {
        Node a = ear.prev, b = ear, c = ear.next;
        if (Area(a, b, c) >= 0) return false;
        var p = ear.next.next;
        while (p != ear.prev)
        {
            if (PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, p.x, p.y) && Area(p.prev, p, p.next) >= 0) return false;
            p = p.next;
        }
        return true;
    }

    static bool IsEarHashed(Node ear, double minX, double minY, double invSize)
    {
        Node a = ear.prev, b = ear, c = ear.next;
        if (Area(a, b, c) >= 0) return false;
        double minTX = Math.Min(a.x, Math.Min(b.x, c.x)), minTY = Math.Min(a.y, Math.Min(b.y, c.y));
        double maxTX = Math.Max(a.x, Math.Max(b.x, c.x)), maxTY = Math.Max(a.y, Math.Max(b.y, c.y));
        int minZ = ZOrder(minTX, minTY, minX, minY, invSize), maxZ = ZOrder(maxTX, maxTY, minX, minY, invSize);
        Node p = ear.prevZ, n = ear.nextZ;
        bool Blocks(Node q) => q != ear.prev && q != ear.next && q != ear &&
            PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, q.x, q.y) && Area(q.prev, q, q.next) >= 0;
        while (p != null && p.z >= minZ && n != null && n.z <= maxZ)
        {
            if (Blocks(p)) return false; p = p.prevZ;
            if (Blocks(n)) return false; n = n.nextZ;
        }
        while (p != null && p.z >= minZ) { if (Blocks(p)) return false; p = p.prevZ; }
        while (n != null && n.z <= maxZ) { if (Blocks(n)) return false; n = n.nextZ; }
        return true;
    }

    static Node CureLocalIntersections(Node start, List<int> tris)
    {
        var p = start;
        do
        {
            Node a = p.prev, b = p.next.next;
            if (!Equals(a, b) && Intersects(a, p, p.next, b) && LocallyInside(a, b) && LocallyInside(b, a))
            {
                tris.Add(a.i); tris.Add(p.i); tris.Add(b.i);
                RemoveNode(p); RemoveNode(p.next);
                p = start = b;
            }
            p = p.next;
        } while (p != start);
        return FilterPoints(p);
    }

    static void SplitEarcut(Node start, List<int> tris, double minX, double minY, double invSize)
    {
        var a = start;
        do
        {
            var b = a.next.next;
            while (b != a.prev)
            {
                if (a.i != b.i && IsValidDiagonal(a, b))
                {
                    var c = SplitPolygon(a, b);
                    a = FilterPoints(a, a.next); c = FilterPoints(c, c.next);
                    EarcutLinked(a, tris, minX, minY, invSize, 0);
                    EarcutLinked(c, tris, minX, minY, invSize, 0);
                    return;
                }
                b = b.next;
            }
            a = a.next;
        } while (a != start);
    }

    static Node EliminateHoles(double[] data, int[] holeIndices, Node outerNode)
    {
        var queue = new List<Node>();
        for (int k = 0; k < holeIndices.Length; k++)
        {
            int start = holeIndices[k] * 2, end = k < holeIndices.Length - 1 ? holeIndices[k + 1] * 2 : data.Length;
            var list = LinkedList(data, start, end, false);
            if (list == null) continue;
            if (list == list.next) list.steiner = true;
            queue.Add(GetLeftmost(list));
        }
        queue.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
        foreach (var h in queue) outerNode = EliminateHole(h, outerNode);
        return outerNode;
    }

    static Node EliminateHole(Node hole, Node outerNode)
    {
        var bridge = FindHoleBridge(hole, outerNode);
        if (bridge == null) return outerNode;
        var bridgeReverse = SplitPolygon(bridge, hole);
        var filtered = FilterPoints(bridge, bridge.next);
        FilterPoints(bridgeReverse, bridgeReverse.next);
        return outerNode == bridge ? filtered : outerNode;
    }

    static Node FindHoleBridge(Node hole, Node outerNode)
    {
        var p = outerNode; double hx = hole.x, hy = hole.y, qx = double.NegativeInfinity; Node m = null;
        do
        {
            if (hy <= p.y && hy >= p.next.y && p.next.y != p.y)
            {
                double x = p.x + (hy - p.y) * (p.next.x - p.x) / (p.next.y - p.y);
                if (x <= hx && x > qx)
                {
                    qx = x; m = p.x < p.next.x ? p : p.next;
                    if (x == hx) return m;
                }
            }
            p = p.next;
        } while (p != outerNode);
        if (m == null) return null;
        var stop = m; double mx = m.x, my = m.y, tanMin = double.PositiveInfinity;
        p = m;
        do
        {
            if (hx >= p.x && p.x >= mx && hx != p.x &&
                PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.x, p.y))
            {
                double tan = Math.Abs(hy - p.y) / (hx - p.x);
                if (LocallyInside(p, hole) && (tan < tanMin || (tan == tanMin && (p.x > m.x || (p.x == m.x && SectorContainsSector(m, p))))))
                { m = p; tanMin = tan; }
            }
            p = p.next;
        } while (p != stop);
        return m;
    }

    static bool SectorContainsSector(Node m, Node p) => Area(m.prev, m, p.prev) < 0 && Area(p.next, m, m.next) < 0;

    static void IndexCurve(Node start, double minX, double minY, double invSize)
    {
        var p = start;
        do
        {
            p.z = ZOrder(p.x, p.y, minX, minY, invSize);
            p.prevZ = p.prev; p.nextZ = p.next; p = p.next;
        } while (p != start);
        p.prevZ.nextZ = null; p.prevZ = null;
        SortLinked(p);
    }

    static Node SortLinked(Node list)
    {
        int inSize = 1, numMerges;
        do
        {
            var p = list; list = null; Node tail = null; numMerges = 0;
            while (p != null)
            {
                numMerges++;
                var q = p; int pSize = 0;
                for (int k = 0; k < inSize; k++) { pSize++; q = q.nextZ; if (q == null) break; }
                int qSize = inSize;
                while (pSize > 0 || (qSize > 0 && q != null))
                {
                    Node e;
                    if (pSize != 0 && (qSize == 0 || q == null || p.z <= q.z)) { e = p; p = p.nextZ; pSize--; }
                    else { e = q; q = q.nextZ; qSize--; }
                    if (tail != null) tail.nextZ = e; else list = e;
                    e.prevZ = tail; tail = e;
                }
                p = q;
            }
            tail.nextZ = null;
            inSize *= 2;
        } while (numMerges > 1);
        return list;
    }

    static int ZOrder(double fx, double fy, double minX, double minY, double invSize)
    {
        int x = (int)((fx - minX) * invSize), y = (int)((fy - minY) * invSize);
        x = (x | (x << 8)) & 0x00FF00FF; x = (x | (x << 4)) & 0x0F0F0F0F; x = (x | (x << 2)) & 0x33333333; x = (x | (x << 1)) & 0x55555555;
        y = (y | (y << 8)) & 0x00FF00FF; y = (y | (y << 4)) & 0x0F0F0F0F; y = (y | (y << 2)) & 0x33333333; y = (y | (y << 1)) & 0x55555555;
        return x | (y << 1);
    }

    static Node GetLeftmost(Node start)
    {
        Node p = start, left = start;
        do { if (p.x < left.x || (p.x == left.x && p.y < left.y)) left = p; p = p.next; } while (p != start);
        return left;
    }

    static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
        (cx - px) * (ay - py) >= (ax - px) * (cy - py) && (ax - px) * (by - py) >= (bx - px) * (ay - py) && (bx - px) * (cy - py) >= (cx - px) * (by - py);

    static bool IsValidDiagonal(Node a, Node b) =>
        a.next.i != b.i && a.prev.i != b.i && !IntersectsPolygon(a, b) &&
        ((LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) && (Area(a.prev, a, b.prev) != 0 || Area(a, b.prev, b) != 0)) ||
         (Equals(a, b) && Area(a.prev, a, a.next) > 0 && Area(b.prev, b, b.next) > 0));

    static double Area(Node p, Node q, Node r) => (q.y - p.y) * (r.x - q.x) - (q.x - p.x) * (r.y - q.y);
    static bool Equals(Node a, Node b) => a.x == b.x && a.y == b.y;

    static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        int o1 = Math.Sign(Area(p1, q1, p2)), o2 = Math.Sign(Area(p1, q1, q2)), o3 = Math.Sign(Area(p2, q2, p1)), o4 = Math.Sign(Area(p2, q2, q1));
        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
        if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
        if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
        if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
        return false;
    }
    static bool OnSegment(Node p, Node q, Node r) =>
        q.x <= Math.Max(p.x, r.x) && q.x >= Math.Min(p.x, r.x) && q.y <= Math.Max(p.y, r.y) && q.y >= Math.Min(p.y, r.y);

    static bool IntersectsPolygon(Node a, Node b)
    {
        var p = a;
        do
        {
            if (p.i != a.i && p.next.i != a.i && p.i != b.i && p.next.i != b.i && Intersects(p, p.next, a, b)) return true;
            p = p.next;
        } while (p != a);
        return false;
    }

    static bool LocallyInside(Node a, Node b) => Area(a.prev, a, a.next) < 0
        ? Area(a, b, a.next) >= 0 && Area(a, a.prev, b) >= 0
        : Area(a, b, a.prev) < 0 || Area(a, a.next, b) < 0;

    static bool MiddleInside(Node a, Node b)
    {
        var p = a; bool inside = false; double px = (a.x + b.x) / 2, py = (a.y + b.y) / 2;
        do
        {
            if (((p.y > py) != (p.next.y > py)) && p.next.y != p.y && (px < (p.next.x - p.x) * (py - p.y) / (p.next.y - p.y) + p.x))
                inside = !inside;
            p = p.next;
        } while (p != a);
        return inside;
    }

    static Node SplitPolygon(Node a, Node b)
    {
        var a2 = new Node(a.i, a.x, a.y); var b2 = new Node(b.i, b.x, b.y);
        Node an = a.next, bp = b.prev;
        a.next = b; b.prev = a; a2.next = an; an.prev = a2; b2.next = a2; a2.prev = b2; bp.next = b2; b2.prev = bp;
        return b2;
    }

    static Node InsertNode(int i, double x, double y, Node last)
    {
        var p = new Node(i, x, y);
        if (last == null) { p.prev = p; p.next = p; }
        else { p.next = last.next; p.prev = last; last.next.prev = p; last.next = p; }
        return p;
    }

    static void RemoveNode(Node p)
    {
        p.next.prev = p.prev; p.prev.next = p.next;
        if (p.prevZ != null) p.prevZ.nextZ = p.nextZ;
        if (p.nextZ != null) p.nextZ.prevZ = p.prevZ;
    }

    static double SignedArea(double[] data, int start, int end)
    {
        double sum = 0;
        for (int k = start, j = end - 2; k < end; k += 2) { sum += (data[j] - data[k]) * (data[k + 1] + data[j + 1]); j = k; }
        return sum;
    }
}

// ---------------------------------------------------------------------------------------------------- repeat folding
public static partial class Lacework
{
    public sealed class FoldInfo
    {
        public double P, PxY; public int Nb, H, Repeats; public double[,] A; public bool[,] Module, Tiled;
        public bool Symmetric; public double MirrorCorr, SplitCorr; public int RailTopRows, RailBotRows;
        public double RailTopLo, RailTopHi, RailBotLo, RailBotHi, Threshold;
        public List<(double y0, double y1, int shift)> Bays = new();
    }

    static double Frac(double v) => v - Math.Floor(v);

    static FoldInfo Fold(List<double[]> band, List<(double y, double w)> posts, bool[] postCol, int W, int H, double px,
        double y0, double z0, Opts o, Action<string> L, string prefix)
    {
        // bays (column runs between posts) with their points, y-extent clipped to P0.5..P99.5 of the points
        var bays = new List<(double ya, double yb, List<double[]> pts)>();
        {
            int s = -1;
            for (int i = 0; i <= W; i++)
            {
                bool on = i < W && !postCol[i];
                if (on && s < 0) s = i;
                if (!on && s >= 0)
                {
                    double ya = y0 + s * px, yb = y0 + i * px;
                    var bp = band.Where(p => p[1] >= ya && p[1] < yb).ToList();
                    if (bp.Count > 200)
                    {
                        var ys = bp.Select(p => p[1]).OrderBy(v => v).ToList();
                        double a = Pct(ys, 0.005), b = Pct(ys, 0.995);
                        if (b - a > 0.3) bays.Add((a, b, bp.Where(p => p[1] >= a && p[1] <= b).ToList()));
                    }
                    s = -1;
                }
            }
        }
        if (bays.Count == 0) return null;

        // 1. autocorrelation along y on an 8 mm raster, pooled over bays
        const double cp = 0.008;
        int Hc = (int)Math.Ceiling(H * px / cp);
        double maxLen = bays.Max(b => b.yb - b.ya);
        int Lmin = (int)(0.10 / cp), Lmax = (int)(Math.Min(0.75, maxLen / 2) / cp);
        var num = new double[Lmax + 1]; var dA = new double[Lmax + 1]; var dB = new double[Lmax + 1];
        foreach (var b in bays)
        {
            int Wc = (int)Math.Ceiling((b.yb - b.ya) / cp) + 1;
            var c = new double[Wc, Hc];
            foreach (var p in b.pts) { int i = (int)((p[1] - b.ya) / cp), j = Math.Min(Hc - 1, (int)((p[2] - z0) / cp)); if (j >= 0) c[i, j]++; }
            var rm = new double[Hc]; for (int j = 0; j < Hc; j++) { for (int i = 0; i < Wc; i++) rm[j] += c[i, j]; rm[j] /= Wc; }   // remove row means (vertical profile)
            for (int L2 = Lmin; L2 <= Lmax && L2 < Wc; L2++)
                for (int i = 0; i + L2 < Wc; i++) for (int j = 0; j < Hc; j++)
                {
                    double u = c[i, j] - rm[j], w = c[i + L2, j] - rm[j];
                    num[L2] += u * w; dA[L2] += u * u; dB[L2] += w * w;
                }
        }
        var r = new double[Lmax + 1];
        for (int L2 = Lmin; L2 <= Lmax; L2++) r[L2] = dA[L2] > 0 && dB[L2] > 0 ? num[L2] / Math.Sqrt(dA[L2] * dB[L2]) : 0;
        var peaks = new List<int>();
        for (int L2 = Lmin + 1; L2 < Lmax; L2++) if (r[L2] > r[L2 - 1] && r[L2] >= r[L2 + 1] && r[L2] > 0.02) peaks.Add(L2);
        L($"  repeat search: autocorrelation peaks (lag mm : r) {string.Join("  ", peaks.OrderByDescending(k => r[k]).Take(8).OrderBy(k => k).Select(k => $"{k * cp * 1000:F0}:{r[k]:F3}"))}");
        double P0;
        if (o.Period is double gp) P0 = gp;
        else
        {
            if (peaks.Count == 0) return null;
            double best = peaks.Max(k => r[k]);
            if (best < 0.04) { L("  autocorrelation too weak for a repeat"); return null; }
            int fund = peaks.Where(k => r[k] >= 0.6 * best).Min();
            P0 = fund * cp;
        }

        // 2. refine P by folded-module contrast (phase-free per bay), 0.5 mm steps over +-5 %
        int Hm = H;
        double Contrast(double P)
        {
            int nb = Math.Max(8, (int)Math.Round(P / px)); double s = 0, norm = 0;
            foreach (var b in bays)
            {
                var M = new double[nb, Hm]; int n = 0;
                foreach (var p in b.pts) { int j = (int)((p[2] - z0) / px); if (j < 0 || j >= Hm) continue; M[(int)(Frac(p[1] / P) * nb) % nb, j]++; n++; }
                double m = (double)n / (nb * Hm); var rmm = new double[Hm]; for (int j = 0; j < Hm; j++) { for (int i = 0; i < nb; i++) rmm[j] += M[i, j]; rmm[j] /= nb; }
                for (int i = 0; i < nb; i++) for (int j = 0; j < Hm; j++) s += (M[i, j] - rmm[j]) * (M[i, j] - rmm[j]) - M[i, j];
                norm += m * m * nb * Hm;
            }
            return s / Math.Max(1e-9, norm);
        }
        double Pf = P0, cBest = Contrast(P0);
        if (o.Period is null)
            for (double P = P0 * 0.95; P <= P0 * 1.05; P += 0.0005) { double c = Contrast(P); if (c > cBest) { cBest = c; Pf = P; } }
        int Nb = Math.Max(8, (int)Math.Round(Pf / px));
        L($"  period: autocorr {P0 * 1000:F0} mm -> refined {Pf * 1000:F1} mm ({Nb} columns of {Pf / Nb * 1000:F2} mm); fold contrast {cBest:F3} (P/2: {Contrast(Pf * 0.5):F3}, 2P: {Contrast(Pf * 2):F3})");

        // 3. fold each bay (absolute phase), align bays by circular cross-correlation, sum
        double[,] FoldPts(IEnumerable<double[]> pts)
        {
            var M = new double[Nb, Hm];
            foreach (var p in pts) { int j = (int)((p[2] - z0) / px); if (j < 0 || j >= Hm) continue; M[(int)(Frac(p[1] / Pf) * Nb) % Nb, j]++; }
            return M;
        }
        int BestShift(double[,] Ref, double[,] M, bool mirror, out double corr)
        {
            double mr = 0, mm = 0; foreach (var v in Ref) mr += v; foreach (var v in M) mm += v; mr /= Ref.Length; mm /= M.Length;
            int bs = 0; corr = double.MinValue;
            for (int s = 0; s < Nb; s++)
            {
                double sxy = 0, sxx = 0, syy = 0;
                for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++)
                {
                    int k = mirror ? ((s - i) % Nb + Nb) % Nb : (i + s) % Nb;
                    double a = Ref[i, j] - mr, b = M[k, j] - mm; sxy += a * b; sxx += a * a; syy += b * b;
                }
                double c = sxy / Math.Sqrt(Math.Max(1e-12, sxx * syy));
                if (c > corr) { corr = c; bs = s; }
            }
            return bs;
        }
        var fi = new FoldInfo { P = Pf, Nb = Nb, H = Hm, PxY = Pf / Nb };
        var mods = bays.Select(b => FoldPts(b.pts)).ToList();
        int refB = Enumerable.Range(0, bays.Count).OrderByDescending(k => bays[k].pts.Count).First();
        var A = new double[Nb, Hm];
        for (int k = 0; k < bays.Count; k++)
        {
            double cc = 1;
            int sh = k == refB ? 0 : BestShift(mods[refB], mods[k], false, out cc);
            L($"    bay y {bays[k].ya:F3}..{bays[k].yb:F3}: {(bays[k].yb - bays[k].ya) / Pf:F1} repeats{(k == refB ? " (reference)" : $", aligned with shift {sh * fi.PxY * 1000:F0} mm, r {cc:F2}")}");
            for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) A[i, j] += mods[k][(i + sh) % Nb, j];
            fi.Bays.Add((bays[k].ya, bays[k].yb, sh));
            fi.Repeats += (int)Math.Floor((bays[k].yb - bays[k].ya) / Pf);
        }
        SaveModule(prefix + "-module.txt", Pf, A);
        // optional: merge another property's module (same period) - aligned by best circular shift
        if (o.Merge != null && File.Exists(o.Merge))
        {
            var (Pm, Am) = LoadModule(o.Merge);
            if (Math.Abs(Pm - Pf) / Pf < 0.03)
            {
                var Amh = new double[Nb, Hm];   // resample other module onto this column count, align rows by top
                int nbo = Am.GetLength(0), hmo = Am.GetLength(1);
                for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) { int jo = hmo - Hm + j; if (jo >= 0 && jo < hmo) Amh[i, j] = Am[(int)((i + 0.5) / Nb * nbo) % nbo, jo]; }
                int sh = BestShift(A, Amh, false, out double cc);
                L($"  merge {Path.GetFileName(o.Merge)} (P {Pm * 1000:F1} mm): best r {cc:F2} at shift {sh * fi.PxY * 1000:F0} mm{(cc > 0.3 ? " -> merged" : " -> too different, not merged")}");
                if (cc > 0.3)
                {
                    double sa = 0, sb = 0; foreach (var v in A) sa += v; foreach (var v in Amh) sb += v;
                    for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) A[i, j] += Amh[(i + sh) % Nb, j] * sa / Math.Max(1e-9, sb);
                }
            }
            else L($"  NOT merging {Path.GetFileName(o.Merge)}: period {Pm * 1000:F1} mm vs {Pf * 1000:F1} mm");
        }

        // split-half reliability (even vs odd repeats) and mirror symmetry
        var E = new double[Nb, Hm]; var Od = new double[Nb, Hm];
        for (int k = 0; k < bays.Count; k++)
        {
            int sh = fi.Bays[k].shift;
            var me = FoldPts(bays[k].pts.Where(p => ((long)Math.Floor(p[1] / Pf)) % 2 == 0));
            var mo = FoldPts(bays[k].pts.Where(p => ((long)Math.Floor(p[1] / Pf)) % 2 != 0));
            for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) { E[i, j] += me[(i + sh) % Nb, j]; Od[i, j] += mo[(i + sh) % Nb, j]; }
        }
        Corr0(E, Od, out double split);
        BestShift(E, Od, true, out double mirSplit);
        int ms = BestShift(A, A, true, out double mir);
        fi.SplitCorr = split; fi.MirrorCorr = mirSplit;
        fi.Symmetric = split > 0.15 && mirSplit >= 0.85 * split;
        int npts = bays.Sum(b => b.pts.Count);
        L($"  folded {fi.Repeats} whole repeats over {bays.Count} bay(s): {npts} points, {npts / (double)(Nb * Hm):F1} per 4 mm module pixel");
        L($"  split-half (even vs odd repeats) r = {split:F2}; mirror (even vs mirrored odd) r = {mirSplit:F2} -> {(fi.Symmetric ? "left/right SYMMETRIC (symmetrised)" : "not symmetric")}");
        if (fi.Symmetric)
        {
            var S = new double[Nb, Hm];
            for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) S[i, j] = 0.5 * (A[i, j] + A[((ms - i) % Nb + Nb) % Nb, j]);
            A = S;
        }
        // light 3x3 smoothing (circular in i)
        var Sm = new double[Nb, Hm];
        for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++)
        {
            double s = 0, wsum = 0;
            for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
            {
                int jj = j + dj; if (jj < 0 || jj >= Hm) continue;
                double w = (di == 0 ? 2 : 1) * (dj == 0 ? 2 : 1);
                s += w * A[((i + di) % Nb + Nb) % Nb, jj]; wsum += w;
            }
            Sm[i, j] = s / wsum;
        }
        fi.A = Sm;

        // 4. threshold (Otsu), close (circular), rails
        double thr = o.FoldThr is double ft ? ft * Sm.Cast<double>().Max() : Otsu(Sm);
        fi.Threshold = thr;
        var Mk = new bool[Nb, Hm];
        for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) Mk[i, j] = Sm[i, j] >= thr;
        var T3 = new bool[3 * Nb, Hm];
        for (int t = 0; t < 3; t++) for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) T3[t * Nb + i, j] = Mk[i, j];
        for (int k = 0; k < o.Close; k++) T3 = Dilate(T3);
        for (int k = 0; k < o.Close; k++) T3 = Erode(T3, keepBorder: true);
        for (int i = 0; i < Nb; i++) for (int j = 0; j < Hm; j++) Mk[i, j] = T3[Nb + i, j];
        var rowf = new double[Hm];
        for (int j = 0; j < Hm; j++) { int n = 0; for (int i = 0; i < Nb; i++) if (Mk[i, j]) n++; rowf[j] = (double)n / Nb; }
        int top = Hm - 1; while (top > 0 && rowf[top] < 0.5) top--;
        int rt = top; while (rt >= 0 && rowf[rt] >= 0.75) rt--;
        int bot = 0; while (bot < Hm - 1 && rowf[bot] < 0.5) bot++;
        int rb = bot; while (rb < Hm && rowf[rb] >= 0.75) rb++;
        fi.RailTopRows = top - rt; fi.RailBotRows = rb - bot;
        if (fi.RailTopRows > 0 && fi.RailTopRows * px <= 0.08) { for (int j = rt + 1; j <= top; j++) for (int i = 0; i < Nb; i++) Mk[i, j] = true; fi.RailTopLo = z0 + (rt + 1) * px; fi.RailTopHi = z0 + (top + 1) * px; }
        else fi.RailTopRows = 0;
        if (fi.RailBotRows > 0 && fi.RailBotRows * px <= 0.08) { for (int j = bot; j < rb; j++) for (int i = 0; i < Nb; i++) Mk[i, j] = true; fi.RailBotLo = z0 + bot * px; fi.RailBotHi = z0 + rb * px; }
        else fi.RailBotRows = 0;
        L($"  module threshold {thr:F2} ({(o.FoldThr is null ? "Otsu" : "given fraction")} on smoothed folded counts, max {Sm.Cast<double>().Max():F1}); module fill {Mk.Cast<bool>().Count(b => b) / (double)Mk.Length:P0}");
        L($"  rails (rows >= 75 % solid, <= 80 mm): top {(fi.RailTopRows > 0 ? $"RL {fi.RailTopLo:F3}..{fi.RailTopHi:F3}" : "none")}; bottom {(fi.RailBotRows > 0 ? $"RL {fi.RailBotLo:F3}..{fi.RailBotHi:F3}" : "none")}");
        L($"  module row fill (top->bottom, 8 mm): {string.Join(" ", Enumerable.Range(0, Hm).Reverse().Where(j => j % 2 == 0).Select(j => ((int)(rowf[j] * 9.99)).ToString()))}");
        fi.Module = Mk;

        // 5. tile back along each bay (clipped at the bay's iron extent)
        var Tl = new bool[W, H];
        foreach (var (ya, yb, sh) in fi.Bays)
            for (int i = Math.Max(0, (int)((ya - y0) / px)); i < Math.Min(W, (int)Math.Ceiling((yb - y0) / px)); i++)
            {
                if (postCol[i]) continue;
                double y = y0 + (i + 0.5) * px;
                int k = (int)(Frac(y / Pf) * Nb) % Nb, m = ((k - sh) % Nb + Nb) % Nb;
                for (int j = 0; j < H; j++) Tl[i, j] = Mk[m, j];
            }
        fi.Tiled = Tl;
        SaveModulePng(prefix + "-module.png", fi, r, cp, Lmin, Lmax, z0, px);
        return fi;
    }

    static void Corr0(double[,] a, double[,] b, out double c)
    {
        double ma = 0, mb = 0; foreach (var v in a) ma += v; foreach (var v in b) mb += v; ma /= a.Length; mb /= b.Length;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < a.GetLength(0); i++) for (int j = 0; j < a.GetLength(1); j++)
        { double x = a[i, j] - ma, y = b[i, j] - mb; sxy += x * y; sxx += x * x; syy += y * y; }
        c = sxy / Math.Sqrt(Math.Max(1e-12, sxx * syy));
    }

    static double Otsu(double[,] a)
    {
        var v = a.Cast<double>().ToArray(); double lo = v.Min(), hi = v.Max(); if (hi <= lo) return hi;
        const int nbins = 128; var h = new double[nbins];
        foreach (var x in v) h[Math.Min(nbins - 1, (int)((x - lo) / (hi - lo) * nbins))]++;
        double total = v.Length, sumAll = 0; for (int k = 0; k < nbins; k++) sumAll += k * h[k];
        double wB = 0, sumB = 0, best = -1; int bk = 0;
        for (int k = 0; k < nbins; k++)
        {
            wB += h[k]; if (wB == 0) continue; double wF = total - wB; if (wF == 0) break;
            sumB += k * h[k]; double mB = sumB / wB, mF = (sumAll - sumB) / wF, between = wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; bk = k; }
        }
        return lo + (bk + 1) * (hi - lo) / nbins;
    }

    static void SaveModule(string path, double P, double[,] A)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{P.ToString("R", Inv)} {A.GetLength(0)} {A.GetLength(1)}");
        for (int j = 0; j < A.GetLength(1); j++) sb.AppendLine(string.Join(" ", Enumerable.Range(0, A.GetLength(0)).Select(i => A[i, j].ToString("0.###", Inv))));
        File.WriteAllText(path, sb.ToString());
    }
    static (double P, double[,] A) LoadModule(string path)
    {
        var lines = File.ReadAllLines(path); var h = lines[0].Split(' ');
        double P = double.Parse(h[0], Inv); int nb = int.Parse(h[1]), hm = int.Parse(h[2]);
        var A = new double[nb, hm];
        for (int j = 0; j < hm; j++) { var s = lines[1 + j].Split(' '); for (int i = 0; i < nb; i++) A[i, j] = double.Parse(s[i], Inv); }
        return (P, A);
    }

    // Module sheet: folded density (3 repeats), thresholded module (3 repeats), autocorrelation curve. Seen from the street.
    static void SaveModulePng(string png, FoldInfo f, double[] r, double cp, int Lmin, int Lmax, double z0, double px)
    {
        int s = 4, reps = 3, Wm = f.Nb * reps * s, Hm = f.H * s, plotH = 140, pad = 24;
        int IW = Math.Max(Wm + 20, 700), IH = 2 * (Hm + pad) + plotH + 2 * pad + 10;
        using var bmp = new Bitmap(IW, IH); using var g = Graphics.FromImage(bmp); g.Clear(Color.White);
        using var font = new Font("Arial", 9);
        double mx = 0; foreach (var v in f.A) mx = Math.Max(mx, v);
        for (int t = 0; t < reps; t++) for (int i = 0; i < f.Nb; i++) for (int j = 0; j < f.H; j++)
        {
            int X = 10 + (reps * f.Nb - 1 - (t * f.Nb + i)) * s;
            int k = (int)(255 - 255 * Math.Min(1, f.A[i, j] / Math.Max(1e-9, mx)));
            using var b1 = new SolidBrush(Color.FromArgb(k, k, k));
            g.FillRectangle(b1, X, pad + (f.H - 1 - j) * s, s, s);
            if (f.Module[i, j]) g.FillRectangle(Brushes.Black, X, 2 * pad + Hm + (f.H - 1 - j) * s, s, s);
        }
        using var pen = new Pen(Color.Red);
        for (int t = 0; t <= reps; t++) { int X = 10 + t * f.Nb * s; g.DrawLine(pen, X, pad, X, pad + Hm); g.DrawLine(pen, X, 2 * pad + Hm, X, 2 * pad + 2 * Hm); }
        g.DrawString($"folded density x3, period {f.P * 1000:F1} mm, {f.Repeats} repeats folded, RL {z0:F3}..{z0 + f.H * px:F3} (seen from street)", font, Brushes.DarkRed, 10, 4);
        g.DrawString($"traced module (threshold {f.Threshold:F1}{(f.Symmetric ? ", symmetrised" : "")}); split-half r {f.SplitCorr:F2}, mirror r {f.MirrorCorr:F2}", font, Brushes.DarkRed, 10, pad + Hm + 4);
        int oy = 2 * Hm + 3 * pad + 10;
        g.DrawString($"autocorrelation along y, lag {Lmin * cp * 1000:F0}..{Lmax * cp * 1000:F0} mm (red = chosen period)", font, Brushes.DarkRed, 10, oy - 18);
        double rmax = Math.Max(0.05, r.Skip(Lmin).Max());
        var ptsP = new List<PointF>();
        for (int L2 = Lmin; L2 <= Lmax; L2++) ptsP.Add(new PointF(10 + (float)((L2 - Lmin) / (double)Math.Max(1, Lmax - Lmin) * (IW - 30)), oy + plotH - (float)(Math.Max(0, r[L2]) / rmax * plotH)));
        if (ptsP.Count > 1) g.DrawLines(Pens.Blue, ptsP.ToArray());
        float xp = 10 + (float)((f.P / cp - Lmin) / Math.Max(1, Lmax - Lmin) * (IW - 30));
        g.DrawLine(pen, xp, oy, xp, oy + plotH);
        g.DrawLine(Pens.Gray, 10, oy + plotH, IW - 20, oy + plotH);
        bmp.Save(png, ImageFormat.Png);
    }
}
