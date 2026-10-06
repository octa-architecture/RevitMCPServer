using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

internal static class RevisionUtil
{
    /// <summary>
    /// OCTA stage sequence numbering CODE-01, CODE-02 … Reuses the project's existing sequence
    /// (named after the stage code, e.g. "DD", as in Fairhaven) — never a parallel one, which
    /// would restart the numbers. Creates one named CODE only when none exists.
    /// </summary>
    public static RevisionNumberingSequence EnsureStageSequence(Document doc, string stageCode, out bool created)
    {
        created = false;
        var code = stageCode.Trim().ToUpperInvariant();
        var all = new FilteredElementCollector(doc).OfClass(typeof(RevisionNumberingSequence))
            .Cast<RevisionNumberingSequence>().ToList();
        var seq = all.FirstOrDefault(s => s.Name.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(s => s.Name.Equals($"OCTA {code}", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(s => s.NumberType == RevisionNumberType.Numeric &&
                                       string.Equals(s.GetNumericRevisionSettings().Prefix, code + "-", StringComparison.OrdinalIgnoreCase));
        if (seq is not null) return seq;
        var settings = new NumericRevisionSettings { Prefix = code + "-", MinimumDigits = 2, StartNumber = 1 };
        seq = RevisionNumberingSequence.CreateNumericSequence(doc, code, settings);
        created = true;
        return seq;
    }

    public static Revision Find(Document doc, JsonObject p)
    {
        if (p["revisionId"] is not null)
            return doc.GetElement(new ElementId(P.Long(p, "revisionId"))) as Revision
                ?? throw new RevitCommandException("not_found", "revisionId is not a revision.");
        var number = P.StrOrNull(p, "revisionNumber")
            ?? throw new RevitCommandException("bad_request", "Pass revisionId or revisionNumber (e.g. 'DD-02').");
        return Revision.GetAllRevisionIds(doc).Select(i => (Revision)doc.GetElement(i))
            .FirstOrDefault(r => r.RevisionNumber.Equals(number, StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found", $"No revision numbered '{number}'.");
    }

    public static JsonObject Json(Document doc, Revision r) => new()
    {
        ["id"] = r.Id.Value,
        ["sequence"] = r.SequenceNumber,
        ["number"] = r.RevisionNumber,
        ["date"] = r.RevisionDate,
        ["description"] = r.Description,
        ["issued"] = r.Issued,
        ["issuedBy"] = r.IssuedBy,
        ["issuedTo"] = r.IssuedTo,
        ["numbering"] = doc.GetElement(r.RevisionNumberingSequenceId)?.Name,
        ["visibility"] = r.Visibility.ToString(),
    };
}

/// <summary>List revisions in order, with numbering sequence and the sheets each appears on.</summary>
public sealed class ListRevisionsCommand : IRevitCommand
{
    public string Name => "list_revisions";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        var rows = new JsonArray();
        foreach (var id in Revision.GetAllRevisionIds(doc))
        {
            var r = (Revision)doc.GetElement(id);
            var o = RevisionUtil.Json(doc, r);
            o["sheets"] = new JsonArray(sheets.Where(s => s.GetAllRevisionIds().Contains(id))
                .Select(s => (JsonNode)JsonValue.Create(s.SheetNumber)!).ToArray());
            rows.Add(o);
        }
        return new JsonObject { ["count"] = rows.Count, ["revisions"] = rows };
    }
}

/// <summary>
/// Create a revision in an OCTA stage sequence (numbered CODE-NN, e.g. DD-02). The project's
/// sequence named CODE is reused, or created if missing. Params: stageCode (SD|TP|DD|BP|TD|FC),
/// description, date (text as shown on sheets, OCTA format dd/mm/yyyy), issuedBy?, issuedTo?,
/// issued? (default false).
/// </summary>
public sealed class CreateRevisionCommand : IRevitCommand
{
    public string Name => "create_revision";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var seq = RevisionUtil.EnsureStageSequence(doc, P.Str(p, "stageCode"), out var seqCreated);
        var r = Revision.Create(doc);
        r.RevisionNumberingSequenceId = seq.Id;
        r.Description = P.Str(p, "description");
        r.RevisionDate = P.Str(p, "date");
        if (P.StrOrNull(p, "issuedBy") is { } by) r.IssuedBy = by;
        if (P.StrOrNull(p, "issuedTo") is { } to) r.IssuedTo = to;
        doc.Regenerate();
        if (P.BoolOr(p, "issued", false)) r.Issued = true;
        var created = new List<long> { r.Id.Value };
        if (seqCreated) created.Add(seq.Id.Value);
        var o = RevisionUtil.Json(doc, r);
        o["affected"] = Affected.Created(created);
        o["sequenceCreated"] = seqCreated;
        return o;
    }
}

/// <summary>
/// Update a revision: description, date, issuedBy, issuedTo, issued (true locks it).
/// Params: revisionId or revisionNumber, plus fields to change.
/// </summary>
public sealed class UpdateRevisionCommand : IRevitCommand
{
    public string Name => "update_revision";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var r = RevisionUtil.Find(doc, p);
        var issued = p["issued"] is null ? (bool?)null : P.BoolOr(p, "issued", false);
        if (r.Issued && issued != false &&
            (p["description"] is not null || p["date"] is not null || p["issuedBy"] is not null || p["issuedTo"] is not null))
            throw new RevitCommandException("read_only_parameter", $"Revision {r.RevisionNumber} is issued (locked). Pass issued=false to unlock it first.");
        if (issued == false) r.Issued = false;
        if (P.StrOrNull(p, "description") is { } d) r.Description = d;
        if (P.StrOrNull(p, "date") is { } dt) r.RevisionDate = dt;
        if (P.StrOrNull(p, "issuedBy") is { } by) r.IssuedBy = by;
        if (P.StrOrNull(p, "issuedTo") is { } to) r.IssuedTo = to;
        if (issued == true) r.Issued = true;
        var o = RevisionUtil.Json(doc, r);
        o["affected"] = Affected.Modified(r.Id.Value);
        return o;
    }
}

/// <summary>
/// Add or remove a revision on sheets (the sheet's "Revisions on Sheet" list).
/// Params: revisionId or revisionNumber, sheetNumbers (e.g. ["AR-20-01"]) or sheetIds, remove? (default false).
/// Revisions carried by clouds on the sheet can't be removed this way and are reported.
/// </summary>
public sealed class SetSheetRevisionsCommand : IRevitCommand
{
    public string Name => "set_sheet_revisions";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var rev = RevisionUtil.Find(doc, p);
        var remove = P.BoolOr(p, "remove", false);
        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();

        var targets = new List<ViewSheet>();
        if (p["sheetNumbers"] is JsonArray nums)
            foreach (var n in nums)
            {
                var s = P.StrFrom(n, "sheetNumbers[]");
                targets.Add(all.FirstOrDefault(x => x.SheetNumber.Equals(s, StringComparison.OrdinalIgnoreCase))
                    ?? throw new RevitCommandException("not_found", $"Sheet '{s}' not found."));
            }
        if (p["sheetIds"] is JsonArray)
            foreach (var id in OctaUtil.Ids(p, "sheetIds"))
                targets.Add(doc.GetElement(id) as ViewSheet ?? throw new RevitCommandException("not_found", $"{id.Value} is not a sheet."));
        if (targets.Count == 0) throw new RevitCommandException("bad_request", "Pass sheetNumbers or sheetIds.");

        var changed = new List<long>();
        var notes = new JsonArray();
        foreach (var s in targets.Distinct())
        {
            var ids = s.GetAdditionalRevisionIds().ToList();
            bool has = ids.Contains(rev.Id);
            if (!remove && !has) ids.Add(rev.Id);
            else if (remove && has) ids.Remove(rev.Id);
            else continue;
            s.SetAdditionalRevisionIds(ids);
            changed.Add(s.Id.Value);
            if (remove && s.GetAllRevisionIds().Contains(rev.Id))
                notes.Add($"{s.SheetNumber}: still shows {rev.RevisionNumber} because a cloud on the sheet carries it.");
        }
        return new JsonObject
        {
            ["affected"] = Affected.Modified(changed),
            ["revision"] = rev.RevisionNumber,
            ["sheetsChanged"] = changed.Count,
            ["notes"] = notes,
        };
    }
}

/// <summary>
/// Draw a revision cloud around an area in a view or sheet. Params: viewId?, revisionId or
/// revisionNumber, boundary [{x,y}…] (≥3 points, closed automatically), units.
/// </summary>
public sealed class CreateRevisionCloudCommand : IRevitCommand
{
    public string Name => "create_revision_cloud";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var view = OctaUtil.ResolveView(doc, p);
        var rev = RevisionUtil.Find(doc, p);
        if (rev.Issued)
            throw new RevitCommandException("read_only_parameter", $"Revision {rev.RevisionNumber} is issued; clouds can't be added to it.");
        var scale = OctaUtil.UnitScale(p);
        var arr = P.Arr(p, "boundary");
        if (arr.Count < 3) throw new RevitCommandException("bad_request", "'boundary' needs at least 3 points.");
        var pts = arr.Select((n, i) => ViewPlane.Project(view,
            OctaUtil.Point(n as JsonObject ?? throw new RevitCommandException("bad_request", $"boundary[{i}] must be {{x,y}}."), scale))).ToList();
        var curves = new List<Curve>();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (a.DistanceTo(b) > 1e-6) curves.Add(Line.CreateBound(a, b));
        }
        var cloud = RevisionCloud.Create(doc, view, rev.Id, curves);
        return new JsonObject
        {
            ["affected"] = Affected.Created(cloud.Id.Value),
            ["id"] = cloud.Id.Value,
            ["revision"] = rev.RevisionNumber,
            ["viewId"] = view.Id.Value,
        };
    }
}
