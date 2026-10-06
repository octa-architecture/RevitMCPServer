using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Set visibility/graphics overrides for whole categories in a view OR a view template
/// (pass the template's id as viewId). Use this instead of hiding elements in details.
/// Params: viewId (required), overrides: [{ category, ...override keys (see OctaUtil.BuildOverrides), visible? }],
/// detailLevel? coarse|medium|fine.
/// </summary>
public sealed class SetCategoryOverridesCommand : IRevitCommand
{
    public string Name => "set_category_overrides";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = doc.GetElement(new ElementId(P.Long(p, "viewId"))) as View
            ?? throw new RevitCommandException("not_found", "viewId is not a view or view template.");
        if (!view.IsTemplate && view.ViewTemplateId != ElementId.InvalidElementId)
            throw new RevitCommandException("template_controlled",
                $"View '{view.Name}' is controlled by template '{doc.GetElement(view.ViewTemplateId)?.Name}'. " +
                "Change the template (pass its id) or remove it from the view first.");

        var done = new JsonArray();
        foreach (var node in P.Arr(p, "overrides"))
        {
            if (node is not JsonObject o) continue;
            var cat = OctaUtil.ResolveCategory(doc, P.Str(o, "category"));
            if (!view.IsCategoryOverridable(cat.Id))
                throw new RevitCommandException("invalid_parameter", $"Category '{cat.Name}' can't be overridden in this view.");
            view.SetCategoryOverrides(cat.Id, OctaUtil.BuildOverrides(doc, o));
            if (o["visible"] is not null) view.SetCategoryHidden(cat.Id, !P.BoolOr(o, "visible", true));
            done.Add(cat.Name);
        }
        if (P.StrOrNull(p, "detailLevel") is { } dl)
            view.DetailLevel = dl.ToLowerInvariant() switch
            {
                "coarse" => ViewDetailLevel.Coarse,
                "medium" => ViewDetailLevel.Medium,
                "fine" => ViewDetailLevel.Fine,
                _ => throw new RevitCommandException("invalid_parameter", "detailLevel must be coarse, medium or fine."),
            };
        return new JsonObject
        {
            ["affected"] = Affected.Modified(view.Id.Value),
            ["viewId"] = view.Id.Value,
            ["isTemplate"] = view.IsTemplate,
            ["categories"] = done,
        };
    }
}

/// <summary>
/// Full per-element graphics override in a view: halftone (fade), line colours/weights/patterns,
/// surface and cut fills. Params: viewId, elementIds, plus override keys, or reset=true.
/// </summary>
public sealed class SetElementGraphicsCommand : IRevitCommand
{
    public string Name => "set_element_graphics";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = OctaUtil.ResolveView(doc, p);
        var ids = OctaUtil.Ids(p, "elementIds");
        var ogs = P.BoolOr(p, "reset", false) ? new OverrideGraphicSettings() : OctaUtil.BuildOverrides(doc, p);
        foreach (var id in ids) view.SetElementOverrides(id, ogs);
        return new JsonObject
        {
            ["affected"] = Affected.Modified(view.Id.Value),
            ["viewId"] = view.Id.Value,
            ["count"] = ids.Count,
        };
    }
}

/// <summary>
/// OCTA review flag: draw elements' projection and cut lines in magenta (heavier weight) in a view,
/// so clashes / items needing review stand out without hiding anything. reset=true clears.
/// Params: viewId, elementIds, lineWeight? (default 6), reset?.
/// </summary>
public sealed class FlagElementsCommand : IRevitCommand
{
    public string Name => "octa_flag_elements";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = OctaUtil.ResolveView(doc, p);
        var ids = OctaUtil.Ids(p, "elementIds");
        var reset = P.BoolOr(p, "reset", false);
        var w = Math.Clamp(P.IntOr(p, "lineWeight", 6), 1, 16);
        foreach (var id in ids)
        {
            var ogs = reset ? new OverrideGraphicSettings() : view.GetElementOverrides(id);
            if (!reset)
            {
                ogs.SetProjectionLineColor(OctaUtil.FlagColor);
                ogs.SetCutLineColor(OctaUtil.FlagColor);
                ogs.SetProjectionLineWeight(w);
                ogs.SetCutLineWeight(w);
                ogs.SetHalftone(false);
            }
            view.SetElementOverrides(id, ogs);
        }
        return new JsonObject
        {
            ["affected"] = Affected.Modified(view.Id.Value),
            ["viewId"] = view.Id.Value,
            ["count"] = ids.Count,
            ["flagged"] = !reset,
        };
    }
}

/// <summary>
/// Create a view template from a view (its current graphics), optionally assigning it back.
/// Params: viewId, name, assign? (default true).
/// </summary>
public sealed class CreateViewTemplateFromViewCommand : IRevitCommand
{
    public string Name => "create_view_template_from_view";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = doc.GetElement(new ElementId(P.Long(p, "viewId"))) as View
            ?? throw new RevitCommandException("not_found", "viewId is not a view.");
        var name = P.Str(p, "name");
        if (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Any(v => v.IsTemplate && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new RevitCommandException("name_collision", $"A view template named '{name}' already exists.");
        var tpl = view.CreateViewTemplate();
        tpl.Name = name;
        if (P.BoolOr(p, "assign", true)) view.ViewTemplateId = tpl.Id;
        return new JsonObject
        {
            ["affected"] = Affected.Of(created: new[] { tpl.Id.Value }, modified: new[] { view.Id.Value }),
            ["templateId"] = tpl.Id.Value,
            ["name"] = tpl.Name,
        };
    }
}

/// <summary>
/// Read a view's graphics setup: template, detail level, scale, category overrides, and every
/// element hidden in the view (so hidden-element "cheats" in details can be found and fixed).
/// Params: viewId (default active), maxHidden? (default 200).
/// </summary>
public sealed class GetViewGraphicsCommand : IRevitCommand
{
    public string Name => "get_view_graphics";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = OctaUtil.ResolveView(doc, p);
        var max = P.IntOr(p, "maxHidden", 200);

        var hidden = new List<Element>();
        if (!view.IsTemplate)
        {
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (e.Category is null || e is View) continue;
                bool isHidden;
                try { isHidden = e.IsHidden(view); } catch { continue; }
                if (isHidden) hidden.Add(e);
            }
        }

        var catOverrides = new JsonArray();
        foreach (Category c in doc.Settings.Categories)
        {
            if (!view.IsCategoryOverridable(c.Id)) continue;
            var o = view.GetCategoryOverrides(c.Id);
            bool any = o.Halftone || o.ProjectionLineColor.IsValid || o.CutLineColor.IsValid ||
                       o.SurfaceForegroundPatternId != ElementId.InvalidElementId ||
                       o.CutForegroundPatternId != ElementId.InvalidElementId || o.Transparency > 0;
            bool catHidden = view.GetCategoryHidden(c.Id);
            if (!any && !catHidden) continue;
            catOverrides.Add(new JsonObject
            {
                ["category"] = c.Name,
                ["hidden"] = catHidden,
                ["halftone"] = o.Halftone,
                ["projectionLineColor"] = OctaUtil.ColorJson(o.ProjectionLineColor),
                ["cutLineColor"] = OctaUtil.ColorJson(o.CutLineColor),
                ["surfacePattern"] = OctaUtil.PatternName(doc, o.SurfaceForegroundPatternId),
                ["surfaceColor"] = OctaUtil.ColorJson(o.SurfaceForegroundPatternColor),
                ["cutPattern"] = OctaUtil.PatternName(doc, o.CutForegroundPatternId),
                ["cutColor"] = OctaUtil.ColorJson(o.CutForegroundPatternColor),
            });
        }

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
            ["isTemplate"] = view.IsTemplate,
            ["template"] = view.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(view.ViewTemplateId)?.Name,
            ["detailLevel"] = view.DetailLevel.ToString(),
            ["scale"] = view.Scale,
            ["hiddenElementCount"] = hidden.Count,
            ["hiddenElements"] = new JsonArray(hidden.Take(max).Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id.Value, ["category"] = e.Category?.Name, ["name"] = e.Name,
            }).ToArray()),
            ["categoryOverrides"] = catOverrides,
        };
    }
}
