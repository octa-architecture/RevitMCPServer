using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Copy parameter values from a source element to one or more target elements.
/// Only writable parameters whose storage type matches on both elements are copied.
///
/// Params:
///   - sourceId:       long, required — the element to copy from.
///   - targetIds:      long[], required — elements to copy to.
///   - parameterNames: string[], optional — names to copy. Omit to copy all writable params.
/// </summary>
public sealed class CopyParametersCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "copy_parameters";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        var sourceIdValue = P.Long(p, "sourceId");
        var targetIdsArr  = P.Arr(p, "targetIds");

        var sourceElem = doc.GetElement(new ElementId(sourceIdValue))
            ?? throw new RevitCommandException("not_found", $"Source element {sourceIdValue} not found.");

        // Build optional name filter.
        HashSet<string>? filterNames = null;
        if (p["parameterNames"] is JsonArray namesArr)
        {
            filterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in namesArr)
                if (n is not null) filterNames.Add(P.StrFrom(n, "parameterNames[]"));
        }

        // Collect writable source parameters keyed by name.
        var sourceParams = new Dictionary<string, Parameter>(StringComparer.OrdinalIgnoreCase);
        foreach (Parameter sp in sourceElem.Parameters)
        {
            if (sp.IsReadOnly || sp.StorageType == StorageType.None) continue;
            if (sp.Definition?.Name is not string name) continue;
            if (filterNames != null && !filterNames.Contains(name)) continue;
            sourceParams[name] = sp;
        }

        if (sourceParams.Count == 0)
            throw new RevitCommandException("not_found",
                filterNames != null
                    ? "None of the specified parameters were found (writable, matching storage type) on the source element."
                    : "Source element has no writable parameters.");

        var perTargetResults = new JsonArray();
        int totalCopied = 0;

        foreach (var targetNode in targetIdsArr)
        {
            var targetIdValue = P.LongFrom(targetNode, "targetIds[]");
            var targetElem = doc.GetElement(new ElementId(targetIdValue));

            if (targetElem == null)
            {
                perTargetResults.Add(new JsonObject
                {
                    ["targetId"]    = targetIdValue,
                    ["ok"]          = false,
                    ["error"]       = "not_found",
                    ["paramsCopied"] = 0,
                });
                continue;
            }

            int copied = 0;
            var failures = new JsonArray();
            var copiedNames = new JsonArray();

            foreach (var (paramName, srcParam) in sourceParams)
            {
                var tgtParam = targetElem.LookupParameter(paramName);
                if (tgtParam == null || tgtParam.IsReadOnly || tgtParam.StorageType != srcParam.StorageType)
                    continue;

                try
                {
                    bool wrote = srcParam.StorageType switch
                    {
                        StorageType.String    => tgtParam.Set(srcParam.AsString()),
                        StorageType.Integer   => tgtParam.Set(srcParam.AsInteger()),
                        StorageType.Double    => tgtParam.Set(srcParam.AsDouble()),
                        StorageType.ElementId => tgtParam.Set(srcParam.AsElementId()),
                        _                     => false,
                    };
                    if (wrote) { copied++; copiedNames.Add(paramName); }
                }
                catch (Exception ex)
                {
                    failures.Add(new JsonObject
                    {
                        ["parameterName"] = paramName,
                        ["error"]         = ex.Message,
                    });
                }
            }

            totalCopied += copied;
            var entry = new JsonObject
            {
                ["targetId"]    = targetIdValue,
                ["ok"]          = true,
                ["paramsCopied"] = copied,
                ["copiedParameters"] = copiedNames,
            };
            if (failures.Count > 0) entry["failures"] = failures;
            perTargetResults.Add(entry);
        }

        return new JsonObject
        {
            ["sourceId"]    = sourceIdValue,
            ["affected"]    = Affected.Modified(perTargetResults.OfType<JsonObject>()
                                  .Where(t => t["ok"]?.GetValue<bool>() == true && (t["paramsCopied"]?.GetValue<int>() ?? 0) > 0)
                                  .Select(t => t["targetId"]!.GetValue<long>())),
            ["paramsCopied"] = totalCopied,
            ["targets"]     = perTargetResults,
            ["changeSummary"] = $"Copied {totalCopied} parameter value(s) from element {sourceIdValue} to {perTargetResults.Count} target(s).",
        };
    }
    /// <summary>Every parameter the command reports as copied must now equal the source's value.</summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result)
    {
        var doc = ctx.RequireDoc();
        var src = doc.GetElement(new ElementId(P.Long(ctx.Parameters, "sourceId")));
        if (src is null) return VerifyResult.Skip("source element no longer exists; nothing to compare against");
        var mismatches = new List<string>();
        var compared = 0;
        foreach (var t in (result["targets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (t["ok"]?.GetValue<bool>() != true) continue;
            var tid = t["targetId"]!.GetValue<long>();
            var target = doc.GetElement(new ElementId(tid));
            if (target is null) { mismatches.Add($"{tid}: target missing after commit"); continue; }
            foreach (var n in (t["copiedParameters"] as JsonArray ?? new JsonArray()))
            {
                var name = n!.GetValue<string>();
                var sp = src.LookupParameter(name);
                var tp = target.LookupParameter(name);
                if (sp is null || tp is null) { mismatches.Add($"{tid}: '{name}' missing after commit"); continue; }
                compared++;
                var want = ReadBack.Raw(sp);
                if (want is not null && ReadBack.ParameterMismatch(tp, want) is string m) mismatches.Add($"{tid}: {m}");
            }
        }
        return ReadBack.FromMismatches(mismatches, $"{compared} copied value(s) re-read");
    }
}
