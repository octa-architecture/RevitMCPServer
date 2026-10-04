using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Server;

/// <summary>
/// Append-only JSONL audit, one file per UTC day: <c>{dir}\audit-YYYY-MM-DD.jsonl</c>, one line per
/// command (a batch writes one line per step plus a summary line sharing <c>batchId</c>).
///
/// Privacy: the document is recorded only as a hash of its path, and params only as a hash unless
/// <c>logParams</c> says otherwise. In hash mode the read-back <c>detail</c> is dropped too, since a
/// mismatch message quotes the values involved.
///
/// Fail-open: any write error is remembered in <see cref="LastError"/> (surfaced by <c>/health</c>)
/// and printed once per distinct error; the command's result is never affected.
/// </summary>
public sealed class AuditLog : IAuditSink
{
    private readonly object _lock = new();
    private readonly AuditSettings _settings;
    private readonly Action<string> _log;
    private string? _lastPrinted;

    internal AuditLog(AuditSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    public bool Enabled => _settings.Enabled;
    public bool IncludeReads => _settings.IncludeReads;
    public string Directory => _settings.Directory;

    /// <summary>The most recent write failure, or null once a later write succeeds.</summary>
    public string? LastError { get; private set; }

    public void Write(AuditRecord record)
    {
        if (!_settings.Enabled) return;
        try
        {
            var line = Format(record, _settings.LogParams).ToJsonString() + "\n";
            var file = Path.Combine(_settings.Directory, $"audit-{record.TimestampUtc:yyyy-MM-dd}.jsonl");
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(_settings.Directory);
                File.AppendAllText(file, line, new UTF8Encoding(false));
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            if (LastError != _lastPrinted)
            {
                _lastPrinted = LastError;
                _log($"[RevitMCP] Audit write failed (commands are unaffected): {LastError}");
            }
        }
    }

    /// <summary>The JSON line for a record (pure; unit-tested).</summary>
    internal static JsonObject Format(AuditRecord r, AuditParamsMode mode)
    {
        var o = new JsonObject
        {
            ["ts"] = r.TimestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["id"] = r.Id,
            ["revit"] = r.RevitVersion,
            ["docHash"] = r.DocumentKey is null ? null : CanonicalJson.Sha256(r.DocumentKey),
            ["command"] = r.Command,
            ["kind"] = r.Kind,
            ["batchId"] = r.BatchId,
            ["step"] = r.Step,
            ["dryRun"] = r.DryRun,
            ["ok"] = r.Ok,
            ["errorCode"] = r.ErrorCode,
            ["durationMs"] = r.DurationMs,
            ["paramsHash"] = CanonicalJson.Sha256(r.Parameters),
        };
        if (mode == AuditParamsMode.Redacted) o["params"] = Redact(r.Parameters);
        else if (mode == AuditParamsMode.Full) o["params"] = r.Parameters?.DeepClone();

        o["affected"] = r.Affected?.DeepClone();
        o["verify"] = r.Verify is JsonObject v
            ? new JsonObject
            {
                ["status"] = v["status"]?.DeepClone(),
                ["detail"] = mode == AuditParamsMode.Full ? v["detail"]?.DeepClone() : null,
            }
            : null;
        o["client"] = r.Client;
        o["trace"] = r.Trace;
        o["approval"] = r.Approval?.DeepClone();
        return o;
    }

    /// <summary>Keeps structure and non-string values; replaces every string value with "***".</summary>
    internal static JsonNode? Redact(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Select(kv => new System.Collections.Generic.KeyValuePair<string, JsonNode?>(kv.Key, Redact(kv.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Redact).ToArray()),
        JsonValue val when val.TryGetValue<string>(out _) => JsonValue.Create("***"),
        _ => node.DeepClone(),
    };
}
