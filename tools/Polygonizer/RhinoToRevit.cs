using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

// Rhino3dmReader export (render meshes, survey mm with the MGA truncation) -> mesh JSON for
// import_mesh_directshape (internal metres): subtract the site origin, rotate by the site angle.
// mode "object": one mesh per Rhino object; "merge": all objects into one mesh.
public static class RhinoToRevit
{
    public static string Run(string inPath, string outPath, double ox, double oy, double thetaDeg, string name, string category, string mode)
    {
        var root = JsonNode.Parse(File.ReadAllText(inPath))!;
        var objs = root["objects"]!.AsArray();
        double th = thetaDeg * Math.PI / 180, c = Math.Cos(th), s = Math.Sin(th);
        double[] Map(JsonArray v)
        {
            double x = v[0]!.GetValue<double>() / 1000 - ox, y = v[1]!.GetValue<double>() / 1000 - oy, z = v[2]!.GetValue<double>() / 1000;
            return new[] { Math.Round(x * c - y * s, 4), Math.Round(x * s + y * c, 4), Math.Round(z, 4) };
        }
        var outArr = new JsonArray();
        JsonArray? mv = null, mf = null; int k = 0, used = 0;
        foreach (var o in objs)
        {
            var mesh = o!["renderMesh"] ?? o["mesh"];
            if (mesh?["vertices"] is not JsonArray verts || mesh["faces"] is not JsonArray faces) continue;
            used++;
            if (mode == "object" || mv is null)
            {
                mv = new JsonArray(); mf = new JsonArray(); k = 0;
                outArr.Add(new JsonObject
                {
                    ["name"] = mode == "object" ? $"{name} {outArr.Count + 1}" : name,
                    ["category"] = category, ["vertices"] = mv, ["faces"] = mf,
                    ["comments"] = $"From Rhino Existing Conditions ({o["layer"]}), survey coordinates",
                });
            }
            int baseIdx = k;
            foreach (var v in verts) { var p = Map((JsonArray)v!); mv!.Add(new JsonArray(p[0], p[1], p[2])); k++; }
            foreach (var f in faces)
                mf!.Add(new JsonArray(((JsonArray)f!).Select(i => (JsonNode)(i!.GetValue<int>() + baseIdx)).ToArray()));
        }
        File.WriteAllText(outPath, outArr.ToJsonString());
        return $"{used} objects -> {outArr.Count} meshes";
    }
}
