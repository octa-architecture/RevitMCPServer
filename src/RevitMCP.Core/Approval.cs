using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RevitMCPAddin;

/// <summary>Whether a model write may run straight away or must first be previewed.</summary>
public enum MutationMode
{
    /// <summary>Default: writes run as sent (the behaviour before approval tokens existed).</summary>
    Direct,

    /// <summary>
    /// A model write must carry the <c>approvalToken</c> returned by a dry-run of the SAME request
    /// (command, params, active document). Read-only and UI-action commands are not gated.
    /// </summary>
    PreviewRequired,
}

/// <summary>Outcome of presenting an approval token; <see cref="Code"/> is the error code when not ok.</summary>
public sealed record ApprovalCheck(bool Ok, string? Code = null, string? Message = null)
{
    public const string Required = "approval_required";
    public const string Mismatch = "approval_mismatch";
}

/// <summary>
/// What an approval token is bound to: the canonical request plus the active document. Any change
/// to the command, a param value, the batch steps, <c>stopOnError</c> or the document gives a
/// different binding, so the token no longer fits.
/// </summary>
public static class ApprovalBinding
{
    public static string ForCommand(string command, JsonObject? parameters, string? docKey) =>
        CanonicalJson.Sha256(new JsonObject
        {
            ["command"] = command,
            ["params"] = parameters?.DeepClone() ?? new JsonObject(),
            ["doc"] = docKey,
        });

    public static string ForBatch(IEnumerable<(string Command, JsonObject? Parameters)> steps, bool stopOnError,
        string? docKey) =>
        CanonicalJson.Sha256(new JsonObject
        {
            ["command"] = "batch",
            ["steps"] = new JsonArray(steps.Select(s => (JsonNode?)new JsonObject
            {
                ["command"] = s.Command,
                ["params"] = s.Parameters?.DeepClone() ?? new JsonObject(),
            }).ToArray()),
            ["stopOnError"] = stopOnError,
            ["doc"] = docKey,
        });
}

/// <summary>
/// In-memory, single-use approval tokens. A token is random (not derived from the request, so it
/// cannot be forged from a known payload) and maps to the binding of the dry-run that issued it.
/// Tokens live in this Revit session only: restarting Revit invalidates every outstanding one.
/// Thread-safe; the dispatcher calls it from the Revit thread, so check-and-consume is atomic.
/// </summary>
public sealed class ApprovalStore
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);
    private const int MaxEntries = 1000;

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Binding, DateTime ExpiresUtc)> _tokens = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _clock;

    public ApprovalStore(TimeSpan? ttl = null, Func<DateTime>? clock = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public TimeSpan Ttl => _ttl;

    /// <summary>Outstanding (unexpired, unused) tokens.</summary>
    public int Count
    {
        get { lock (_lock) { Purge(_clock()); return _tokens.Count; } }
    }

    /// <summary>Mints a token for <paramref name="binding"/>, valid for <see cref="Ttl"/>.</summary>
    public (string Token, DateTime ExpiresUtc) Issue(string binding)
    {
        var bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        var token = "apv_" + string.Concat(bytes.Select(b => b.ToString("x2")));
        lock (_lock)
        {
            var now = _clock();
            Purge(now);
            // Bound memory: a client that previews without ever executing cannot grow this forever.
            while (_tokens.Count >= MaxEntries)
                _tokens.Remove(_tokens.OrderBy(kv => kv.Value.ExpiresUtc).First().Key);
            var expires = now + _ttl;
            _tokens[token] = (binding, expires);
            return (token, expires);
        }
    }

    /// <summary>
    /// Checks <paramref name="token"/> against <paramref name="binding"/> and, on success, consumes it.
    /// A token presented for a different request is NOT consumed, so a mistyped call does not burn
    /// the approval of the request that was actually previewed.
    /// </summary>
    public ApprovalCheck Consume(string? token, string binding)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new ApprovalCheck(false, ApprovalCheck.Required,
                "This server requires a preview before any model change (mutationMode: preview_required). " +
                "Send the same request with dryRun: true, review the result, then send it again with the " +
                "approvalToken it returned.");

        lock (_lock)
        {
            var now = _clock();
            if (!_tokens.TryGetValue(token!, out var entry))
                return new ApprovalCheck(false, ApprovalCheck.Mismatch,
                    "approvalToken is unknown or already used (tokens are single-use and do not survive a " +
                    "Revit restart). Run the dry-run again to get a new one.");

            if (now > entry.ExpiresUtc)
            {
                _tokens.Remove(token!);
                return new ApprovalCheck(false, ApprovalCheck.Mismatch,
                    $"approvalToken expired (valid {(int)_ttl.TotalSeconds}s after the dry-run). " +
                    "Run the dry-run again to get a new one.");
            }

            if (!string.Equals(entry.Binding, binding, StringComparison.Ordinal))
                return new ApprovalCheck(false, ApprovalCheck.Mismatch,
                    "approvalToken was issued for a different request: the command, its params, the batch " +
                    "steps or the active document changed since the dry-run. Preview this exact request again.");

            _tokens.Remove(token!);
            return new ApprovalCheck(true);
        }
    }

    /// <summary>
    /// Non-reversible short id of a token, safe for the audit log: it links a dry-run line to the
    /// write it approved without writing a replayable token to disk.
    /// </summary>
    public static string Fingerprint(string token) => CanonicalJson.Sha256(token).Substring(7, 16);

    private void Purge(DateTime now)
    {
        if (_tokens.Count == 0) return;
        foreach (var key in _tokens.Where(kv => now > kv.Value.ExpiresUtc).Select(kv => kv.Key).ToList())
            _tokens.Remove(key);
    }
}
