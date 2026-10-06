using System;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// List fill patterns. Params: target ("drafting"|"model"|omit for both), nameContains.
/// </summary>
public sealed class ListFillPatternsCommand : IRevitCommand
{
    public string Name => "list_fill_patterns";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var target = P.StrOrNull(ctx.Parameters, "target")?.ToLowerInvariant();
        var contains = P.StrOrNull(ctx.Parameters, "nameContains");
        var rows = new JsonArray();
        foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                     .OrderBy(f => f.Name))
        {
            var fp = f.GetFillPattern();
            var t = fp.Target == FillPatternTarget.Drafting ? "drafting" : "model";
            if (target is not null && target != t) continue;
            if (contains is not null && f.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(new JsonObject { ["id"] = f.Id.Value, ["name"] = f.Name, ["target"] = t, ["isSolid"] = fp.IsSolidFill });
        }
        return new JsonObject { ["count"] = rows.Count, ["patterns"] = rows };
    }
}

/// <summary>List filled region types with their patterns, colours, masking and line weight.</summary>
public sealed class ListFilledRegionTypesCommand : IRevitCommand
{
    public string Name => "list_filled_region_types";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var usage = new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>()
            .GroupBy(r => r.GetTypeId()).ToDictionary(g => g.Key, g => g.Count());
        var rows = new JsonArray();
        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                     .OrderBy(t => t.Name))
        {
            rows.Add(new JsonObject
            {
                ["id"] = t.Id.Value,
                ["name"] = t.Name,
                ["foregroundPattern"] = OctaUtil.PatternName(doc, t.ForegroundPatternId),
                ["foregroundColor"] = OctaUtil.ColorJson(t.ForegroundPatternColor),
                ["backgroundPattern"] = OctaUtil.PatternName(doc, t.BackgroundPatternId),
                ["backgroundColor"] = OctaUtil.ColorJson(t.BackgroundPatternColor),
                ["isMasking"] = t.IsMasking,
                ["lineWeight"] = t.LineWeight,
                ["instances"] = usage.TryGetValue(t.Id, out var n) ? n : 0,
            });
        }
        return new JsonObject { ["count"] = rows.Count, ["types"] = rows };
    }
}

/// <summary>
/// Create (or update) a filled region type by duplicating an existing one.
/// Params: name (required), baseTypeName/baseTypeId (optional),
/// foregroundPattern, foregroundColor {r,g,b}, backgroundPattern, backgroundColor,
/// isMasking, lineWeight (1-16), updateIfExists (default false).
/// </summary>
public sealed class CreateFilledRegionTypeCommand : IRevitCommand
{
    public string Name => "create_filled_region_type";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var name = P.Str(p, "name");
        var all = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
        var existing = all.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        FilledRegionType frt;
        bool created;
        if (existing is not null)
        {
            if (!P.BoolOr(p, "updateIfExists", false))
                throw new RevitCommandException("name_collision",
                    $"Filled region type '{name}' already exists (id {existing.Id.Value}). Pass updateIfExists=true to change it.");
            frt = existing;
            created = false;
        }
        else
        {
            var baseType = p["baseTypeName"] is not null || p["baseTypeId"] is not null
                ? OctaUtil.ByNameOrId<FilledRegionType>(doc, p, "baseTypeName", "baseTypeId", "Filled region type")
                : all.FirstOrDefault() ?? throw new RevitCommandException("not_found", "No filled region type to duplicate.");
            frt = (FilledRegionType)baseType.Duplicate(name);
            created = true;
        }

        if (P.StrOrNull(p, "foregroundPattern") is { } fg) frt.ForegroundPatternId = OctaUtil.FillPatternId(doc, fg);
        if (OctaUtil.ColorOrNull(p, "foregroundColor") is { } fgc) frt.ForegroundPatternColor = fgc;
        if (P.StrOrNull(p, "backgroundPattern") is { } bg)
            frt.BackgroundPatternId = bg.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? ElementId.InvalidElementId : OctaUtil.FillPatternId(doc, bg);
        if (OctaUtil.ColorOrNull(p, "backgroundColor") is { } bgc) frt.BackgroundPatternColor = bgc;
        if (p["isMasking"] is not null) frt.IsMasking = P.BoolOr(p, "isMasking", false);
        if (p["lineWeight"] is not null) frt.LineWeight = Math.Clamp(P.Int(p, "lineWeight"), 1, 16);

        return new JsonObject
        {
            ["affected"] = created ? Affected.Created(frt.Id.Value) : Affected.Modified(frt.Id.Value),
            ["id"] = frt.Id.Value,
            ["name"] = frt.Name,
            ["created"] = created,
        };
    }
}
