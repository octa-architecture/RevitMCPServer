using System.Text.Json;

// Usage:
//   Polygonizer faces <segments.json> <out.json> [tol=0.03] [minArea=6] [maxArea=3000]
//   Polygonizer footprints <segments.json> <roof.json> <ground.json> <out.json> [minHeight=2]
// segments: [[x1,y1,x2,y2] | [x1,y1,z1,x2,y2,z2]]; roof/ground: [[x,y,z]] (metres, internal).
static List<double[]> Segs(string path) => JsonSerializer.Deserialize<List<double[]>>(File.ReadAllText(path))!
    .Select(s => s.Length >= 6 ? new[] { s[0], s[1], s[3], s[4] } : s).ToList();   // [x1,y1,x2,y2(,isWallLine)]
static List<double[]> Pts(string path) => JsonSerializer.Deserialize<List<double[]>>(File.ReadAllText(path))!;

var inv = System.Globalization.CultureInfo.InvariantCulture;
if (args[0] == "faces")
{
    var segs = Segs(args[1]);
    var faces = Polygonize.Faces(segs, args.Length > 3 ? double.Parse(args[3], inv) : 0.03,
        args.Length > 4 ? double.Parse(args[4], inv) : 6, args.Length > 5 ? double.Parse(args[5], inv) : 3000);
    File.WriteAllText(args[2], JsonSerializer.Serialize(faces));
    Console.WriteLine($"{segs.Count} segments -> {faces.Count} faces");
}
else if (args[0] == "render")
{
    // Polygonizer render <points.csv> <out.png> <west|east|north|south|plan> [pxPerM=100] [rgb|depth]
    Console.WriteLine(Render.Run(args[1], args[2], args[3], args.Length > 4 ? double.Parse(args[4], inv) : 100, args.Length > 5 ? args[5] : "rgb"));
}
else if (args[0] == "rhino")
{
    // Polygonizer rhino <export.json> <out.json> <originX> <originY> <thetaDeg> <name> <category> [object|merge]
    Console.WriteLine(RhinoToRevit.Run(args[1], args[2], double.Parse(args[3], inv), double.Parse(args[4], inv), double.Parse(args[5], inv),
        args[6], args[7], args.Length > 8 ? args[8] : "merge"));
}
else if (args[0] == "lace")
{
    // Polygonizer lace <in.csv> <outPrefix> <name> [--x 2.94] [--thickness 0.012] [--zmin ..] [--zmax ..] [--ymin ..] [--ymax ..] [--posts y1,y2] (see Lacework.cs)
    Console.WriteLine(Lacework.Run(args));
}
else if (args[0] == "lacediag2") Lacework.Diag2(args);
else if (args[0] == "lacegen") Console.WriteLine(LaceGen.Run(args[1], args[2]));
else if (args[0] == "imgtrace")
{
    // Polygonizer imgtrace <png> <out.json> <preview.png> <heightMm> [cellPx=2] [tolPx=1.2] [minAreaPx=6] [invert|-] [tiles=1]
    if (args.Length > 9) ImageTrace.Tiles = int.Parse(args[9]);
    Console.WriteLine(ImageTrace.Run(args[1], args[2], args[3], double.Parse(args[4], inv),
        args.Length > 5 ? int.Parse(args[5]) : 2, args.Length > 6 ? double.Parse(args[6], inv) : 1.2,
        args.Length > 7 ? double.Parse(args[7], inv) : 6, args.Length > 8 && args[8] == "invert"));
}
else if (args[0] == "walls")
{
    // Polygonizer walls <segments.json> <out.json>
    var w = Walls.Find(Segs(args[1]));
    File.WriteAllText(args[2], JsonSerializer.Serialize(w));
    Console.WriteLine($"{w.Count} walls");
    foreach (var g in w.GroupBy(x => Math.Round(x.Thickness * 100) / 100).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key * 1000,4:N0} mm: {g.Count(),3} walls, {g.Sum(x => x.Length),6:N1} m");
}
else
{
    var b = Footprints.Find(Segs(args[1]), Pts(args[2]), Pts(args[3]), minHeight: args.Length > 5 ? double.Parse(args[5], inv) : 2.0, minCoverage: args.Length > 6 ? double.Parse(args[6], inv) : 0.5);
    File.WriteAllText(args[4], JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = false }));
    Console.WriteLine($"{b.Count} buildings");
    foreach (var x in b) Console.WriteLine($"  area {x.AreaM2,7:N1}  ground {x.GroundZ:N3}  eave {x.EaveZ:N3}  ridge {x.RidgeZ:N3}  cover {x.Coverage:P0}  pts {x.Outline.Length}  x {x.Outline.Min(p => p[0]):N1}..{x.Outline.Max(p => p[0]):N1} y {x.Outline.Min(p => p[1]):N1}..{x.Outline.Max(p => p[1]):N1}");
}


