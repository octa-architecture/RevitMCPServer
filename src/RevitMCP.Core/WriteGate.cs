using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;

namespace RevitMCPAddin;

/// <summary>What the dispatcher does when a read-back fails.</summary>
public enum VerifyFailureMode
{
    /// <summary>Default: keep <c>ok:true</c> (the change is committed) and report <c>data.verify.status = "failed"</c>.</summary>
    Report,

    /// <summary>
    /// Strict: a single command returns <c>ok:false, error.code = "verify_failed"</c> (the message
    /// says the model WAS committed); a batch verifies before its commit and rolls back whole.
    /// </summary>
    Error,
}

/// <summary>Server-side write-gate settings, loaded by the host from <c>revit-mcp-audit.json</c>.</summary>
public sealed class WriteGateOptions
{
    public VerifyFailureMode VerifyFailure { get; init; } = VerifyFailureMode.Report;

    /// <summary>Default <see cref="MutationMode.Direct"/>: writes need no approval token.</summary>
    public MutationMode Mutation { get; init; } = MutationMode.Direct;

    /// <summary>How long a dry-run's approval token stays valid (preview_required mode only).</summary>
    public TimeSpan ApprovalTtl { get; init; } = ApprovalStore.DefaultTtl;
}

/// <summary>
/// Per-request context the HTTP layer hands to the dispatcher: audit labels, plus the approval token
/// a write carries in preview_required mode (checked, never logged).
/// </summary>
public sealed record RequestMeta(string? RequestId, string? Client, string? Trace, string? ApprovalToken = null)
{
    public static readonly RequestMeta None = new(null, null, null);
}

/// <summary>One audit line, before the sink decides how much of <see cref="Parameters"/> to keep.</summary>
public sealed class AuditRecord
{
    public DateTime TimestampUtc { get; init; }
    public string Id { get; init; } = "";
    public string? RevitVersion { get; init; }
    /// <summary>Document identity (path or title) — the sink stores only its hash.</summary>
    public string? DocumentKey { get; init; }
    public string Command { get; init; } = "";
    public string? Kind { get; init; }
    public string? BatchId { get; init; }
    public int? Step { get; init; }
    public bool DryRun { get; init; }
    public bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public long DurationMs { get; init; }
    public JsonNode? Parameters { get; init; }
    public JsonNode? Affected { get; init; }
    public JsonNode? Verify { get; init; }
    public string? Client { get; init; }
    public string? Trace { get; init; }
    /// <summary><c>{state, token}</c> in preview_required mode — token is a fingerprint, never the token.</summary>
    public JsonNode? Approval { get; init; }
}

/// <summary>
/// Where audit records go. Implementations must never throw into the command path — an audit
/// failure is reported (console, <c>/health</c>) but the command's result is unchanged.
/// </summary>
public interface IAuditSink
{
    /// <summary>When false, read-only commands are not audited.</summary>
    bool IncludeReads { get; }

    void Write(AuditRecord record);
}

/// <summary>Pure decisions the dispatcher applies around a commit (unit-tested without Revit).</summary>
public static class WriteGate
{
    /// <summary>
    /// Attaches <paramref name="verify"/> to a committed command's data and decides the envelope.
    /// Report mode: unchanged success envelope. Error mode + failed: <c>verify_failed</c>, with the
    /// data kept on the envelope so the caller still gets the ids of what was committed.
    /// </summary>
    public static JsonObject ApplyVerify(JsonObject successEnvelope, JsonObject data, VerifyResult verify,
        VerifyFailureMode mode)
    {
        data["verify"] = verify.ToJson();
        if (!verify.IsFailed || mode != VerifyFailureMode.Error) return successEnvelope;

        var err = JsonResult.Error("verify_failed",
            $"Read-back after commit failed: {verify.Detail}. The change WAS committed to the model — " +
            "check it in Revit or undo it (Ctrl+Z undoes this command as one step).");
        err["data"] = data.DeepClone();
        return err;
    }

    /// <summary>
    /// In a batch every read-back sees the FINAL state, so a step whose element a later step changed
    /// again (set the same parameter twice, create then move…) can look failed when it was simply
    /// superseded. Such a failure becomes <c>skipped</c>, naming the later step; the later step's own
    /// read-back is the one that speaks for that element. Detection uses <c>data.affected</c>, so a
    /// later step that does not report it cannot supersede anything.
    /// </summary>
    public static IReadOnlyList<VerifyResult> Supersede(IReadOnlyList<(int Index, JsonObject Data, VerifyResult Verify)> steps)
    {
        var touched = steps.Select(s =>
        {
            var (c, m, d) = Commands.Affected.Read(s.Data["affected"]);
            return new HashSet<long>(c.Concat(m).Concat(d));
        }).ToList();

        var output = new List<VerifyResult>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var r = steps[i].Verify;
            if (r.IsFailed && touched[i].Count > 0)
            {
                for (var j = i + 1; j < steps.Count; j++)
                {
                    var overlap = touched[i].Intersect(touched[j]).Take(5).ToList();
                    if (overlap.Count == 0) continue;
                    r = VerifyResult.Skip($"element(s) {string.Join(", ", overlap)} were changed again by step {steps[j].Index} " +
                                          $"of this batch; its read-back reports the final state. Was: {r.Detail}");
                    break;
                }
            }
            output.Add(r);
        }
        return output;
    }

    /// <summary>Strict batches roll back when any step's read-back failed.</summary>
    public static bool BatchMustRollBack(IEnumerable<VerifyResult> results, VerifyFailureMode mode) =>
        mode == VerifyFailureMode.Error && results.Any(r => r.IsFailed);

    /// <summary>The <c>verify_failed</c> envelope for a strict batch that was rolled back.</summary>
    public static JsonObject BatchVerifyFailed(JsonArray results, IEnumerable<(int Index, string Command, VerifyResult Verify)> failures)
    {
        var list = failures.ToList();
        var first = list[0];
        return new JsonObject
        {
            ["ok"] = false,
            ["error"] = new JsonObject
            {
                ["code"] = "verify_failed",
                ["message"] = $"Batch rolled back: read-back failed for {list.Count} step(s); first at step {first.Index} " +
                              $"('{first.Command}'): {first.Verify.Detail}. Nothing was committed.",
            },
            ["committed"] = false,
            ["results"] = results,
        };
    }
}

/// <summary>Canonical JSON (object keys sorted, no whitespace) and its SHA-256, for audit hashes.</summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    public static string Sha256(JsonNode? node) => Sha256(Serialize(node));

    public static string Sha256(string text)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder("sha256:", 7 + 64);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(a[i], sb);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }
}
