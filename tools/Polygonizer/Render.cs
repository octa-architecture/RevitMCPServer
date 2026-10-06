using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

// Point-cloud elevation / plan / section renderer: CSV (x,y,z,r,g,b) -> PNG, nearest point wins.
// view: west|east|north|south (elevation looking that way's opposite: "west" = seen from the west,
// looking east), plan. Grid lines every metre, labelled with model coordinates, for measuring.
public static class Render
{
    public static string Run(string csv, string png, string view, double pxPerM, string mode)
    {
        var inv = CultureInfo.InvariantCulture;
        var pts = new List<(double h, double v, double d, int r, int g, int b)>();
        foreach (var line in File.ReadLines(csv))
        {
            var s = line.Split(',');
            if (s.Length < 6) continue;
            double x = double.Parse(s[0], inv), y = double.Parse(s[1], inv), z = double.Parse(s[2], inv);
            int r = int.Parse(s[3]), g = int.Parse(s[4]), b = int.Parse(s[5]);
            // h = screen right, v = screen up, d = distance from the viewer (smaller = nearer)
            var p = view switch
            {
                "west" => (-y, z, x),    // standing west looking east: north on the left
                "east" => (y, z, -x),
                "north" => (x, z, -y),   // standing north looking south: west on the left... east on right? (x right)
                "south" => (-x, z, y),
                _ => (x, y, -z),         // plan from above
            };
            pts.Add((p.Item1, p.Item2, p.Item3, r, g, b));
        }
        if (pts.Count == 0) return "no points";
        double h0 = pts.Min(q => q.h), h1 = pts.Max(q => q.h), v0 = pts.Min(q => q.v), v1 = pts.Max(q => q.v);
        double d0 = pts.Min(q => q.d), d1 = pts.Max(q => q.d);
        int W = (int)((h1 - h0) * pxPerM) + 80, H = (int)((v1 - v0) * pxPerM) + 60;
        using var bmp = new Bitmap(W, H);
        using var gr = Graphics.FromImage(bmp);
        gr.Clear(Color.White);
        var depth = new double[W, H];
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++) depth[i, j] = double.MaxValue;
        foreach (var q in pts)
        {
            int px = 60 + (int)((q.h - h0) * pxPerM), py = H - 30 - (int)((q.v - v0) * pxPerM);
            if (px < 0 || py < 0 || px >= W || py >= H || q.d >= depth[px, py]) continue;
            depth[px, py] = q.d;
            Color c;
            if (mode == "depth")
            {
                int k = (int)(40 + 190 * (q.d - d0) / Math.Max(1e-6, d1 - d0));
                c = Color.FromArgb(k, k, k);
            }
            else c = Color.FromArgb(q.r, q.g, q.b);
            bmp.SetPixel(px, py, c);
        }
        using var pen = new Pen(Color.FromArgb(70, 255, 0, 0), 1);
        using var font = new Font("Arial", 9);
        // Grid: every 0.5 m (labels every metre). h axis label = model coordinate along the facade.
        for (double h = Math.Ceiling(h0 * 2) / 2; h <= h1; h += 0.5)
        {
            int px = 60 + (int)((h - h0) * pxPerM);
            gr.DrawLine(pen, px, 0, px, H - 30);
            if (Math.Abs(h - Math.Round(h)) < 1e-6) gr.DrawString(((view is "west" or "south") ? -h : h).ToString("0", inv), font, Brushes.Red, px - 6, H - 28);
        }
        for (double v = Math.Ceiling(v0 * 2) / 2; v <= v1; v += 0.5)
        {
            int py = H - 30 - (int)((v - v0) * pxPerM);
            gr.DrawLine(pen, 60, py, W, py);
            if (Math.Abs(v * 2 - Math.Round(v * 2)) < 1e-6) gr.DrawString(v.ToString("0.0", inv), font, Brushes.Red, 2, py - 7);
        }
        bmp.Save(png, ImageFormat.Png);
        return $"{pts.Count} points -> {W}x{H}px";
    }
}
