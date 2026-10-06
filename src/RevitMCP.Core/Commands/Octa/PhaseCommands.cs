using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands.Octa;

internal static class PhaseUtil
{
    public static Phase Find(Document doc, string nameOrId)
    {
        foreach (Phase ph in doc.Phases)
            if (ph.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) || ph.Id.Value.ToString() == nameOrId)
                return ph;
        throw new RevitCommandException("not_found",
            $"Phase '{nameOrId}' not found. Phases: {string.Join(", ", doc.Phases.Cast<Phase>().Select(p => p.Name))}");
    }

    public static PhaseFilter FindFilter(Document doc, string name) =>
        new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new RevitCommandException("not_found", $"Phase filter '{name}' not found. Use list_phase_filters.");

    public static string Presentation(PhaseFilter f, ElementOnPhaseStatus s) =>
        f.GetPhaseStatusPresentation(s) switch
        {
            PhaseStatusPresentation.ShowByCategory => "by_category",
            PhaseStatusPresentation.ShowOverriden => "overridden",
            _ => "not_displayed",
        };

    public static PhaseStatusPresentation ParsePresentation(string v) => v.ToLowerInvariant() switch
    {
        "by_category" or "bycategory" or "show" => PhaseStatusPresentation.ShowByCategory,
        "overridden" or "overriden" => PhaseStatusPresentation.ShowOverriden,
        "not_displayed" or "hidden" or "hide" => PhaseStatusPresentation.DontShow,
        _ => throw new RevitCommandException("invalid_parameter",
            $"Presentation must be by_category, overridden or not_displayed, got '{v}'."),
    };
}

/// <summary>List phase filters with how each shows New / Existing / Demolished / Temporary.</summary>
public sealed class ListPhaseFiltersCommand : IRevitCommand
{
    public string Name => "list_phase_filters";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var rows = new JsonArray();
        foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().OrderBy(f => f.Name))
            rows.Add(new JsonObject
            {
                ["id"] = f.Id.Value,
                ["name"] = f.Name,
                ["new"] = PhaseUtil.Presentation(f, ElementOnPhaseStatus.New),
                ["existing"] = PhaseUtil.Presentation(f, ElementOnPhaseStatus.Existing),
                ["demolished"] = PhaseUtil.Presentation(f, ElementOnPhaseStatus.Demolished),
                ["temporary"] = PhaseUtil.Presentation(f, ElementOnPhaseStatus.Temporary),
            });
        return new JsonObject { ["count"] = rows.Count, ["filters"] = rows };
    }
}

/// <summary>
/// Create or update a phase filter. Params: name, new, existing, demolished, temporary
/// (each by_category | overridden | not_displayed), updateIfExists?.
/// </summary>
public sealed class CreatePhaseFilterCommand : IRevitCommand
{
    public string Name => "create_phase_filter";
    public bool IsReadOnly => false;
    public string RiskLevel => "low";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var name = P.Str(p, "name");
        var existing = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !P.BoolOr(p, "updateIfExists", false))
            throw new RevitCommandException("name_collision", $"Phase filter '{name}' exists. Pass updateIfExists=true.");
        var f = existing ?? PhaseFilter.Create(doc, name);
        foreach (var (key, status) in new[]
                 {
                     ("new", ElementOnPhaseStatus.New), ("existing", ElementOnPhaseStatus.Existing),
                     ("demolished", ElementOnPhaseStatus.Demolished), ("temporary", ElementOnPhaseStatus.Temporary),
                 })
            if (P.StrOrNull(p, key) is { } v) f.SetPhaseStatusPresentation(status, PhaseUtil.ParsePresentation(v));
        return new JsonObject
        {
            ["affected"] = existing is null ? Affected.Created(f.Id.Value) : Affected.Modified(f.Id.Value),
            ["id"] = f.Id.Value,
            ["name"] = f.Name,
            ["created"] = existing is null,
        };
    }
}

/// <summary>
/// Set Phase Created and/or Phase Demolished on elements (demolish = set phaseDemolished).
/// Params: elementIds, phaseCreated? (name/id), phaseDemolished? (name/id or "none").
/// Elements that can't take the value are reported, not fatal.
/// </summary>
public sealed class SetElementPhaseCommand : IRevitCommand
{
    public string Name => "set_element_phase";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var created = P.StrOrNull(p, "phaseCreated") is { } c ? PhaseUtil.Find(doc, c) : null;
        var demoStr = P.StrOrNull(p, "phaseDemolished");
        if (created is null && demoStr is null)
            throw new RevitCommandException("bad_request", "Pass phaseCreated and/or phaseDemolished.");
        ElementId? demoId = demoStr is null ? null
            : demoStr.Equals("none", StringComparison.OrdinalIgnoreCase) ? ElementId.InvalidElementId
            : PhaseUtil.Find(doc, demoStr).Id;

        var changed = new List<long>();
        var failed = new JsonArray();
        foreach (var id in OctaUtil.Ids(p, "elementIds"))
        {
            var el = doc.GetElement(id);
            try
            {
                if (el is null) throw new InvalidOperationException("not found");
                if (created is not null)
                {
                    var pc = el.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    if (pc is null || pc.IsReadOnly) throw new InvalidOperationException("no editable Phase Created");
                    pc.Set(created.Id);
                }
                if (demoId is not null)
                {
                    var pd = el.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                    if (pd is null || pd.IsReadOnly) throw new InvalidOperationException("no editable Phase Demolished");
                    pd.Set(demoId);
                }
                changed.Add(id.Value);
            }
            catch (Exception ex)
            {
                failed.Add(new JsonObject { ["id"] = id.Value, ["error"] = ex.Message });
            }
        }
        return new JsonObject
        {
            ["affected"] = Affected.Modified(changed),
            ["changed"] = changed.Count,
            ["failed"] = failed,
        };
    }
}

/// <summary>Set a view's Phase and/or Phase Filter. Params: viewIds, phase?, phaseFilter?.</summary>
public sealed class SetViewPhaseCommand : IRevitCommand
{
    public string Name => "set_view_phase";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var phase = P.StrOrNull(p, "phase") is { } ph ? PhaseUtil.Find(doc, ph) : null;
        var filter = P.StrOrNull(p, "phaseFilter") is { } pf ? PhaseUtil.FindFilter(doc, pf) : null;
        if (phase is null && filter is null) throw new RevitCommandException("bad_request", "Pass phase and/or phaseFilter.");

        var changed = new List<long>();
        var failed = new JsonArray();
        foreach (var id in OctaUtil.Ids(p, "viewIds"))
        {
            try
            {
                var v = doc.GetElement(id) as View ?? throw new InvalidOperationException("not a view");
                if (phase is not null)
                {
                    var prm = v.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    if (prm is null || prm.IsReadOnly)
                        throw new InvalidOperationException("Phase is not editable (controlled by its view template?)");
                    prm.Set(phase.Id);
                }
                if (filter is not null)
                {
                    var prm = v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
                    if (prm is null || prm.IsReadOnly)
                        throw new InvalidOperationException("Phase Filter is not editable (controlled by its view template?)");
                    prm.Set(filter.Id);
                }
                changed.Add(id.Value);
            }
            catch (Exception ex)
            {
                failed.Add(new JsonObject { ["id"] = id.Value, ["error"] = ex.Message });
            }
        }
        return new JsonObject { ["affected"] = Affected.Modified(changed), ["changed"] = changed.Count, ["failed"] = failed };
    }
}

/// <summary>Rename a phase. Params: phase (name/id), newName.</summary>
public sealed class RenamePhaseCommand : IRevitCommand
{
    public string Name => "rename_phase";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var ph = PhaseUtil.Find(doc, P.Str(ctx.Parameters, "phase"));
        var old = ph.Name;
        ph.Name = P.Str(ctx.Parameters, "newName");
        return new JsonObject { ["affected"] = Affected.Modified(ph.Id.Value), ["id"] = ph.Id.Value, ["from"] = old, ["to"] = ph.Name };
    }
}
