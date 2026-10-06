using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Shared helpers for the OCTA detailing commands (fills, detail components,
/// text leaders, graphics, internal notes). Kept apart from upstream files so
/// upstream merges stay clean.
/// </summary>
public static class OctaUtil
{
    /// <summary>OCTA convention: magenta means "internal / needs review — not for issue".</summary>
    public static readonly Color FlagColor = new(255, 0, 255);
    public const string InternalNoteTypeName = "OCTA - INTERNAL NOTE";

    /// <summary>Scale from the request's units to Revit feet. Accepts meters (default), millimeters, feet.</summary>
    public static double UnitScale(JsonObject p)
    {
        var u = P.StrOrNull(p, "units")?.Trim().ToLowerInvariant() ?? "meters";
        return u switch
        {
            "feet" or "ft" => 1.0,
            "millimeters" or "millimetres" or "mm" => 1.0 / 304.8,
            "meters" or "metres" or "m" => P.MetersToFeet,
            _ => throw new RevitCommandException("invalid_parameter",
                $"'units' must be meters, millimeters or feet, got '{u}'."),
        };
    }

    public static XYZ Point(JsonObject o, double scale) =>
        new(P.DblOr(o, "x", 0) * scale, P.DblOr(o, "y", 0) * scale, P.DblOr(o, "z", 0) * scale);

    public static XYZ PointParam(JsonObject p, string key, double scale) => Point(P.Obj(p, key), scale);

    public static View ResolveView(Document doc, JsonObject p, string key = "viewId")
    {
        if (p[key] is null)
            return doc.ActiveView ?? throw new RevitCommandException("not_found", "No active view.");
        var id = P.Long(p, key);
        return doc.GetElement(new ElementId(id)) as View
            ?? throw new RevitCommandException("not_found", $"View {id} not found.");
    }

    public static Color? ColorOrNull(JsonObject p, string key)
    {
        if (p[key] is not JsonObject c) return null;
        return new Color(P.ColorByte(c, "r", 0), P.ColorByte(c, "g", 0), P.ColorByte(c, "b", 0));
    }

    public static JsonObject ColorJson(Color? c) =>
        c is null || !c.IsValid
            ? new JsonObject()
            : new JsonObject { ["r"] = c.Red, ["g"] = c.Green, ["b"] = c.Blue };

    /// <summary>Revit stores text/line colours as an int: r + g·256 + b·65536.</summary>
    public static int ColorToInt(Color c) => c.Red + (c.Green << 8) + (c.Blue << 16);

    public static ElementId SolidFillId(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id
        ?? throw new RevitCommandException("not_found", "No solid fill pattern in the document.");

    /// <summary>Fill pattern by name ("<Solid fill>" or "solid" for solid). Drafting patterns win ties.</summary>
    public static ElementId FillPatternId(Document doc, string name)
    {
        if (name.Equals("solid", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("<Solid fill>", StringComparison.OrdinalIgnoreCase))
            return SolidFillId(doc);
        var matches = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.GetFillPattern().Target == FillPatternTarget.Drafting ? 0 : 1)
            .ToList();
        return matches.FirstOrDefault()?.Id
            ?? throw new RevitCommandException("not_found",
                $"Fill pattern '{name}' not found. Use list_fill_patterns to see what's loaded.");
    }

    public static ElementId LinePatternId(Document doc, string name)
    {
        if (name.Equals("solid", StringComparison.OrdinalIgnoreCase))
            return LinePatternElement.GetSolidPatternId();
        return LinePatternElement.GetLinePatternElementByName(doc, name)?.Id
            ?? throw new RevitCommandException("not_found", $"Line pattern '{name}' not found.");
    }

    public static string PatternName(Document doc, ElementId id) =>
        id == ElementId.InvalidElementId ? "" : doc.GetElement(id)?.Name ?? "";

    public static T ByNameOrId<T>(Document doc, JsonObject p, string nameKey, string idKey, string what)
        where T : Element
    {
        if (p[idKey] is not null)
        {
            var id = P.Long(p, idKey);
            return doc.GetElement(new ElementId(id)) as T
                ?? throw new RevitCommandException("not_found", $"{what} {id} not found.");
        }
        var name = P.StrOrNull(p, nameKey)
            ?? throw new RevitCommandException("bad_request", $"Pass '{nameKey}' or '{idKey}'.");
        return new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
            .FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found", $"{what} '{name}' not found.");
    }

    public static List<ElementId> Ids(JsonObject p, string key)
    {
        var arr = P.Arr(p, key);
        var ids = new List<ElementId>(arr.Count);
        for (int i = 0; i < arr.Count; i++) ids.Add(new ElementId(P.LongFrom(arr[i], $"{key}[{i}]")));
        return ids;
    }

    public static Category ResolveCategory(Document doc, string nameOrOst)
    {
        if (nameOrOst.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<BuiltInCategory>(nameOrOst, true, out var bic))
            return Category.GetCategory(doc, bic)
                ?? throw new RevitCommandException("not_found", $"Category {nameOrOst} isn't available in this document.");
        foreach (Category c in doc.Settings.Categories)
            if (c.Name.Equals(nameOrOst, StringComparison.OrdinalIgnoreCase)) return c;
        throw new RevitCommandException("not_found",
            $"Category '{nameOrOst}' not found. Use a display name ('Walls') or OST name ('OST_Walls').");
    }

    /// <summary>
    /// Build OverrideGraphicSettings from a request object. Keys (all optional):
    /// halftone, transparency, projectionLineColor, projectionLineWeight, projectionLinePattern,
    /// cutLineColor, cutLineWeight, cutLinePattern, surfacePattern, surfaceColor,
    /// cutPattern, cutColor. Colours are {r,g,b}; patterns are names.
    /// </summary>
    public static OverrideGraphicSettings BuildOverrides(Document doc, JsonObject o)
    {
        var ogs = new OverrideGraphicSettings();
        if (o["halftone"] is not null) ogs.SetHalftone(P.BoolOr(o, "halftone", false));
        if (o["transparency"] is not null) ogs.SetSurfaceTransparency(Math.Clamp(P.Int(o, "transparency"), 0, 100));

        if (ColorOrNull(o, "projectionLineColor") is { } plc) ogs.SetProjectionLineColor(plc);
        if (o["projectionLineWeight"] is not null) ogs.SetProjectionLineWeight(Math.Clamp(P.Int(o, "projectionLineWeight"), 1, 16));
        if (P.StrOrNull(o, "projectionLinePattern") is { } plp) ogs.SetProjectionLinePatternId(LinePatternId(doc, plp));

        if (ColorOrNull(o, "cutLineColor") is { } clc) ogs.SetCutLineColor(clc);
        if (o["cutLineWeight"] is not null) ogs.SetCutLineWeight(Math.Clamp(P.Int(o, "cutLineWeight"), 1, 16));
        if (P.StrOrNull(o, "cutLinePattern") is { } clp) ogs.SetCutLinePatternId(LinePatternId(doc, clp));

        if (P.StrOrNull(o, "surfacePattern") is { } sp)
        {
            ogs.SetSurfaceForegroundPatternId(FillPatternId(doc, sp));
            ogs.SetSurfaceForegroundPatternVisible(true);
        }
        if (ColorOrNull(o, "surfaceColor") is { } sc) ogs.SetSurfaceForegroundPatternColor(sc);

        if (P.StrOrNull(o, "cutPattern") is { } cp)
        {
            ogs.SetCutForegroundPatternId(FillPatternId(doc, cp));
            ogs.SetCutForegroundPatternVisible(true);
        }
        if (ColorOrNull(o, "cutColor") is { } cc) ogs.SetCutForegroundPatternColor(cc);
        return ogs;
    }

    /// <summary>Set a named parameter from JSON. Length parameters take millimetres.</summary>
    public static void SetParam(Element el, string name, JsonNode? value)
    {
        var prm = el.LookupParameter(name)
            ?? throw new RevitCommandException("not_found", $"'{el.Name}' has no parameter '{name}'.");
        if (prm.IsReadOnly)
            throw new RevitCommandException("read_only_parameter", $"Parameter '{name}' is read-only.");
        switch (prm.StorageType)
        {
            case StorageType.Double:
                var d = P.DblFrom(value, name);
                prm.Set(prm.Definition.GetDataType() == SpecTypeId.Length ? d / 304.8 : d);
                break;
            case StorageType.Integer:
                prm.Set(value is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? 1 : 0) : P.IntFrom(value, name));
                break;
            case StorageType.String:
                prm.Set(P.StrFrom(value, name));
                break;
            case StorageType.ElementId:
                prm.Set(new ElementId(P.LongFrom(value, name)));
                break;
            default:
                throw new RevitCommandException("invalid_parameter", $"Parameter '{name}' can't be set.");
        }
    }

    public static JsonObject ParamsJson(Element el, IEnumerable<string>? only = null)
    {
        var o = new JsonObject();
        var filter = only?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Parameter prm in el.Parameters)
        {
            if (prm.Definition is null) continue;
            var n = prm.Definition.Name;
            if (filter is not null && !filter.Contains(n)) continue;
            if (filter is null && prm.Definition.GetDataType() != SpecTypeId.Length) continue;
            o[n] = prm.StorageType == StorageType.Double && prm.Definition.GetDataType() == SpecTypeId.Length
                ? Math.Round(prm.AsDouble() * 304.8, 2)
                : prm.AsValueString() ?? prm.AsString();
        }
        return o;
    }
}
