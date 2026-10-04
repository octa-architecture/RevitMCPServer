using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Batch per-element parameter update for spreadsheet import.
/// Each item names one element + one parameter + one value.
/// All items execute in the single transaction opened by the dispatcher,
/// so the entire import is one Revit undo step.
///
/// Params:
///   items: required — array of { elementId: long, parameterName: string,
///                                value: string,   units?: string }
///
/// Returns: { applied: int, failed: int,
///            results: [{ elementId, ok, error? }] }
/// </summary>
public sealed class ImportParametersCommand : IRevitCommand, IVerifiableCommand
{
    public string Name => "import_parameters";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var items = ctx.Parameters["items"] as JsonArray
            ?? throw new RevitCommandException("bad_request",
                "Missing required parameter 'items' (array).");

        var applied = 0;
        var failed = 0;
        var results = new JsonArray();
        var okIds = new List<long>();

        foreach (var item in items.OfType<JsonObject>())
        {
            var elementId = P.Long(item, "elementId");
            var paramName = P.StrOrNull(item, "parameterName")
                ?? throw new RevitCommandException("bad_request",
                    "Each item requires 'parameterName'.");
            var valueNode = item["value"]
                ?? throw new RevitCommandException("bad_request",
                    "Each item requires 'value'.");
            var units = P.StrOrNull(item, "units") ?? "internal";

            try
            {
                var el = doc.GetElement(new ElementId(elementId))
                    ?? throw new InvalidOperationException($"Element {elementId} not found.");

                var param = el.LookupParameter(paramName)
                    ?? throw new InvalidOperationException(
                        $"Param '{paramName}' not found on element {elementId}.");

                if (param.IsReadOnly)
                    throw new InvalidOperationException($"Param '{paramName}' is read-only.");

                SetValue(param, valueNode, units);
                applied++;
                okIds.Add(elementId);
                results.Add(new JsonObject { ["elementId"] = elementId, ["ok"] = true });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new JsonObject
                {
                    ["elementId"] = elementId,
                    ["ok"] = false,
                    ["error"] = ex.Message,
                });
            }
        }

        return new JsonObject
        {
            ["applied"] = applied,
            ["failed"] = failed,
            ["results"] = results,
            ["affected"] = Affected.Modified(okIds),
        };
    }

    /// <summary>Re-reads every row reported ok and compares it with the same coercion the write used.</summary>
    public VerifyResult Verify(CommandContext ctx, JsonObject result)
    {
        var doc = ctx.RequireDoc();
        var items = (ctx.Parameters["items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var rows = result["results"] as JsonArray;
        var mismatches = new List<string>();
        var checkedRows = 0;
        for (var i = 0; i < items.Count && rows is not null && i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row || row["ok"]?.GetValue<bool>() != true) continue;
            checkedRows++;
            var item = items[i];
            var id = P.Long(item, "elementId");
            var param = doc.GetElement(new ElementId(id))?.LookupParameter(P.Str(item, "parameterName"));
            if (param is null) { mismatches.Add($"row {i} ({id}): element or parameter missing after commit"); continue; }
            var expected = Coerce(param, item["value"]!, P.StrOrNull(item, "units") ?? "internal");
            if (ReadBack.ParameterMismatch(param, expected) is string m) mismatches.Add($"row {i} ({id}): {m}");
        }
        return ReadBack.FromMismatches(mismatches, $"{checkedRows} row(s) re-read");
    }

    // Value always arrives as a string from CSV/Excel; Coerce turns it into the parameter's
    // StorageType (shared by the write and the read-back so they can never disagree).
    private static void SetValue(Parameter param, JsonNode valueNode, string units)
    {
        var v = Coerce(param, valueNode, units);
        switch (param.StorageType)
        {
            case StorageType.String: param.Set((string)v); break;
            case StorageType.Integer: param.Set((int)v); break;
            case StorageType.Double: param.Set((double)v); break;
            case StorageType.ElementId: param.Set(new ElementId((long)v)); break;
        }
    }

    private static object Coerce(Parameter param, JsonNode valueNode, string units)
    {
        // Prefer the string representation that came from the spreadsheet cell.
        var raw = valueNode is JsonValue jv && jv.TryGetValue<string>(out var sv)
            ? sv
            : valueNode.ToString();

        switch (param.StorageType)
        {
            case StorageType.String:
                return raw;

            case StorageType.Integer:
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    throw new InvalidOperationException(
                        $"Cannot parse '{raw}' as integer for '{param.Definition.Name}'.");
                return i;

            case StorageType.Double:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new InvalidOperationException(
                        $"Cannot parse '{raw}' as number for '{param.Definition.Name}'.");
                return SetParameterCommand.ConvertToInternal(param, d, units, out _);

            case StorageType.ElementId:
                if (!long.TryParse(raw, out var eid))
                    throw new InvalidOperationException(
                        $"Cannot parse '{raw}' as ElementId for '{param.Definition.Name}'.");
                return eid;

            default:
                throw new InvalidOperationException(
                    $"Unsupported StorageType '{param.StorageType}' for '{param.Definition.Name}'.");
        }
    }
}
