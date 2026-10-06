using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI.Events;

namespace RevitMCPAddin;

/// <summary>
/// OCTA: answers a short whitelist of harmless Revit dialogs so Revit can be driven unattended
/// (e.g. "Unresolved References" → "Ignore and continue opening the project"), and logs EVERY
/// dialog it sees — answered or not — so new rules can be added from evidence, not guesses.
///
/// Rules: %APPDATA%\Autodesk\Revit\Addins\{ver}\revit-mcp-dialogs.json
///   { "enabled": true, "rules": [ { "name": "...", "dialogId": "...", "messageContains": "...", "result": 1002 } ] }
/// A rule matches when every field it sets matches (case-insensitive substring). Defaults apply
/// when the file is absent. Log: revit-mcp-dialogs.log next to it.
/// </summary>
public sealed class DialogResponder
{
    private sealed record Rule(string Name, string? DialogId, string? MessageContains, int Result);

    private readonly List<Rule> _rules = new();
    private readonly bool _enabled = true;
    private readonly string _logPath;
    private readonly Action<string> _log;

    // TaskDialogResult.CommandLink2 = 1002: the second command link.
    private static readonly Rule[] Defaults =
    {
        new("Unresolved references → Ignore and continue", null, "could not find or read", 1002),
        // Revit's document warning box (after a command commits with warnings). OK = continue, the
        // same as a person clicking OK; without it an unattended session stalls.
        new("Document warnings → OK", "Dialog_Revit_DocWarnDialog", null, 1),
    };

    public DialogResponder(string revitVersion, Action<string> log)
    {
        _log = log;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", revitVersion);
        _logPath = Path.Combine(dir, "revit-mcp-dialogs.log");
        var cfg = Path.Combine(dir, "revit-mcp-dialogs.json");
        try
        {
            if (File.Exists(cfg))
            {
                var root = JsonNode.Parse(File.ReadAllText(cfg)) as JsonObject;
                _enabled = root?["enabled"]?.GetValue<bool>() ?? true;
                if (root?["rules"] is JsonArray arr)
                    foreach (var n in arr)
                        if (n is JsonObject o && o["result"] is not null)
                            _rules.Add(new Rule(
                                o["name"]?.GetValue<string>() ?? "rule",
                                o["dialogId"]?.GetValue<string>(),
                                o["messageContains"]?.GetValue<string>(),
                                o["result"]!.GetValue<int>()));
            }
            else _rules.AddRange(Defaults);
        }
        catch (Exception ex)
        {
            _log($"[RevitMCP] Dialog rules unreadable ({ex.Message}); using defaults.");
            _rules.Clear();
            _rules.AddRange(Defaults);
        }
        _log($"[RevitMCP] Dialog responder {(_enabled ? "ON" : "OFF")} — {_rules.Count} rule(s)");
    }

    public void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        var message = e switch
        {
            TaskDialogShowingEventArgs t => t.Message,
            MessageBoxShowingEventArgs m => m.Message,
            _ => "",
        };
        string action = "shown to user";
        try
        {
            if (_enabled)
                foreach (var r in _rules)
                {
                    if (r.DialogId is not null &&
                        (e.DialogId ?? "").IndexOf(r.DialogId, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (r.MessageContains is not null &&
                        (message ?? "").IndexOf(r.MessageContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (r.DialogId is null && r.MessageContains is null) continue;
                    action = e.OverrideResult(r.Result) ? $"answered {r.Result} by rule '{r.Name}'" : $"rule '{r.Name}' refused";
                    break;
                }
        }
        catch (Exception ex)
        {
            action = "error: " + ex.Message;
        }
        Write(e.DialogId, message, action);
    }

    private void Write(string? dialogId, string? message, string action)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                time = DateTime.Now.ToString("s"),
                dialogId,
                message = message?.Length > 400 ? message[..400] : message,
                action,
            });
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // logging must never interfere with Revit
        }
    }
}
