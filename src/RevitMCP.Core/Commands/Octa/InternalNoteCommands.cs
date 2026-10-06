using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

internal static class InternalNotes
{
    public static TextNoteType? FindType(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
            .FirstOrDefault(t => t.Name.Equals(OctaUtil.InternalNoteTypeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Find or create the magenta internal-note text type (Arial 2.5 mm, opaque background).</summary>
    public static TextNoteType EnsureType(Document doc, out bool created)
    {
        created = false;
        var t = FindType(doc);
        if (t is not null) return t;
        var baseType = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType
            ?? new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().First();
        t = (TextNoteType)baseType.Duplicate(OctaUtil.InternalNoteTypeName);
        t.get_Parameter(BuiltInParameter.LINE_COLOR)?.Set(OctaUtil.ColorToInt(OctaUtil.FlagColor));
        t.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("Arial");
        t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(2.5 / 304.8);
        t.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(0); // opaque, so it reads over linework
        created = true;
        return t;
    }

    public static List<TextNote> All(Document doc)
    {
        var type = FindType(doc);
        if (type is null) return new List<TextNote>();
        return new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>()
            .Where(n => n.GetTypeId() == type.Id).ToList();
    }
}

/// <summary>
/// Add an internal office note (magenta "OCTA - INTERNAL NOTE" type) — for review comments that
/// must not go out on issued drawings. Params: text, location, viewId?, leaders?, width?, units.
/// </summary>
public sealed class AddInternalNoteCommand : IRevitCommand
{
    public string Name => "octa_add_internal_note";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var scale = OctaUtil.UnitScale(p);
        var view = OctaUtil.ResolveView(doc, p);
        var type = InternalNotes.EnsureType(doc, out var typeCreated);
        var loc = ViewPlane.Project(view, OctaUtil.PointParam(p, "location", scale));
        var text = P.Str(p, "text");
        var note = TextNote.Create(doc, view.Id, loc, P.DblOr(p, "width", 70) / 304.8, text, type.Id);
        var leaders = p["leaders"] is JsonArray la ? TextUtil.AddLeaders(note, view, la, scale) : new List<long>();
        var created = new List<long> { note.Id.Value };
        if (typeCreated) created.Add(type.Id.Value);
        return new JsonObject
        {
            ["affected"] = Affected.Created(created),
            ["id"] = note.Id.Value,
            ["viewId"] = view.Id.Value,
            ["leaders"] = leaders.Count,
            ["typeCreated"] = typeCreated,
        };
    }
}

/// <summary>List every internal note in the model with its view and text. Params: viewId? to filter.</summary>
public sealed class ListInternalNotesCommand : IRevitCommand
{
    public string Name => "octa_list_internal_notes";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var viewFilter = P.LongOrNull(ctx.Parameters, "viewId");
        var rows = new JsonArray();
        foreach (var n in InternalNotes.All(doc))
        {
            if (viewFilter is not null && n.OwnerViewId.Value != viewFilter) continue;
            var v = doc.GetElement(n.OwnerViewId) as View;
            rows.Add(new JsonObject
            {
                ["id"] = n.Id.Value,
                ["text"] = n.Text.Trim(),
                ["viewId"] = n.OwnerViewId.Value,
                ["viewName"] = v?.Name,
                ["hidden"] = v is not null && n.IsHidden(v),
            });
        }
        return new JsonObject { ["count"] = rows.Count, ["notes"] = rows };
    }
}

/// <summary>
/// Hide or show all internal notes (e.g. hide before issuing/printing, show after).
/// Params: visible (bool, required), viewIds? (default: every view that has internal notes).
/// </summary>
public sealed class SetInternalNotesVisibilityCommand : IRevitCommand
{
    public string Name => "octa_set_internal_notes_visibility";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var visible = P.BoolOr(p, "visible", true);
        if (p["visible"] is null) throw new RevitCommandException("bad_request", "Missing required parameter 'visible'.");
        HashSet<long>? only = p["viewIds"] is JsonArray ? OctaUtil.Ids(p, "viewIds").Select(i => i.Value).ToHashSet() : null;

        int changed = 0;
        var views = new List<long>();
        foreach (var g in InternalNotes.All(doc).GroupBy(n => n.OwnerViewId))
        {
            if (only is not null && !only.Contains(g.Key.Value)) continue;
            if (doc.GetElement(g.Key) is not View v) continue;
            var ids = g.Where(n => n.IsHidden(v) == visible).Select(n => n.Id).ToList();
            if (ids.Count == 0) continue;
            if (visible) v.UnhideElements(ids); else v.HideElements(ids);
            changed += ids.Count;
            views.Add(v.Id.Value);
        }
        return new JsonObject
        {
            ["affected"] = Affected.Modified(views),
            ["visible"] = visible,
            ["notesChanged"] = changed,
            ["views"] = views.Count,
        };
    }
}
