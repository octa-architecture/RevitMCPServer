using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;

// Generates a representative Victorian cast-iron lacework frieze module as profile extrusions for
// create_model_family (loops in mm, x about the module centre, z up from the bottom rail).
// Motifs: diagonal lattice ground, central petalled medallion, four C/S scrolls, fleur-de-lis,
// bud, and a pendant fringe under the bottom rail. Writes the spec "extrusions" array + a preview.
public static class LaceGen
{
    const double W = 450, H = 400, Rail = 20, TopRail = 25, Edge = 10;
    static readonly List<List<List<double[]>>> Shapes = new();   // each shape: outer loop + holes

    static List<double[]> Circle(double cx, double cz, double r, int n = 24) =>
        Enumerable.Range(0, n).Select(i => { var a = 2 * Math.PI * i / n; return new[] { cx + r * Math.Cos(a), cz + r * Math.Sin(a) }; }).ToList();

    static List<double[]> Ellipse(double cx, double cz, double a, double b, double rot, int n = 18) =>
        Enumerable.Range(0, n).Select(i =>
        {
            var t = 2 * Math.PI * i / n; double x = a * Math.Cos(t), z = b * Math.Sin(t);
            return new[] { cx + x * Math.Cos(rot) - z * Math.Sin(rot), cz + x * Math.Sin(rot) + z * Math.Cos(rot) };
        }).ToList();

    // Thick polyline (ribbon) as a closed loop.
    static List<double[]> Ribbon(List<double[]> cl, double w)
    {
        var l = new List<double[]>(); var r = new List<double[]>();
        for (int i = 0; i < cl.Count; i++)
        {
            var a = cl[Math.Max(0, i - 1)]; var b = cl[Math.Min(cl.Count - 1, i + 1)];
            double dx = b[0] - a[0], dz = b[1] - a[1], len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-9) continue;
            double nx = -dz / len * w / 2, nz = dx / len * w / 2;
            l.Add(new[] { cl[i][0] + nx, cl[i][1] + nz }); r.Add(new[] { cl[i][0] - nx, cl[i][1] - nz });
        }
        r.Reverse(); l.AddRange(r); return l;
    }

    static void Solid(List<double[]> loop) => Shapes.Add(new() { loop });
    static void Ring(double cx, double cz, double ro, double ri, int n = 28) => Shapes.Add(new() { Circle(cx, cz, ro, n), Circle(cx, cz, ri, n) });
    static void Bar(double x1, double z1, double x2, double z2, double w) => Solid(Ribbon(new() { new[] { x1, z1 }, new[] { x2, z2 } }, w));

    static List<double[]> Spiral(double cx, double cz, double r0, double r1, double turns, int dir, double phase)
    {
        int n = (int)(40 * turns); var pts = new List<double[]>();
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n, ang = phase + dir * 2 * Math.PI * turns * t, r = r0 + (r1 - r0) * t;
            pts.Add(new[] { cx + r * Math.Cos(ang), cz + r * Math.Sin(ang) });
        }
        return pts;
    }

    public static string Run(string outJson, string outPng)
    {
        Shapes.Clear();
        double fx0 = -W / 2 + Edge, fx1 = W / 2 - Edge, fz0 = Rail, fz1 = H - TopRail, cz = (fz0 + fz1) / 2;
        // Lattice ground: 45Â° bars every 45 mm, clipped to the field.
        for (double k = -W; k <= W; k += 45)
            foreach (var dir in new[] { 1, -1 })
            {
                var seg = Clip(k, cz, 1, dir, fx0, fx1, fz0, fz1);
                if (seg is null) continue;
                // keep the medallion open: cut each lattice bar where it passes inside the ring
                foreach (var piece in OutsideCircle(seg, 0, cz, 85)) Bar(piece[0], piece[1], piece[2], piece[3], 5);
            }
        // Central medallion.
        Ring(0, cz, 92, 78, 40); Ring(0, cz, 40, 32, 28); Solid(Circle(0, cz, 16));
        for (int i = 0; i < 8; i++) { var a = Math.PI * i / 4; Solid(Ellipse(59 * Math.Cos(a), cz + 59 * Math.Sin(a), 17, 8, a)); }
        // Four scrolls, one per quadrant, tails running into the medallion ring.
        foreach (var sx in new[] { -1, 1 })
            foreach (var sz in new[] { -1, 1 })
            {
                double sc = sx * 148, szc = cz + sz * 95;
                int dir = sx * sz;
                // one full turn so the outer end points at the medallion, then a short straight tail
                var toMed = Math.Atan2((cz + sz * 62) - szc, sx * 80 - sc);
                var sp = Spiral(sc, szc, 8, 44, 1.0, dir, toMed);
                sp.Add(new[] { sx * 80, cz + sz * 62 });
                Solid(Ribbon(sp, 9));
                Solid(Circle(sc, szc, 7, 16));
                // acanthus leaf off each scroll
                Solid(Ellipse(sx * 195, szc - sz * 40, 20, 7, sz * sx * Math.PI / 3));
            }
        // Fleur-de-lis under the top rail; bud over the bottom rail.
        Solid(Ellipse(0, fz1 - 33, 30, 10, Math.PI / 2)); Solid(Ellipse(-21, fz1 - 41, 22, 8, Math.PI / 3)); Solid(Ellipse(21, fz1 - 41, 22, 8, 2 * Math.PI / 3));
        Bar(-27, fz1 - 57, 27, fz1 - 57, 8); Bar(0, fz1 - 3, 0, fz1 - 57, 6);
        Solid(Ellipse(0, fz0 + 32, 26, 9, Math.PI / 2)); Bar(-18, fz0 + 52, 18, fz0 + 52, 7); Bar(0, fz0, 0, fz0 + 8, 6);
        // Pendant fringe below the bottom rail.
        for (double x = -W / 2 + 22.5; x < W / 2; x += 45) { Bar(x, 2, x, -18, 5); Solid(Ellipse(x, -30, 13, 6, Math.PI / 2, 14)); }

        // Spec JSON.
        var arr = new JsonArray();
        foreach (var s in Shapes)
            arr.Add(new JsonObject
            {
                ["y"] = new JsonArray("PI", "PO"), ["material"] = "Lacework Material",
                ["loops"] = new JsonArray(s.Select(loop => (JsonNode)new JsonArray(loop.Select(p => (JsonNode)new JsonArray(Math.Round(p[0], 2), Math.Round(p[1], 2))).ToArray())).ToArray()),
            });
        System.IO.File.WriteAllText(outJson, arr.ToJsonString());

        // Preview.
        using var bmp = new Bitmap(760, 760); using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var iron = new SolidBrush(Color.FromArgb(55, 48, 42));
        PointF P(double x, double z) => new((float)(380 + x * 1.5), (float)(660 - z * 1.5));
        void Rect(double a, double b, double c, double d) => g.FillPolygon(iron, new[] { P(a, b), P(c, b), P(c, d), P(a, d) });
        Rect(-W / 2, 0, W / 2, Rail); Rect(-W / 2, H - TopRail, W / 2, H); Rect(-W / 2, Rail, fx0, fx1 > 0 ? H - TopRail : 0); Rect(fx1, Rail, W / 2, H - TopRail);
        foreach (var s in Shapes)
            for (int i = 0; i < s.Count; i++)
                g.FillPolygon(i == 0 ? iron : Brushes.White, s[i].Select(p => P(p[0], p[1])).ToArray());
        bmp.Save(outPng);
        return $"{Shapes.Count} shapes";
    }

    static IEnumerable<double[]> OutsideCircle(double[] s, double cx, double cz, double r)
    {
        double dx = s[2] - s[0], dz = s[3] - s[1], fx = s[0] - cx, fz = s[1] - cz;
        double a = dx * dx + dz * dz, b = 2 * (fx * dx + fz * dz), c = fx * fx + fz * fz - r * r, disc = b * b - 4 * a * c;
        if (disc <= 0) { yield return s; yield break; }
        double t1 = (-b - Math.Sqrt(disc)) / (2 * a), t2 = (-b + Math.Sqrt(disc)) / (2 * a);
        if (t1 > 0.02) yield return new[] { s[0], s[1], s[0] + dx * Math.Min(t1, 1), s[1] + dz * Math.Min(t1, 1) };
        if (t2 < 0.98) yield return new[] { s[0] + dx * Math.Max(t2, 0), s[1] + dz * Math.Max(t2, 0), s[2], s[3] };
    }

    static double[]? Clip(double px, double pz, double dx, double dz, double x0, double x1, double z0, double z1)
    {
        var ts = new List<double>();
        foreach (var x in new[] { x0, x1 }) { var t = (x - px) / dx; var z = pz + t * dz; if (z >= z0 - 1e-6 && z <= z1 + 1e-6) ts.Add(t); }
        foreach (var z in new[] { z0, z1 }) { var t = (z - pz) / dz; var x = px + t * dx; if (x >= x0 - 1e-6 && x <= x1 + 1e-6) ts.Add(t); }
        if (ts.Count < 2) return null;
        ts.Sort();
        double a = ts[0], b = ts[^1];
        if (b - a < 15) return null;
        return new[] { px + a * dx, pz + a * dz, px + b * dx, pz + b * dz };
    }
}

