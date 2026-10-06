using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>List open documents (title, path, modified, active, family/project).</summary>
public sealed class ListOpenDocumentsCommand : IRevitCommand
{
    public string Name => "list_open_documents";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var active = ctx.App.ActiveUIDocument?.Document;
        var rows = new JsonArray();
        foreach (Document d in ctx.App.Application.Documents)
        {
            if (d.IsLinked) continue;
            rows.Add(new JsonObject
            {
                ["title"] = d.Title,
                ["path"] = d.PathName,
                ["isModified"] = d.IsModified,
                ["isActive"] = active is not null && d.Equals(active),
                ["isFamily"] = d.IsFamilyDocument,
                ["isWorkshared"] = d.IsWorkshared,
            });
        }
        return new JsonObject { ["count"] = rows.Count, ["documents"] = rows };
    }
}

/// <summary>
/// Open a model (or family) file and make it active. Params: path (required), audit? (default false).
/// Harmless open-time dialogs (e.g. unresolved CAD links) are answered by the add-in's dialog rules.
/// </summary>
public sealed class OpenDocumentCommand : IRevitCommand
{
    public string Name => "open_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var path = P.Str(ctx.Parameters, "path");
        if (!File.Exists(path))
            throw new RevitCommandException("not_found", $"File not found: {path}");
        var opts = new OpenOptions { Audit = P.BoolOr(ctx.Parameters, "audit", false) };
        var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        var uidoc = ctx.App.OpenAndActivateDocument(mp, opts, false);
        return new JsonObject
        {
            ["title"] = uidoc.Document.Title,
            ["path"] = uidoc.Document.PathName,
            ["isFamily"] = uidoc.Document.IsFamilyDocument,
        };
    }
}

/// <summary>
/// Save the active document, or save it as a new file. Params: saveAsPath? (new .rvt path),
/// overwrite? (false). Ask the user before saving a live project model; test models are fine.
/// </summary>
public sealed class SaveDocumentCommand : IRevitCommand
{
    public string Name => "save_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "high";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.App.ActiveUIDocument?.Document
            ?? throw new RevitCommandException("not_found", "No active document.");
        if (doc.IsWorkshared)
            throw new RevitCommandException("unsupported", $"'{doc.Title}' is workshared; synchronise it in Revit.");
        if (P.StrOrNull(ctx.Parameters, "saveAsPath") is { } path)
        {
            if (File.Exists(path) && !P.BoolOr(ctx.Parameters, "overwrite", false))
                throw new RevitCommandException("conflict", $"{path} exists; pass overwrite=true to replace it.");
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        }
        else
        {
            if (string.IsNullOrEmpty(doc.PathName))
                throw new RevitCommandException("bad_request", $"'{doc.Title}' has never been saved; pass saveAsPath.");
            doc.Save();
        }
        return new JsonObject { ["saved"] = true, ["title"] = doc.Title, ["path"] = doc.PathName };
    }
}

/// <summary>
/// Save (optionally) and exit Revit cleanly. Refuses when a document has unsaved changes and
/// save=false, so work is never thrown away silently. Params: save (bool, required).
/// Revit exits just after this call returns.
/// </summary>
public sealed class ExitRevitCommand : IRevitCommand
{
    public string Name => "exit_revit";
    public bool IsReadOnly => false;
    public string RiskLevel => "high";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        if (ctx.Parameters["save"] is null)
            throw new RevitCommandException("bad_request", "Pass save=true (save modified documents) or save=false.");
        var save = P.BoolOr(ctx.Parameters, "save", false);

        var docs = ctx.App.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
        var dirty = docs.Where(d => d.IsModified).ToList();
        var saved = new JsonArray();

        if (dirty.Count > 0 && !save)
            throw new RevitCommandException("unsaved_changes",
                "These documents have unsaved changes: " + string.Join(", ", dirty.Select(d => d.Title)) +
                ". Ask the user, then call again with save=true.");

        foreach (var d in dirty)
        {
            if (string.IsNullOrEmpty(d.PathName))
                throw new RevitCommandException("unsaved_changes",
                    $"'{d.Title}' has never been saved, so it can't be saved automatically. Save it in Revit first.");
            if (d.IsWorkshared)
                throw new RevitCommandException("unsupported",
                    $"'{d.Title}' is workshared; synchronise it in Revit before exiting.");
            d.Save();
            saved.Add(d.Title);
        }

        var exitId = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
        ctx.App.PostCommand(exitId);
        return new JsonObject
        {
            ["exiting"] = true,
            ["saved"] = saved,
            ["openDocuments"] = new JsonArray(docs.Select(d => (JsonNode)JsonValue.Create(d.PathName)!).ToArray()),
        };
    }
}
