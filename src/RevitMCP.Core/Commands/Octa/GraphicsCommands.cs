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
        // Detail templates mustn't force a scale (or crop) onto every view they're applied to.
        var uncontrol = p["uncontrolled"] is JsonArray u
            ? u.Select((n, i) => P.StrFrom(n, $"uncontrolled[{i}]")).ToList()
            : new List<string> { "View Scale" };
        var released = TemplateControls.Release(tpl, uncontrol);
        if (P.BoolOr(p, "assign", true)) view.ViewTemplateId = tpl.Id;
        return new JsonObject
        {
            ["affected"] = Affected.Of(created: new[] { tpl.Id.Value }, modified: new[] { view.Id.Value }),
            ["templateId"] = tpl.Id.Value,
            ["name"] = tpl.Name,
            ["uncontrolled"] = new JsonArray(released.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        };
    }
}

internal static class TemplateControls
{
    /// <summary>Stop a template controlling the named properties (e.g. "View Scale"). Returns names released.</summary>
    public static List<string> Release(View tpl, IEnumerable<string> names)
    {
        var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        // "View Scale" has a paired "Scale Value 1:" parameter; release both together.
        if (want.Contains("View Scale"))
            foreach (Parameter prm in tpl.Parameters)
                if (prm.Definition?.Name?.StartsWith("Scale Value", StringComparison.OrdinalIgnoreCase) == true)
                    want.Add(prm.Definition.Name);
        var nonControlled = tpl.GetNonControlledTemplateParameterIds().ToList();
        var released = new List<string>();
        foreach (var id in tpl.GetTemplateParameterIds())
        {
            var prmName = tpl.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == id)?.Definition?.Name;
            if (prmName is null || !want.Contains(prmName) || nonControlled.Contains(id)) continue;
            nonControlled.Add(id);
            released.Add(prmName);
        }
        tpl.SetNonControlledTemplateParameterIds(nonControlled);
        return released;
    }

    public static List<string> Control(View tpl, IEnumerable<string> names)
    {
        var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var nonControlled = tpl.GetNonControlledTemplateParameterIds().ToList();
        var controlled = new List<string>();
        foreach (var id in nonControlled.ToList())
        {
            var prmName = tpl.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == id)?.Definition?.Name;
            if (prmName is null || !want.Contains(prmName)) continue;
            nonControlled.Remove(id);
            controlled.Add(prmName);
        }
        tpl.SetNonControlledTemplateParameterIds(nonControlled);
        return controlled;
    }
}

/// <summary>
/// Choose which properties a view template controls. Params: templateId or templateName,
/// release? [names] (e.g. ["View Scale"]), control? [names]. Returns the template's controlled list.
/// </summary>
public sealed class SetTemplateControlsCommand : IRevitCommand
{
    public string Name => "set_template_controls";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        View tpl;
        if (p["templateId"] is not null)
            tpl = doc.GetElement(new ElementId(P.Long(p, "templateId"))) as View
                  ?? throw new RevitCommandException("not_found", "templateId is not a view.");
        else
        {
            var n = P.Str(p, "templateName");
            tpl = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                      .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                  ?? throw new RevitCommandException("not_found", $"View template '{n}' not found.");
        }
        if (!tpl.IsTemplate) throw new RevitCommandException("invalid_parameter", $"'{tpl.Name}' is not a view template.");
        List<string> Names(string key) => p[key] is JsonArray a ? a.Select((n, i) => P.StrFrom(n, $"{key}[{i}]")).ToList() : new List<string>();
        var released = TemplateControls.Release(tpl, Names("release"));
        var controlled = TemplateControls.Control(tpl, Names("control"));
        var non = tpl.GetNonControlledTemplateParameterIds().ToHashSet();
        var list = tpl.GetTemplateParameterIds().Where(id => !non.Contains(id))
            .Select(id => tpl.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == id)?.Definition?.Name)
            .Where(s => s is not null).Select(s => (JsonNode)JsonValue.Create(s!)!).ToArray();
        return new JsonObject
        {
            ["affected"] = Affected.Modified(tpl.Id.Value),
            ["template"] = tpl.Name,
            ["released"] = new JsonArray(released.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["controlled"] = new JsonArray(controlled.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["nowControls"] = new JsonArray(list),
        };
    }
}

/// <summary>
/// Crop a 3D view to its section box, so images and sheets frame just the boxed area (Revit
/// otherwise frames the whole model). Params: viewId, paddingMm? (default 300).
/// </summary>
public sealed class FitCropToSectionBoxCommand : IRevitCommand
{
    public string Name => "fit_3d_crop_to_section_box";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var v = OctaUtil.ResolveView(doc, ctx.Parameters) as View3D
            ?? throw new RevitCommandException("invalid_parameter", "Not a 3D view.");
        if (!v.IsSectionBoxActive) throw new RevitCommandException("invalid_parameter", "The 3D view has no active section box.");
        var pad = P.DblOr(ctx.Parameters, "paddingMm", 300) / 304.8;
        var sb = v.GetSectionBox();
        var crop = v.CropBox;
        var inv = crop.Transform.Inverse;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var x in new[] { sb.Min.X, sb.Max.X })
            foreach (var y in new[] { sb.Min.Y, sb.Max.Y })
                foreach (var z in new[] { sb.Min.Z, sb.Max.Z })
                {
                    var q = inv.OfPoint(sb.Transform.OfPoint(new XYZ(x, y, z)));
                    minX = Math.Min(minX, q.X); minY = Math.Min(minY, q.Y);
                    maxX = Math.Max(maxX, q.X); maxY = Math.Max(maxY, q.Y);
                }
        var bb = new BoundingBoxXYZ
        {
            Transform = crop.Transform,
            Min = new XYZ(minX - pad, minY - pad, crop.Min.Z),
            Max = new XYZ(maxX + pad, maxY + pad, crop.Max.Z),
        };
        v.CropBox = bb;
        v.CropBoxActive = true;
        v.CropBoxVisible = false;
        return new JsonObject { ["affected"] = Affected.Modified(v.Id.Value), ["viewId"] = v.Id.Value, ["cropped"] = true };
    }
}

/// <summary>Set a view's scale (1:N). Params: viewId, scale (N). Fails if a template controls scale.</summary>
public sealed class SetViewScaleCommand : IRevitCommand
{
    public string Name => "set_view_scale";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var view = OctaUtil.ResolveView(doc, ctx.Parameters);
        var scale = P.Int(ctx.Parameters, "scale");
        if (scale < 1) throw new RevitCommandException("invalid_parameter", "scale must be >= 1 (1:N).");
        var prm = view.get_Parameter(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC) ?? view.get_Parameter(BuiltInParameter.VIEW_SCALE);
        if (prm is null || prm.IsReadOnly)
            throw new RevitCommandException("template_controlled", $"'{view.Name}' scale is controlled by its view template.");
        var old = view.Scale;
        view.Scale = scale;
        return new JsonObject { ["affected"] = Affected.Modified(view.Id.Value), ["viewId"] = view.Id.Value, ["from"] = old, ["to"] = view.Scale };
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
