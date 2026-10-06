// SurveyPdfReader: extracts words, merged text lines, vector segments, surveyed levels (RLs)
// and E/N coordinates from survey PDFs. Coordinates are PDF points, origin bottom-left.
// Usage: SurveyPdfReader <input.pdf> [--page N] [--out out.json]
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Graphics.Colors;

if (args.Length == 0 || args[0].StartsWith("-"))
{
    Console.Error.WriteLine("Usage: SurveyPdfReader <input.pdf> [--page N] [--out out.json]");
    return 1;
}
string input = args[0];
int? onlyPage = null;
string? outPath = null;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--page") onlyPage = int.Parse(args[++i]);
    else if (args[i] == "--out") outPath = args[++i];
}

var result = new Result { File = Path.GetFileName(input) };
using (var doc = PdfDocument.Open(input))
{
    for (int p = 1; p <= doc.NumberOfPages; p++)
    {
        if (onlyPage.HasValue && p != onlyPage) continue;
        var page = doc.GetPage(p);
        var pr = Extract.Page(page);
        result.Pages.Add(pr);
        Survey.Parse(pr, result);
    }
}

var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});
if (outPath != null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllText(outPath, json, new UTF8Encoding(false));
}
else Console.Out.Write(json);

foreach (var pg in result.Pages)
    Console.Error.WriteLine($"page {pg.Page}: {pg.Words.Count} words, {pg.Lines.Count} lines, {pg.Segments.Count} segments, " +
        $"{pg.Diagnostics.Letters} letters, {pg.Diagnostics.Paths} paths, {pg.Diagnostics.Annotations} annotations" +
        (pg.Diagnostics.Note != null ? $" | {pg.Diagnostics.Note}" : ""));
Console.Error.WriteLine($"levels: {result.Levels.Count}, coordinates: {result.Coordinates.Count}, distances: {result.Distances.Count}, bearings: {result.Bearings.Count}");
return 0;

// ---------------------------------------------------------------- model

class Result
{
    public string File { get; set; } = "";
    public List<PageResult> Pages { get; } = new();
    public List<Level> Levels { get; } = new();
    public List<Coordinate> Coordinates { get; } = new();
    public List<TextValue> Bearings { get; } = new();
    public List<TextValue> Distances { get; } = new();
    public bool MillimetreLevels { get; set; }
}

class PageResult
{
    public int Page { get; set; }
    public double PageWidth { get; set; }
    public double PageHeight { get; set; }
    public Diagnostics Diagnostics { get; set; } = new();
    public List<Word> Words { get; } = new();
    public List<Line> Lines { get; } = new();
    public List<Segment> Segments { get; } = new();
}

class Diagnostics
{
    public int Letters { get; set; }
    public int Paths { get; set; }
    public int Annotations { get; set; }
    public List<string>? AnnotationTexts { get; set; }
    public int Subpaths { get; set; }
    public int TinySubpaths { get; set; }
    public Dictionary<string, int> SubpathSizeHistogram { get; } = new();
    public Dictionary<string, int> SmallSubpathKinds { get; } = new();   // subpaths < 6pt, by fill/stroke/curvature
    public List<double[]>? TextLikeClusters { get; set; }                 // only when no text layer: [x, y, w, h, subpaths]
    public string? Note { get; set; }
}

class Word
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double RotationDeg { get; set; }
    public double FontSize { get; set; }
    [JsonIgnore] public double EndX { get; set; }
    [JsonIgnore] public double EndY { get; set; }
}

class Line
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double RotationDeg { get; set; }
    public double FontSize { get; set; }
    [JsonIgnore] public List<Word> Words { get; } = new();
}

class Segment
{
    public double[] P { get; set; } = Array.Empty<double>();
    public string? Color { get; set; }
}

record Level(string Raw, double Value, string Label, double X, double Y, int Page, string Line);
record Coordinate(string Raw, double E, double N, double? Ahd, string Label, double X, double Y, int Page);
record TextValue(string Raw, double Value, double X, double Y, double RotationDeg, int Page);

// ---------------------------------------------------------------- extraction

static class Extract
{
    static double R(double v) => Math.Round(v, 2);

    static readonly NearestNeighbourWordExtractor WordExtractor = new(new NearestNeighbourWordExtractor.NearestNeighbourWordExtractorOptions
    {
        MaximumDistance = (a, b) => 0.12 * Math.Max(Math.Max(a.PointSize, b.PointSize), 1),
        GroupByOrientation = true,
    });

    public static PageResult Page(Page page)
    {
        var pr = new PageResult { Page = page.Number, PageWidth = R(page.Width), PageHeight = R(page.Height) };
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        pr.Diagnostics.Letters = letters.Count;

        // Words: NearestNeighbourWordExtractor handles arbitrary text orientation. CAD exports place
        // glyphs edge-to-edge (gap ~0) and spaces are ~0.28 em, so the default threshold (~0.2 x glyph
        // width, measured on rotated boxes) merges words; use a tight gap relative to point size.
        foreach (var w in WordExtractor.GetWords(letters))
        {
            var ls = w.Letters;
            if (ls.Count == 0 || string.IsNullOrWhiteSpace(w.Text)) continue;
            var first = ls[0];
            var last = ls[^1];
            // Direction from the first glyph's own baseline (robust for single-letter words too).
            double dx = first.EndBaseLine.X - first.StartBaseLine.X, dy = first.EndBaseLine.Y - first.StartBaseLine.Y;
            double rot = Math.Abs(dx) + Math.Abs(dy) < 1e-6 ? first.GlyphRectangle.Rotation : Math.Atan2(dy, dx) * 180 / Math.PI;
            double size = first.PointSize > 0 ? first.PointSize : first.GlyphRectangle.Height;
            pr.Words.Add(new Word
            {
                Text = w.Text, X = R(first.StartBaseLine.X), Y = R(first.StartBaseLine.Y),
                RotationDeg = Math.Round(NormDeg(rot), 1), FontSize = R(size),
                EndX = last.EndBaseLine.X, EndY = last.EndBaseLine.Y,
            });
        }
        pr.Lines.AddRange(MergeLines(pr.Words));

        // Vector segments from all non-clipping paths.
        var paths = page.ExperimentalAccess.Paths;
        pr.Diagnostics.Paths = paths.Count;
        var glyphBoxes = new List<PdfRectangle>();
        foreach (var path in paths)
        {
            if (path.IsClipping && !path.IsStroked && !path.IsFilled) continue;
            string? color = Hex(path.IsStroked ? path.StrokeColor : path.FillColor);
            foreach (var sub in path)
            {
                var bb = sub.GetBoundingRectangle();
                pr.Diagnostics.Subpaths++;
                if (bb.HasValue)
                {
                    double size = Math.Max(bb.Value.Width, bb.Value.Height);
                    var h = pr.Diagnostics.SubpathSizeHistogram;
                    string bin = size < 1 ? "<1pt" : size < 3 ? "1-3pt" : size < 6 ? "3-6pt" : size < 20 ? "6-20pt" : ">=20pt";
                    h[bin] = h.GetValueOrDefault(bin) + 1;
                    if (size < 3 && sub.Commands.Count > 2) pr.Diagnostics.TinySubpaths++;
                    if (size < 6)
                    {   // Text drawn as geometry: filled outlines (TrueType->curves/polygons) vs stroked open polylines (SHX).
                        glyphBoxes.Add(bb.Value);
                        bool curved = sub.Commands.Any(c => c is PdfSubpath.BezierCurve);
                        string kind = path.IsFilled ? (curved ? "filledCurvedOutline" : "filledStraightOutline")
                                    : path.IsStroked ? (sub.IsClosed() ? "strokedClosed" : "strokedOpenPolyline") : "other";
                        var g = pr.Diagnostics.SmallSubpathKinds;
                        g[kind] = g.GetValueOrDefault(kind) + 1;
                    }
                }
                Flatten(sub, (a, b) =>
                {
                    if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 1e-3) return;
                    pr.Segments.Add(new Segment { P = new[] { R(a.X), R(a.Y), R(b.X), R(b.Y) }, Color = color });
                });
            }
        }

        // AutoCAD often exports SHX text as comment annotations; record them.
        var annots = page.ExperimentalAccess.GetAnnotations().ToList();
        pr.Diagnostics.Annotations = annots.Count;
        var texts = annots.Select(a => a.Content).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList();
        if (texts.Count > 0) pr.Diagnostics.AnnotationTexts = texts;

        if (letters.Count == 0)
        {
            var k = pr.Diagnostics.SmallSubpathKinds;
            int filled = k.GetValueOrDefault("filledCurvedOutline") + k.GetValueOrDefault("filledStraightOutline");
            int shx = k.GetValueOrDefault("strokedOpenPolyline");
            pr.Diagnostics.TextLikeClusters = ClusterGlyphs(glyphBoxes);
            pr.Diagnostics.Note = $"NO TEXT LAYER: 0 text glyphs. {paths.Count} paths, {pr.Segments.Count} segments. " +
                (filled > shx ? $"Text appears to be drawn as {filled} small FILLED glyph outlines (text converted to geometry), not SHX strokes ({shx} small open polylines)."
                              : $"Text appears to be SHX-style stroked polylines ({shx} small open polylines vs {filled} filled outlines).") +
                $" {pr.Diagnostics.TextLikeClusters.Count} text-like glyph clusters located (see textLikeClusters).";
        }
        return pr;
    }

    // Groups small glyph-sized subpaths into runs (approximate text strings) by bbox proximity.
    static List<double[]> ClusterGlyphs(List<PdfRectangle> boxes)
    {
        int n = boxes.Count; var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var a = boxes[i]; var b = boxes[j];
                double gap = 0.6 * Math.Max(Math.Max(a.Height, a.Width), Math.Max(b.Height, b.Width));
                if (a.Left - gap <= b.Right && b.Left - gap <= a.Right && a.Bottom - gap <= b.Top && b.Bottom - gap <= a.Top)
                    parent[Find(i)] = Find(j);
            }
        return Enumerable.Range(0, n).GroupBy(Find).Where(g => g.Count() >= 3).Select(g =>
        {
            double l = g.Min(i => boxes[i].Left), b = g.Min(i => boxes[i].Bottom), r = g.Max(i => boxes[i].Right), t = g.Max(i => boxes[i].Top);
            return new[] { R(l), R(b), R(r - l), R(t - b), g.Count() };   // x, y, w, h, subpathCount
        }).OrderByDescending(c => c[1]).ThenBy(c => c[0]).ToList();
    }

    static void Flatten(PdfSubpath sub, Action<PdfPoint, PdfPoint> emit)
    {
        PdfPoint cur = default, start = default;
        foreach (var c in sub.Commands)
        {
            switch (c)
            {
                case PdfSubpath.Move m: cur = start = m.Location; break;
                case PdfSubpath.Line l: emit(l.From, l.To); cur = l.To; break;
                case PdfSubpath.BezierCurve b:
                    const int n = 4;
                    var prev = b.StartPoint;
                    for (int i = 1; i <= n; i++)
                    {
                        double t = (double)i / n, u = 1 - t;
                        var pt = new PdfPoint(
                            u * u * u * b.StartPoint.X + 3 * u * u * t * b.FirstControlPoint.X + 3 * u * t * t * b.SecondControlPoint.X + t * t * t * b.EndPoint.X,
                            u * u * u * b.StartPoint.Y + 3 * u * u * t * b.FirstControlPoint.Y + 3 * u * t * t * b.SecondControlPoint.Y + t * t * t * b.EndPoint.Y);
                        emit(prev, pt); prev = pt;
                    }
                    cur = b.EndPoint; break;
                case PdfSubpath.Close: emit(cur, start); cur = start; break;
            }
        }
    }

    static string? Hex(IColor? c)
    {
        if (c == null) return null;
        try
        {
            var (r, g, b) = c.ToRGBValues();
            return $"#{(int)Math.Round(r * 255):x2}{(int)Math.Round(g * 255):x2}{(int)Math.Round(b * 255):x2}";
        }
        catch { return null; }
    }

    public static double NormDeg(double d) { d %= 360; if (d < 0) d += 360; return d >= 359.95 ? 0 : d; }

    // Merge words on the same rotated baseline whose gap is < 1.5 x font size.
    static List<Line> MergeLines(List<Word> words)
    {
        var lines = new List<Line>();
        var used = new bool[words.Count];
        // Sort so chains are built from their leftmost (along-baseline) word.
        var order = Enumerable.Range(0, words.Count).OrderBy(i => Along(words[i], words[i].X, words[i].Y)).ToList();
        foreach (int i in order)
        {
            if (used[i]) continue;
            used[i] = true;
            var line = new Line { X = words[i].X, Y = words[i].Y, RotationDeg = words[i].RotationDeg, FontSize = words[i].FontSize };
            line.Words.Add(words[i]);
            var cur = words[i];
            while (true)
            {
                int best = -1; double bestGap = double.MaxValue;
                foreach (int j in order)
                {
                    if (used[j]) continue;
                    var w = words[j];
                    if (AngleDiff(w.RotationDeg, cur.RotationDeg) > 3) continue;
                    double a = cur.RotationDeg * Math.PI / 180, ux = Math.Cos(a), uy = Math.Sin(a);
                    double rx = w.X - cur.EndX, ry = w.Y - cur.EndY;
                    double gap = rx * ux + ry * uy;            // along baseline
                    double off = -rx * uy + ry * ux;           // perpendicular to baseline
                    double fs = Math.Max(cur.FontSize, w.FontSize);
                    if (Math.Abs(off) > 0.3 * fs || gap < -0.3 * fs || gap > 1.5 * fs) continue;
                    if (gap < bestGap) { bestGap = gap; best = j; }
                }
                if (best < 0) break;
                used[best] = true;
                line.Words.Add(words[best]);
                cur = words[best];
            }
            line.Text = string.Join(" ", line.Words.Select(w => w.Text));
            lines.Add(line);
        }
        return lines;
    }

    static double Along(Word w, double x, double y)
    {
        double a = w.RotationDeg * Math.PI / 180;
        return x * Math.Cos(a) + y * Math.Sin(a);
    }

    public static double AngleDiff(double a, double b) { double d = Math.Abs(NormDeg(a) - NormDeg(b)); return Math.Min(d, 360 - d); }
}

// ---------------------------------------------------------------- survey parsing

static class Survey
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // A number with optional space-separated thousands groups ("5 815 817 780", "323 590.64").
    const string Num = @"(\d{1,3}(?: \d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)";
    static readonly Regex CoordRx = new(@"\b([EN])\s*:?\s*" + Num + @"\s*(mm|m)?(?![\w.])");
    static readonly Regex AhdRx = new(@"\b(AHD|RL)\s*:?\s*" + Num + @"\s*(mm|m)?(?![\w.])");
    // 96 deg 47'30"  (degree sign may be U+00B0, U+00BA or U+02DA; minute/second marks ASCII or primes)
    static readonly Regex BearingRx = new("\\d{1,3}\\s*[°º˚]\\s*\\d{1,2}\\s*['′’]\\s*(?:\\d{1,2}\\s*[\"″”]?)?");
    static readonly Regex TokenRx = new(@"\d+(?:\.\d+)?|[^\s\d]+");
    static readonly Regex LabelRx = new(@"^\+?[A-Za-z][A-Za-z.\-/]*$");
    static readonly Regex MmNoteRx = new(@"LEVELS.*MILLIMET", RegexOptions.IgnoreCase);

    record CoordPart(char Axis, double Value, Line Line);
    record AhdHit(Level Level, Line Line);

    public static void Parse(PageResult pr, Result res)
    {
        int pg = pr.Page;
        // Drawings noting "LEVELS ... IN MILLIMETRES" write spot levels as bare 5-digit mm integers.
        bool mmLevels = pr.Lines.Any(l => MmNoteRx.IsMatch(l.Text));
        res.MillimetreLevels |= mmLevels;

        var bearingLines = pr.Lines.Where(l => BearingRx.IsMatch(l.Text)).ToList();
        var parts = new List<CoordPart>();
        var ahds = new List<AhdHit>();

        foreach (var l in pr.Lines)
        {
            string text = l.Text;
            var starts = WordStarts(l);
            var consumed = new List<(int S, int E)>();
            (double X, double Y) At(int idx) { int k = starts.FindLastIndex(s => s <= idx); var w = l.Words[Math.Max(k, 0)]; return (w.X, w.Y); }

            foreach (Match m in BearingRx.Matches(text))
            {
                var (x, y) = At(m.Index);
                res.Bearings.Add(new TextValue(m.Value.Trim(), BearingDeg(m.Value), x, y, l.RotationDeg, pg));
                consumed.Add((m.Index, m.Index + m.Length));
            }
            foreach (Match m in CoordRx.Matches(text))
            {
                string digits = m.Groups[2].Value.Replace(" ", "");
                if (digits.Split('.')[0].Length < 5) continue;   // not a grid coordinate
                char axis = m.Groups[1].Value[0];
                double v = Parse(m.Groups[2].Value);
                bool mm = m.Groups[3].Value == "mm" || (!digits.Contains('.') && v >= (axis == 'E' ? 1e7 : 1e8));
                parts.Add(new CoordPart(axis, mm ? v / 1000 : v, l));
                consumed.Add((m.Index, m.Index + m.Length));
            }
            foreach (Match m in AhdRx.Matches(text))
            {
                double v = Parse(m.Groups[2].Value);
                if (m.Groups[3].Value == "mm" || (!m.Groups[2].Value.Contains('.') && v >= 1000)) v /= 1000;
                consumed.Add((m.Index, m.Index + m.Length));
                if (v < 1 || v >= 1000) continue;
                var (x, y) = At(m.Index);
                var lv = new Level(m.Value, Math.Round(v, 4), m.Groups[1].Value, x, y, pg, text);
                res.Levels.Add(lv);
                ahds.Add(new AhdHit(lv, l));
            }

            // Remaining tokens: split letters from digits so "EDGE36955" -> "EDGE", "36955".
            var toks = TokenRx.Matches(text).Where(m => !consumed.Any(c => m.Index < c.E && m.Index + m.Length > c.S)).ToList();
            bool nearBearing = bearingLines.Any(b => Near(b, l));
            for (int i = 0; i < toks.Count; i++)
            {
                string t = toks[i].Value;
                if (!char.IsDigit(t[0])) continue;
                string prev = i > 0 ? toks[i - 1].Value : "", next = i + 1 < toks.Count ? toks[i + 1].Value : "";
                bool adjacentNext = i + 1 < toks.Count && toks[i + 1].Index == toks[i].Index + t.Length;
                string label = LabelBefore(toks, i);
                if (label == "" && toks.Count == 1) label = LabelAbove(pr, l);
                var (x, y) = At(toks[i].Index);
                int dp = t.Contains('.') ? t.Length - t.IndexOf('.') - 1 : 0;
                double v = Parse(t);

                // Distances: 2-dp numbers with an "m" unit, or unlabelled ones set alongside a bearing.
                bool metreUnit = (next is "m" or "M" && adjacentNext) || prev == "m";
                if (dp == 2 && (metreUnit || (nearBearing && label == "")))
                {
                    res.Distances.Add(new TextValue(t, v, x, y, l.RotationDeg, pg));
                    continue;
                }
                if (metreUnit || (next == "mm" && adjacentNext)) continue;
                if (dp is 2 or 3 && t.Length - dp - 1 == 2 && v >= 20 && v < 100)
                    res.Levels.Add(new Level(t, v, label, x, y, pg, text));
                else if (dp == 0 && t.Length == 5 && v >= 20000 && (label != "" || mmLevels))
                    res.Levels.Add(new Level(t, v / 1000.0, label, x, y, pg, text));
            }
        }

        // Pair E with the N on the same line or a stacked line; attach AHD and a title line ("TBM RIVET 1").
        foreach (var e in parts.Where(p => p.Axis == 'E'))
        {
            var n = parts.Where(p => p.Axis == 'N' && (p.Line == e.Line || Stacked(p.Line, e.Line, 3)))
                         .OrderBy(p => Dist(p.Line, e.Line)).FirstOrDefault();
            if (n == null) continue;
            var ahd = ahds.Where(a => a.Line == e.Line || Stacked(a.Line, e.Line, 4)).OrderBy(a => Dist(a.Line, e.Line)).FirstOrDefault();
            var used = new HashSet<Line> { e.Line, n.Line };
            if (ahd != null) used.Add(ahd.Line);
            var title = pr.Lines.Where(t => !used.Contains(t) && Stacked(t, e.Line, 2.5) && Regex.IsMatch(t.Text, "[A-Za-z]{2}")
                                            && !CoordRx.IsMatch(t.Text) && !AhdRx.IsMatch(t.Text))
                                .OrderBy(t => Dist(t, e.Line)).FirstOrDefault();
            var blockLines = new List<Line?> { title, e.Line, n.Line, ahd?.Line }.Where(x => x != null).Distinct();
            string raw = string.Join(" | ", blockLines.Select(x => x!.Text));
            res.Coordinates.Add(new Coordinate(raw, e.Value, n.Value, ahd?.Level.Value, title?.Text ?? "", e.Line.X, e.Line.Y, pg));
            if (ahd != null && title != null)
                res.Levels[res.Levels.IndexOf(ahd.Level)] = ahd.Level with { Label = $"{title.Text} AHD" };
        }
    }

    static List<int> WordStarts(Line l)
    {
        var starts = new List<int>(); int pos = 0;
        foreach (var w in l.Words) { starts.Add(pos); pos += w.Text.Length + 1; }
        return starts;
    }

    // Contiguous label words immediately before token i (leading "+" stripped, e.g. "+FFL" -> "FFL").
    static string LabelBefore(List<Match> toks, int i)
    {
        int s = i;
        while (s > 0 && LabelRx.IsMatch(toks[s - 1].Value) && toks[s - 1].Value is not ("m" or "mm")) s--;
        return string.Join(" ", toks.Skip(s).Take(i - s).Select(t => t.Value.TrimStart('+')));
    }

    // A bare number often sits under its label on separate lines ("GUTTER TOP" / "38690", or "PARAPET" / "TOP" / "38515").
    static string LabelAbove(PageResult pr, Line l)
    {
        var t = LabelLineAbove(pr, l);
        if (t == null) return "";
        string text = string.Join(" ", t.Words.Select(w => w.Text.TrimStart('+')));
        // Single-word label lines may continue one line higher with another single word.
        var up = t.Words.Count == 1 ? LabelLineAbove(pr, t) : null;
        return up != null && up.Words.Count == 1 ? up.Words[0].Text.TrimStart('+') + " " + text : text;
    }

    static Line? LabelLineAbove(PageResult pr, Line l)
    {
        double fs = l.FontSize;
        var above = pr.Lines.Where(t => t != l && SameDir(t, l) && t.Words.All(w => LabelRx.IsMatch(w.Text)))
            .Select(t => (t, r: Rel(l, t), len: Rel(l, new Line { X = t.Words[^1].EndX, Y = t.Words[^1].EndY }).Along))
            // label line is 0.5-1.8 line heights above, and the number starts within the label's horizontal span
            .Where(p => p.r.Perp > 0.5 * fs && p.r.Perp < 1.8 * fs && p.r.Along < 1.5 * fs && p.len > -1.5 * fs)
            .OrderBy(p => p.r.Perp).ThenBy(p => Math.Abs(p.r.Along)).FirstOrDefault();
        return above.t;
    }

    // Offsets of b relative to a, in a's rotated baseline frame.
    static (double Along, double Perp) Rel(Line a, Line b)
    {
        double r = a.RotationDeg * Math.PI / 180, ux = Math.Cos(r), uy = Math.Sin(r);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return (dx * ux + dy * uy, -dx * uy + dy * ux);
    }

    static bool SameDir(Line a, Line b) => Extract.AngleDiff(a.RotationDeg % 180, b.RotationDeg % 180) < 3;

    // b is a stacked line of the same text block as a (within k line heights, roughly aligned).
    static bool Stacked(Line b, Line a, double k)
    {
        if (b == a || !SameDir(a, b)) return false;
        var (al, pe) = Rel(a, b); double fs = Math.Max(a.FontSize, b.FontSize);
        return Math.Abs(pe) < k * 1.3 * fs && Math.Abs(al) < 3 * fs;
    }

    static bool Near(Line bearing, Line l)
    {
        if (!SameDir(bearing, l)) return false;
        if (bearing == l) return true;
        var (al, pe) = Rel(bearing, l); double fs = Math.Max(bearing.FontSize, l.FontSize);
        return Math.Abs(pe) < 2 * fs && Math.Abs(al) < 15 * fs;
    }

    static double Parse(string s) => double.Parse(s.Replace(" ", "").Replace(",", ""), Inv);
    static double Dist(Line a, Line b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static double BearingDeg(string s)
    {
        var p = Regex.Matches(s, @"\d+").Select(m => double.Parse(m.Value, Inv)).ToList();
        return Math.Round(p[0] + (p.Count > 1 ? p[1] / 60 : 0) + (p.Count > 2 ? p[2] / 3600 : 0), 6);
    }
}
