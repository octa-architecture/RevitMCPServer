using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Opt-in read-back for write commands. After the dispatcher commits the transaction (never on a
/// dry-run) it calls <see cref="Verify"/> with the command's own result; the command re-reads the
/// model and reports what is actually stored. The outcome is attached as <c>data.verify</c> and
/// written to the audit log.
///
/// Why after the commit: Revit can still change or delete what a command wrote while it processes
/// failures at commit time (e.g. a room in a region that already has one is removed, a parameter a
/// formula drives snaps back). A check inside the transaction cannot see that.
///
/// Commands that do not implement this get the generic check: if the result carries
/// <c>affected</c> (see <see cref="Affected"/>), created/modified ids must exist and deleted ids
/// must be gone; otherwise <c>verify.status</c> is <c>not_supported</c>.
/// </summary>
public interface IVerifiableCommand
{
    VerifyResult Verify(CommandContext ctx, JsonObject result);
}

/// <summary>Outcome of a read-back. <see cref="Status"/> is one of the constants below.</summary>
public sealed record VerifyResult(string Status, string? Detail = null, JsonObject? Observed = null)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string NotSupported = "not_supported";

    public static VerifyResult Pass(string? detail = null, JsonObject? observed = null) => new(Passed, detail, observed);
    public static VerifyResult Fail(string detail, JsonObject? observed = null) => new(Failed, detail, observed);
    public static VerifyResult Skip(string detail) => new(Skipped, detail);
    public static VerifyResult Unsupported() => new(NotSupported);

    public bool IsFailed => Status == Failed;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["status"] = Status, ["detail"] = Detail };
        if (Observed is not null) o["observed"] = Observed.DeepClone();
        return o;
    }
}
