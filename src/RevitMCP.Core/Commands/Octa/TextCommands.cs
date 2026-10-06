using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

internal static class TextUtil
{
    /// <summary>
    /// Add leaders to a note. Each entry: { end:{x,y}, elbow?:{x,y}, side?: "left"|"right", arc?: bool }.
    /// Side defaults to whichever side of the note the end point is on.
    /// </summary>
    public static List<long> AddLeaders(TextNote note, View view, JsonArray leaders, double scale)
    {
        var added = new List<long>();
        var mid = note.Coord;
        for (int i = 0; i < leaders.Count; i++)
        {
            if (leaders[i] is not JsonObject o)
                throw new RevitCommandException("bad_request", $"leaders[{i}] must be an object with 'end'.");
            var end = ViewPlane.Project(view, OctaUtil.PointParam(o, "end", scale));
            var sideStr = P.StrOrNull(o, "side")?.ToLowerInvariant();
            bool right = sideStr is null ? (end - mid).DotProduct(view.RightDirection) > 0 : sideStr == "right";
            bool arc = P.BoolOr(o, "arc", false);
            var kind = arc
                ? (right ? TextNoteLeaderTypes.TNLT_ARC_R : TextNoteLeaderTypes.TNLT_ARC_L)
                : (right ? TextNoteLeaderTypes.TNLT_STRAIGHT_R : TextNoteLeaderTypes.TNLT_STRAIGHT_L);
            var leader = note.AddLeader(kind);
            leader.End = end;
            if (o["elbow"] is JsonObject) leader.Elbow = ViewPlane.Project(view, OctaUtil.PointParam(o, "elbow", scale));
            else if (!arc && P.BoolOr(o, "orthogonal", true)) LeaderGeom.Orthogonalise(view, leader); // OCTA: 90° leaders
            added.Add(i);
        }
        return added;
    }

    public static TextNoteType ResolveType(Document doc, JsonObject p)
    {
        if (p["textTypeId"] is not null || p["textTypeName"] is not null)
            return OctaUtil.ByNameOrId<TextNoteType>(doc, p, "textTypeName", "textTypeId", "Text type");
        return doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType
            ?? new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().First();
    }
}

/// <summary>List text note types with font, size (mm), colour and leader arrowhead.</summary>
public sealed class ListTextNoteTypesCommand : IRevitCommand
{
    public string Name => "list_text_note_types";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var rows = new JsonArray();
        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(t => t.Name))
        {
            var c = t.get_Parameter(BuiltInParameter.LINE_COLOR)?.AsInteger() ?? 0;
            rows.Add(new JsonObject
            {
                ["id"] = t.Id.Value,
                ["name"] = t.Name,
                ["font"] = t.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString(),
                ["size_mm"] = Math.Round((t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0) * 304.8, 2),
                ["color"] = new JsonObject { ["r"] = c & 0xFF, ["g"] = (c >> 8) & 0xFF, ["b"] = (c >> 16) & 0xFF },
                ["arrowhead"] = OctaUtil.PatternName(doc, t.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD)?.AsElementId() ?? ElementId.InvalidElementId),
            });
        }
        return new JsonObject { ["count"] = rows.Count, ["types"] = rows };
    }
}

/// <summary>
/// Create a text note with a chosen type and arrow leaders (instead of drawing lines).
/// Params: text, location {x,y}, viewId?, width? (paper mm, default 60), textTypeName/textTypeId?,
/// align? left|center|right, leaders?: [{ end, elbow?, side?, arc? }], units (coordinates).
/// </summary>
public sealed class CreateAnnotatedNoteCommand : IRevitCommand
{
    public string Name => "create_text_with_leaders";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var scale = OctaUtil.UnitScale(p);
        var view = OctaUtil.ResolveView(doc, p);
        var type = TextUtil.ResolveType(doc, p);
        var loc = ViewPlane.Project(view, OctaUtil.PointParam(p, "location", scale));

        var opts = new TextNoteOptions(type.Id)
        {
            HorizontalAlignment = (P.StrOrNull(p, "align")?.ToLowerInvariant()) switch
            {
                "center" or "centre" => HorizontalTextAlignment.Center,
                "right" => HorizontalTextAlignment.Right,
                _ => HorizontalTextAlignment.Left,
            },
        };
        // Width is paper space: TextNote.Create takes model-independent paper feet.
        var widthFt = P.DblOr(p, "width", 60) / 304.8;
        var note = TextNote.Create(doc, view.Id, loc, widthFt, P.Str(p, "text"), opts);
        var leaders = p["leaders"] is JsonArray la ? TextUtil.AddLeaders(note, view, la, scale) : new List<long>();

        return new JsonObject
        {
            ["affected"] = Affected.Created(note.Id.Value),
            ["id"] = note.Id.Value,
            ["textType"] = type.Name,
            ["leaders"] = leaders.Count,
            ["viewId"] = view.Id.Value,
        };
    }
}

/// <summary>
/// Add, replace or clear leaders on an existing text note.
/// Params: textNoteId, leaders: [{ end, elbow?, side?, arc? }], replace? (remove existing first), units.
/// </summary>
public sealed class SetTextLeadersCommand : IRevitCommand
{
    public string Name => "set_text_leaders";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var scale = OctaUtil.UnitScale(p);
        var note = doc.GetElement(new ElementId(P.Long(p, "textNoteId"))) as TextNote
            ?? throw new RevitCommandException("not_found", "textNoteId is not a text note.");
        var view = doc.GetElement(note.OwnerViewId) as View
            ?? throw new RevitCommandException("not_found", "Text note's view not found.");
        if (P.BoolOr(p, "replace", false)) note.RemoveLeaders();
        var added = p["leaders"] is JsonArray la ? TextUtil.AddLeaders(note, view, la, scale) : new List<long>();
        return new JsonObject
        {
            ["affected"] = Affected.Modified(note.Id.Value),
            ["id"] = note.Id.Value,
            ["leadersAdded"] = added.Count,
            ["leaderCount"] = note.GetLeaders().Count,
        };
    }
}
