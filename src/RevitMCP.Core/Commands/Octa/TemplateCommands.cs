using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

/// <summary>
/// Copy standards from another model into the active one (building the OCTA template, or bringing a
/// job up to standard). The source is opened in the background if it isn't open, and is never
/// saved. Anything whose name already exists in the destination is skipped, so it is safe to rerun.
/// Params: sourcePath, and any of:
///   viewTemplates: ["name", ...] | "all"      filledRegionTypes: [..] | "all"
///   textNoteTypes: [..] | "all"               dimensionTypes: [..] | "all"
///   families: ["family name", ...]            familyCategories: ["Detail Items", "Generic Annotations", ...]
///   views: ["drafting/legend/schedule view name", ...]
///   dryRun-style preview: preview=true lists what would be copied without copying.
/// </summary>
public sealed class CopyFromDocumentCommand : IRevitCommand
{
    public string Name => "copy_from_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var dest = ctx.RequireDoc();
        var p = ctx.Parameters;
        var path = P.Str(p, "sourcePath");
        var app = ctx.App.Application;
        var src = app.Documents.Cast<Document>().FirstOrDefault(d =>
            string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
        var openedHere = false;
        if (src is null)
        {
            if (!File.Exists(path)) throw new RevitCommandException("not_found", $"File not found: {path}");
            var opts = new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DoNotDetach };
            src = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), opts);
            openedHere = true;
        }
        if (src.Equals(dest)) throw new RevitCommandException("bad_request", "Source and destination are the same document.");

        var groups = new List<(string Group, List<ElementId> Ids, List<string> Names, List<string> Skipped)>();
        void Pick<T>(string key, string group, Func<T, bool>? filter = null) where T : Element
        {
            if (p[key] is null) return;
            var all = p[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Equals("all", StringComparison.OrdinalIgnoreCase);
            var wanted = all ? null : new HashSet<string>(P.Arr(p, key).Select(n => n!.GetValue<string>()), StringComparer.OrdinalIgnoreCase);
            var existing = new HashSet<string>(new FilteredElementCollector(dest).OfClass(typeof(T)).Cast<T>()
                .Where(e => filter?.Invoke(e) ?? true).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            var ids = new List<ElementId>(); var names = new List<string>(); var skipped = new List<string>();
            foreach (var e in new FilteredElementCollector(src).OfClass(typeof(T)).Cast<T>().Where(e => filter?.Invoke(e) ?? true))
            {
                if (wanted is not null && !wanted.Contains(e.Name)) continue;
                if (existing.Contains(e.Name)) { skipped.Add(e.Name); continue; }
                ids.Add(e.Id); names.Add(e.Name);
            }
            if (wanted is not null)
                foreach (var w in wanted.Where(w => !names.Contains(w, StringComparer.OrdinalIgnoreCase) && !skipped.Contains(w, StringComparer.OrdinalIgnoreCase)))
                    skipped.Add(w + " (not in source)");
            groups.Add((group, ids, names, skipped));
        }

        Pick<View>("viewTemplates", "view templates", v => v.IsTemplate);
        Pick<FilledRegionType>("filledRegionTypes", "filled region types");
        Pick<TextNoteType>("textNoteTypes", "text types");
        Pick<DimensionType>("dimensionTypes", "dimension types");
        Pick<Family>("families", "families");
        Pick<View>("views", "views", v => !v.IsTemplate && v.ViewType is ViewType.DraftingView or ViewType.Legend or ViewType.Schedule);

        if (p["familyCategories"] is JsonArray cats)
        {
            var catNames = new HashSet<string>(cats.Select(c => c!.GetValue<string>()), StringComparer.OrdinalIgnoreCase);
            var existing = new HashSet<string>(new FilteredElementCollector(dest).OfClass(typeof(Family)).Cast<Family>()
                .Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var ids = new List<ElementId>(); var names = new List<string>(); var skipped = new List<string>();
            foreach (var f in new FilteredElementCollector(src).OfClass(typeof(Family)).Cast<Family>()
                         .Where(f => f.FamilyCategory is not null && catNames.Contains(f.FamilyCategory.Name) && f.IsEditable))
            {
                if (existing.Contains(f.Name)) { skipped.Add(f.Name); continue; }
                // Copy the family's types: copying a FamilySymbol brings its family with it.
                var sym = f.GetFamilySymbolIds().FirstOrDefault();
                if (sym is null || sym == ElementId.InvalidElementId) continue;
                ids.AddRange(f.GetFamilySymbolIds()); names.Add($"{f.Name} [{f.FamilyCategory!.Name}]");
            }
            groups.Add(("families by category", ids, names, skipped));
        }

        var result = new JsonObject { ["source"] = src.Title, ["openedSource"] = openedHere };
        var report = new JsonArray();
        var created = new List<long>();
        var preview = P.BoolOr(p, "preview", false) || ctx.DryRun;
        foreach (var (group, ids, names, skipped) in groups)
        {
            var row = new JsonObject
            {
                ["group"] = group, ["toCopy"] = names.Count,
                ["names"] = new JsonArray(names.Take(80).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                ["skipped"] = new JsonArray(skipped.Take(40).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            };
            if (!preview && ids.Count > 0)
            {
                var failed = new JsonArray();
                // Views copy one at a time so a single unsupported view doesn't sink the rest.
                var batches = group == "views" ? ids.Select(i => new List<ElementId> { i }).ToList() : new List<List<ElementId>> { ids };
                foreach (var batch in batches)
                {
                    using var tx = new Transaction(dest, $"MCP: copy {group} from {src.Title}");
                    var fo = tx.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new QuietFailures());
                    tx.SetFailureHandlingOptions(fo);
                    try
                    {
                        tx.Start();
                        var opts = new CopyPasteOptions();
                        opts.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                        var newIds = ElementTransformUtils.CopyElements(src, batch, dest, Transform.Identity, opts);
                        tx.Commit();
                        created.AddRange(newIds.Select(i => i.Value));
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        failed.Add($"{string.Join(",", batch.Select(i => src.GetElement(i)?.Name))}: {ex.Message}");
                    }
                }
                row["failed"] = failed;
            }
            report.Add(row);
        }
        result["groups"] = report;
        result["preview"] = preview;
        result["affected"] = Affected.Created(created);
        if (openedHere && P.BoolOr(p, "closeSource", true)) { src.Close(false); result["closedSource"] = true; }
        return result;
    }

    private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }
}

/// <summary>Dismisses warnings during unattended copies/purges so no dialog blocks the session.</summary>
internal sealed class QuietFailures : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
    {
        foreach (var f in fa.GetFailureMessages())
            if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
        return FailureProcessingResult.Continue;
    }
}

/// <summary>
/// Purge unused content (what Manage > Purge Unused would remove), in passes. Title blocks,
/// annotation families, detail items, view templates and anything in keepCategories are always
/// kept, because a template needs them even though nothing uses them yet.
/// Params: apply? (false = preview only), keepCategories? [names], keepNames? [names], passes? (3).
/// </summary>
public sealed class PurgeUnusedCommand : IRevitCommand
{
    public string Name => "purge_unused";
    public bool IsReadOnly => false;
    public string RiskLevel => "high";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var apply = P.BoolOr(p, "apply", false);
        var keepCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Title Blocks", "Detail Items", "Profiles" };
        if (p["keepCategories"] is JsonArray kc) foreach (var c in kc) keepCats.Add(c!.GetValue<string>());
        var keepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (p["keepNames"] is JsonArray kn) foreach (var c in kn) keepNames.Add(c!.GetValue<string>());

        bool Keep(Element e)
        {
            if (keepNames.Contains(e.Name)) return true;
            if (e is View) return true;
            var cat = e is Family f ? f.FamilyCategory : e.Category;
            if (cat is null) return false;
            if (cat.CategoryType == CategoryType.Annotation) return true;
            return keepCats.Contains(cat.Name);
        }

        var byGroup = new SortedDictionary<string, int>();
        var deleted = new List<long>();
        int passes = apply ? P.IntOr(p, "passes", 3) : 1;
        for (int i = 0; i < passes; i++)
        {
            var unused = doc.GetUnusedElements(new HashSet<ElementId>())
                .Select(doc.GetElement).Where(e => e is not null && !Keep(e!)).ToList();
            if (unused.Count == 0) break;
            foreach (var e in unused)
            {
                var cat = (e is Family f ? f.FamilyCategory : e!.Category)?.Name ?? e!.GetType().Name;
                byGroup[cat] = byGroup.GetValueOrDefault(cat) + 1;
            }
            if (!apply) break;
            foreach (var e in unused)
            {
                try { if (doc.GetElement(e!.Id) is not null) { doc.Delete(e.Id); deleted.Add(e.Id.Value); } }
                catch { /* some elements refuse deletion; leave them */ }
            }
        }
        var groups = new JsonObject();
        foreach (var kv in byGroup) groups[kv.Key] = kv.Value;
        return new JsonObject
        {
            ["applied"] = apply, ["byCategory"] = groups, ["deleted"] = deleted.Count,
            ["affected"] = Affected.Deleted(deleted),
        };
    }
}

/// <summary>Close an open (non-active) document without saving. Params: path or title.</summary>
public sealed class CloseDocumentCommand : IRevitCommand
{
    public string Name => "close_document";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var key = P.StrOrNull(ctx.Parameters, "path") ?? P.Str(ctx.Parameters, "title");
        var doc = ctx.App.Application.Documents.Cast<Document>().FirstOrDefault(d =>
            string.Equals(d.PathName, key, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Title, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new RevitCommandException("not_found", $"No open document '{key}'.");
        if (ctx.App.ActiveUIDocument?.Document?.Equals(doc) == true)
            throw new RevitCommandException("bad_request", "That's the active document; Revit can't close it through the API. Switch documents first.");
        var title = doc.Title;
        doc.Close(false);
        return new JsonObject { ["closed"] = title, ["saved"] = false };
    }
}
