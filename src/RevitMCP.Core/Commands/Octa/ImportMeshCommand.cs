using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Import mesh geometry (e.g. from Rhino or a scan) as DirectShapes. Reads a JSON file on this PC:
/// [{ name, category?, vertices: [[x,y,z],...] (metres, internal), faces: [[a,b,c] | [a,b,c,d],...],
///    comments? }]. Params: path, phase? "Existing", category? (default for objects without one,
/// e.g. "Generic Models", "Columns", "Roofs", "Walls", "Railings", "Site").
/// </summary>
public sealed class ImportMeshDirectShapeCommand : IRevitCommand
{
    public string Name => "import_mesh_directshape";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public bool ResolveErrorsOnCommit => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var ft = P.MetersToFeet;
        var arr = JsonNode.Parse(File.ReadAllText(P.Str(p, "path"))) as JsonArray
            ?? throw new RevitCommandException("invalid_parameter", "File must hold a JSON array of meshes.");
        var phase = doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(P.StrOrNull(p, "phase") ?? "Existing", StringComparison.OrdinalIgnoreCase));
        var defCat = P.StrOrNull(p, "category") ?? "Generic Models";
        ElementId CatId(string name)
        {
            foreach (Category c in doc.Settings.Categories)
                if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && DirectShape.IsValidCategoryId(c.Id, doc)) return c.Id;
            return new ElementId(BuiltInCategory.OST_GenericModel);
        }

        var created = new List<long>(); var rows = new JsonArray();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var name = P.StrOrNull(o, "name") ?? "Imported mesh";
            try
            {
                var v = P.Arr(o, "vertices").Select(a => (JsonArray)a!)
                    .Select(a => new XYZ(P.DblFrom(a[0], "x") * ft, P.DblFrom(a[1], "y") * ft, P.DblFrom(a[2], "z") * ft)).ToList();
                var b = new TessellatedShapeBuilder { Target = TessellatedShapeBuilderTarget.AnyGeometry, Fallback = TessellatedShapeBuilderFallback.Mesh };
                b.OpenConnectedFaceSet(false);
                int faces = 0;
                foreach (var f in P.Arr(o, "faces"))
                {
                    var idx = ((JsonArray)f!).Select(x => P.IntFrom(x, "face")).ToList();
                    if (idx.Count == 4 && idx[2] == idx[3]) idx.RemoveAt(3);
                    var loop = idx.Select(k => v[k]).ToList();
                    if (loop.Count < 3 || loop.Distinct().Count() < loop.Count) continue;
                    // skip degenerate slivers
                    if ((loop[1] - loop[0]).CrossProduct(loop[2] - loop[0]).GetLength() < 1e-9) continue;
                    b.AddFace(new TessellatedFace(loop, ElementId.InvalidElementId));
                    faces++;
                }
                b.CloseConnectedFaceSet();
                b.Build();
                var res = b.GetBuildResult();
                var ds = DirectShape.CreateElement(doc, CatId(P.StrOrNull(o, "category") ?? defCat));
                ds.SetShape(res.GetGeometricalObjects());
                ds.Name = name;
                if (phase is not null) ds.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
                ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(P.StrOrNull(o, "comments") ?? name);
                created.Add(ds.Id.Value);
                rows.Add(new JsonObject { ["name"] = name, ["id"] = ds.Id.Value, ["faces"] = faces, ["outcome"] = res.Outcome.ToString() });
            }
            catch (Exception ex) { rows.Add(new JsonObject { ["name"] = name, ["error"] = ex.Message }); }
        }
        return new JsonObject { ["affected"] = Affected.Created(created), ["imported"] = created.Count, ["objects"] = rows };
    }
}
