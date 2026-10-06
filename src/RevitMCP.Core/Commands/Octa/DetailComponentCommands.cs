using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// List loaded detail component families/types with their length parameters (mm).
/// Params: familyContains (optional).
/// </summary>
public sealed class ListDetailComponentsCommand : IRevitCommand
{
    public string Name => "list_detail_components";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var contains = P.StrOrNull(ctx.Parameters, "familyContains");
        var rows = new JsonArray();
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                     .OfCategory(BuiltInCategory.OST_DetailComponents).Cast<FamilySymbol>()
                     .OrderBy(s => s.FamilyName).ThenBy(s => s.Name))
        {
            if (contains is not null && s.FamilyName.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(new JsonObject
            {
                ["symbolId"] = s.Id.Value,
                ["family"] = s.FamilyName,
                ["type"] = s.Name,
                ["lineBased"] = s.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail,
                ["typeLengths_mm"] = OctaUtil.ParamsJson(s),
            });
        }
        return new JsonObject { ["count"] = rows.Count, ["components"] = rows };
    }
}

/// <summary>
/// Place a detail component (detail item) in a 2D view, optionally creating a new type
/// with given sizes first.
///
/// Params:
///   familyName (required) + typeName, or symbolId
///   viewId (default active)
///   location {x,y} for point-based families, or start/end {x,y} for line-based ones
///   rotationDegrees (point-based, about the view direction)
///   newType: { name, params: { "Width": 45, "Depth": 90 } } — duplicate the type with these
///            type values (length params in mm) if a type with that name doesn't exist
///   instanceParams: { name: value } — length params in mm
///   units: meters|millimeters|feet (coordinates)
/// </summary>
public sealed class PlaceDetailComponentCommand : IRevitCommand
{
    public string Name => "place_detail_component";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var scale = OctaUtil.UnitScale(p);
        var view = OctaUtil.ResolveView(doc, p);
        if (view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.DrawingSheet)
            throw new RevitCommandException("unsupported_view", $"Detail components need a 2D view, not '{view.ViewType}'.");

        FamilySymbol symbol;
        if (p["symbolId"] is not null)
        {
            symbol = doc.GetElement(new ElementId(P.Long(p, "symbolId"))) as FamilySymbol
                ?? throw new RevitCommandException("not_found", "symbolId is not a family type.");
        }
        else
        {
            var family = P.Str(p, "familyName");
            var types = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_DetailComponents).Cast<FamilySymbol>()
                .Where(s => s.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase)).ToList();
            if (types.Count == 0)
                throw new RevitCommandException("not_found",
                    $"Detail family '{family}' isn't loaded. Load it from the OCTA library first (revit_load_family).");
            var typeName = P.StrOrNull(p, "typeName");
            symbol = typeName is null ? types[0]
                : types.FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new RevitCommandException("not_found",
                      $"Type '{typeName}' not in '{family}'. Types: {string.Join(", ", types.Select(t => t.Name))}. " +
                      "Use newType to create it.");
        }

        bool typeCreated = false;
        if (p["newType"] is JsonObject nt)
        {
            var ntName = P.Str(nt, "name");
            var existing = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Id == symbol.Family.Id && s.Name.Equals(ntName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) symbol = existing;
            else
            {
                symbol = (FamilySymbol)symbol.Duplicate(ntName);
                typeCreated = true;
                if (nt["params"] is JsonObject tp)
                    foreach (var kv in tp) OctaUtil.SetParam(symbol, kv.Key, kv.Value);
            }
        }
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

        FamilyInstance fi;
        if (symbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail)
        {
            var a = ViewPlane.Project(view, OctaUtil.PointParam(p, "start", scale));
            var b = ViewPlane.Project(view, OctaUtil.PointParam(p, "end", scale));
            if (a.DistanceTo(b) < 1e-6) throw new RevitCommandException("bad_request", "start and end are the same point.");
            fi = doc.Create.NewFamilyInstance(Line.CreateBound(a, b), symbol, view);
        }
        else
        {
            var loc = ViewPlane.Project(view, OctaUtil.PointParam(p, "location", scale));
            fi = doc.Create.NewFamilyInstance(loc, symbol, view);
            var deg = P.DblOr(p, "rotationDegrees", 0);
            if (Math.Abs(deg) > 1e-9)
                ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateUnbound(loc, view.ViewDirection), deg * Math.PI / 180);
        }

        if (p["instanceParams"] is JsonObject ip)
            foreach (var kv in ip) OctaUtil.SetParam(fi, kv.Key, kv.Value);

        return new JsonObject
        {
            ["affected"] = typeCreated ? Affected.Of(created: new[] { fi.Id.Value, symbol.Id.Value }) : Affected.Created(fi.Id.Value),
            ["id"] = fi.Id.Value,
            ["family"] = symbol.FamilyName,
            ["type"] = symbol.Name,
            ["typeCreated"] = typeCreated,
            ["viewId"] = view.Id.Value,
        };
    }
}
