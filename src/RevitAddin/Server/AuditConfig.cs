using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RevitMCPAddin.Server;

/// <summary>How much of a command's params the audit log keeps.</summary>
internal enum AuditParamsMode
{
    /// <summary>Default: only the SHA-256 of the canonical params.</summary>
    Hash,
    /// <summary>Keys and non-string values; every string value is replaced.</summary>
    Redacted,
    /// <summary>The params as sent.</summary>
    Full,
}

/// <summary>Parsed <c>revit-mcp-audit.json</c>; <see cref="Errors"/> lists entries that fell back to a default.</summary>
internal sealed record AuditSettings(
    bool Enabled,
    string Directory,
    bool IncludeReads,
    AuditParamsMode LogParams,
    VerifyFailureMode VerifyFailure,
    IReadOnlyList<string> Errors,
    MutationMode MutationMode = MutationMode.Direct);

/// <summary>
/// Reads <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-audit.json</c> (next to the token
/// file). No file means the defaults — audit ON, writes only, params hashed, read-back failures
/// reported but not fatal, writes need no preview:
/// <code>{ "enabled": true, "dir": null, "includeReads": false, "logParams": "hash", "verifyFailure": "report", "mutationMode": "direct" }</code>
/// A bad value falls back to its default and is reported; it never disables the add-in.
/// </summary>
internal static class AuditConfig
{
    internal const string FileName = "revit-mcp-audit.json";

    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "audit");

    internal static AuditSettings Load(string revitVersion)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", revitVersion, FileName);
        if (!File.Exists(path)) return Parse(null);
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) { return Parse(null) with { Errors = new[] { $"{FileName} could not be read: {ex.Message}" } }; }
    }

    /// <summary>Pure parser (unit-tested). Null or empty text → defaults.</summary>
    internal static AuditSettings Parse(string? json)
    {
        var errors = new List<string>();
        bool enabled = true, includeReads = false;
        string dir = DefaultDirectory;
        var logParams = AuditParamsMode.Hash;
        var verify = VerifyFailureMode.Report;
        var mutation = MutationMode.Direct;

        if (!string.IsNullOrWhiteSpace(json))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(json!); }
            catch (JsonException ex) { errors.Add($"{FileName} is not valid JSON ({ex.Message}); using defaults"); }

            if (doc is not null)
            {
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        errors.Add($"{FileName} must be a JSON object; using defaults");
                    else
                    {
                        enabled = Bool(root, "enabled", true, errors);
                        includeReads = Bool(root, "includeReads", false, errors);

                        if (root.TryGetProperty("dir", out var d) && d.ValueKind != JsonValueKind.Null)
                        {
                            if (d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
                                dir = Environment.ExpandEnvironmentVariables(d.GetString()!.Trim());
                            else errors.Add("\"dir\" must be a non-empty string or null; using the default folder");
                        }

                        logParams = Choice(root, "logParams", AuditParamsMode.Hash, errors, new()
                        {
                            ["hash"] = AuditParamsMode.Hash,
                            ["redacted"] = AuditParamsMode.Redacted,
                            ["full"] = AuditParamsMode.Full,
                        });
                        verify = Choice(root, "verifyFailure", VerifyFailureMode.Report, errors, new()
                        {
                            ["report"] = VerifyFailureMode.Report,
                            ["error"] = VerifyFailureMode.Error,
                        });
                        mutation = Choice(root, "mutationMode", MutationMode.Direct, errors, new()
                        {
                            ["direct"] = MutationMode.Direct,
                            ["preview_required"] = MutationMode.PreviewRequired,
                        });
                    }
                }
            }
        }
        return new AuditSettings(enabled, dir, includeReads, logParams, verify, errors, mutation);
    }

    private static bool Bool(JsonElement root, string key, bool def, List<string> errors)
    {
        if (!root.TryGetProperty(key, out var v)) return def;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        errors.Add($"\"{key}\" must be true or false; using {def.ToString().ToLowerInvariant()}");
        return def;
    }

    private static T Choice<T>(JsonElement root, string key, T def, List<string> errors, Dictionary<string, T> map)
    {
        if (!root.TryGetProperty(key, out var v)) return def;
        if (v.ValueKind == JsonValueKind.String && map.TryGetValue(v.GetString()!.Trim().ToLowerInvariant(), out var t))
            return t;
        errors.Add($"\"{key}\" must be one of {string.Join(", ", map.Keys)}; using the default");
        return def;
    }
}
