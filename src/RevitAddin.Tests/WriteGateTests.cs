using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCPAddin;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Server;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>A write command whose read-back always fails — stands in for "the API said yes, nothing was stored".</summary>
public sealed class FakeAlwaysFailsVerify : IRevitCommand, IVerifiableCommand
{
    public string Name => "fake_write";
    public bool IsReadOnly => false;
    public JsonNode? Execute(CommandContext ctx) => new JsonObject { ["id"] = 7, ["affected"] = Affected.Modified(7) };
    public VerifyResult Verify(CommandContext ctx, JsonObject result) =>
        VerifyResult.Fail("Mark: expected 'D-101', stored 'D-10'");
}

public sealed class FakeAlwaysPassesVerify : IRevitCommand, IVerifiableCommand
{
    public string Name => "fake_ok";
    public bool IsReadOnly => false;
    public JsonNode? Execute(CommandContext ctx) => new JsonObject { ["id"] = 8 };
    public VerifyResult Verify(CommandContext ctx, JsonObject result) => VerifyResult.Pass();
}

public class WriteGateTests
{
    private static (JsonObject Envelope, JsonObject Data) Committed(IRevitCommand cmd)
    {
        var data = (JsonObject)cmd.Execute(null!)!;
        return (JsonResult.Success(data), data);
    }

    private static VerifyResult VerifyOf(IRevitCommand cmd, JsonObject data) =>
        ((IVerifiableCommand)cmd).Verify(null!, data);

    [Fact]
    public void Report_mode_keeps_ok_true_and_attaches_the_failure()
    {
        var cmd = new FakeAlwaysFailsVerify();
        var (env, data) = Committed(cmd);
        var final = WriteGate.ApplyVerify(env, data, VerifyOf(cmd, data), VerifyFailureMode.Report);

        Assert.True(final["ok"]!.GetValue<bool>());
        Assert.Equal("failed", final["data"]!["verify"]!["status"]!.GetValue<string>());
        Assert.Contains("D-10", final["data"]!["verify"]!["detail"]!.GetValue<string>());
    }

    [Fact]
    public void Error_mode_returns_verify_failed_saying_the_model_was_committed()
    {
        var cmd = new FakeAlwaysFailsVerify();
        var (env, data) = Committed(cmd);
        var final = WriteGate.ApplyVerify(env, data, VerifyOf(cmd, data), VerifyFailureMode.Error);

        Assert.False(final["ok"]!.GetValue<bool>());
        Assert.Equal("verify_failed", final["error"]!["code"]!.GetValue<string>());
        Assert.Contains("WAS committed", final["error"]!["message"]!.GetValue<string>());
        // The ids of what was committed are still handed back.
        Assert.Equal(7, final["data"]!["id"]!.GetValue<int>());
        Assert.Equal("failed", final["data"]!["verify"]!["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(VerifyFailureMode.Report)]
    [InlineData(VerifyFailureMode.Error)]
    public void A_passing_read_back_never_changes_the_envelope(VerifyFailureMode mode)
    {
        var cmd = new FakeAlwaysPassesVerify();
        var (env, data) = Committed(cmd);
        var final = WriteGate.ApplyVerify(env, data, VerifyOf(cmd, data), mode);
        Assert.Same(env, final);
        Assert.True(final["ok"]!.GetValue<bool>());
        Assert.Equal("passed", final["data"]!["verify"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Batch_rolls_back_only_in_error_mode_and_only_on_a_failure()
    {
        var mixed = new[] { VerifyResult.Pass(), VerifyResult.Fail("x"), VerifyResult.Unsupported() };
        var clean = new[] { VerifyResult.Pass(), VerifyResult.Unsupported(), VerifyResult.Skip("y") };
        Assert.True(WriteGate.BatchMustRollBack(mixed, VerifyFailureMode.Error));
        Assert.False(WriteGate.BatchMustRollBack(mixed, VerifyFailureMode.Report));
        Assert.False(WriteGate.BatchMustRollBack(clean, VerifyFailureMode.Error));
    }

    [Fact]
    public void A_failure_on_an_element_a_later_step_changed_again_is_superseded_not_failed()
    {
        var steps = new List<(int, JsonObject, VerifyResult)>
        {
            (0, new JsonObject { ["affected"] = Affected.Modified(42) }, VerifyResult.Fail("Comments: expected 'b1', stored 'b3'")),
            (1, new JsonObject { ["affected"] = Affected.Created(99) }, VerifyResult.Pass()),
            (2, new JsonObject { ["affected"] = Affected.Modified(42) }, VerifyResult.Pass()),
        };
        var r = WriteGate.Supersede(steps);
        Assert.Equal("skipped", r[0].Status);
        Assert.Contains("step 2", r[0].Detail);
        Assert.Contains("42", r[0].Detail);
        Assert.Equal("passed", r[2].Status);
    }

    [Fact]
    public void A_failure_nobody_touched_again_stays_failed()
    {
        var steps = new List<(int, JsonObject, VerifyResult)>
        {
            (0, new JsonObject { ["affected"] = Affected.Modified(42) }, VerifyResult.Fail("x")),
            (1, new JsonObject { ["affected"] = Affected.Modified(43) }, VerifyResult.Pass()),
            (2, new JsonObject(), VerifyResult.Unsupported()),   // no affected: cannot supersede anything
        };
        Assert.Equal("failed", WriteGate.Supersede(steps)[0].Status);
    }

    [Fact]
    public void Batch_verify_failed_envelope_says_nothing_was_committed()
    {
        var env = WriteGate.BatchVerifyFailed(new JsonArray(),
            new[] { (2, "set_parameter", VerifyResult.Fail("Mark: expected 'A', stored 'B'")) });
        Assert.False(env["ok"]!.GetValue<bool>());
        Assert.False(env["committed"]!.GetValue<bool>());
        Assert.Equal("verify_failed", env["error"]!["code"]!.GetValue<string>());
        Assert.Contains("step 2", env["error"]!["message"]!.GetValue<string>());
        Assert.Contains("Nothing was committed", env["error"]!["message"]!.GetValue<string>());
    }
}

public class ReadBackAffectedTests
{
    [Fact]
    public void No_affected_key_is_not_supported()
        => Assert.Equal("not_supported", ReadBack.CheckAffected(new JsonObject { ["id"] = 1 }, _ => true).Status);

    [Fact]
    public void All_present_and_all_gone_passes()
    {
        var r = new JsonObject { ["affected"] = Affected.Of(created: new long[] { 1, 2 }, modified: new long[] { 3 }, deleted: new long[] { 9 }) };
        var v = ReadBack.CheckAffected(r, id => id != 9);
        Assert.Equal("passed", v.Status);
    }

    [Fact]
    public void A_created_element_missing_after_commit_fails()
    {
        var r = new JsonObject { ["affected"] = Affected.Created(5, 6) };
        var v = ReadBack.CheckAffected(r, id => id == 5);
        Assert.Equal("failed", v.Status);
        Assert.Contains("6", v.Detail);
        Assert.Contains("created but missing", v.Detail);
    }

    [Fact]
    public void A_deleted_element_still_present_fails()
    {
        var r = new JsonObject { ["affected"] = Affected.Deleted(new long[] { 4 }) };
        var v = ReadBack.CheckAffected(r, _ => true);
        Assert.Equal("failed", v.Status);
        Assert.Contains("still present", v.Detail);
    }

    [Fact]
    public void Affected_round_trips_and_dedupes()
    {
        var a = Affected.Of(created: new long[] { 1, 1, 2 });
        var (c, m, d) = Affected.Read(JsonNode.Parse(a.ToJsonString()));
        Assert.Equal(new long[] { 1, 2 }, c);
        Assert.Empty(m);
        Assert.Empty(d);
    }

    [Fact]
    public void Angle_delta_wraps_into_plus_minus_pi()
    {
        Assert.Equal(0.1, ReadBack.AngleDelta(0.1, 0), 9);
        Assert.Equal(-0.1, ReadBack.AngleDelta(2 * Math.PI - 0.1, 0), 9);
        Assert.Equal(Math.PI / 2, ReadBack.AngleDelta(Math.PI / 2 + 4 * Math.PI, 0), 9);
    }
}

public class CanonicalJsonTests
{
    [Fact]
    public void Key_order_does_not_change_the_hash()
    {
        var a = JsonNode.Parse("""{"b":1,"a":{"y":[1,2],"x":"v"}}""");
        var b = JsonNode.Parse("""{"a":{"x":"v","y":[1,2]},"b":1}""");
        Assert.Equal(CanonicalJson.Serialize(a), CanonicalJson.Serialize(b));
        Assert.Equal(CanonicalJson.Sha256(a), CanonicalJson.Sha256(b));
    }

    [Fact]
    public void Different_values_hash_differently_and_hash_is_prefixed()
    {
        var h1 = CanonicalJson.Sha256(JsonNode.Parse("""{"v":"D-101"}"""));
        var h2 = CanonicalJson.Sha256(JsonNode.Parse("""{"v":"D-102"}"""));
        Assert.NotEqual(h1, h2);
        Assert.StartsWith("sha256:", h1);
        Assert.Equal(7 + 64, h1.Length);
    }

    [Fact]
    public void Array_order_matters()
        => Assert.NotEqual(CanonicalJson.Sha256(JsonNode.Parse("[1,2]")), CanonicalJson.Sha256(JsonNode.Parse("[2,1]")));
}

public class AuditConfigTests
{
    [Fact]
    public void No_file_gives_the_documented_defaults()
    {
        var s = AuditConfig.Parse(null);
        Assert.True(s.Enabled);
        Assert.False(s.IncludeReads);
        Assert.Equal(AuditParamsMode.Hash, s.LogParams);
        Assert.Equal(VerifyFailureMode.Report, s.VerifyFailure);
        Assert.EndsWith(Path.Combine("RevitMCP", "audit"), s.Directory);
        Assert.Empty(s.Errors);
    }

    [Fact]
    public void Every_setting_is_read()
    {
        var s = AuditConfig.Parse("""{"enabled":false,"dir":"C:\\x\\audit","includeReads":true,"logParams":"redacted","verifyFailure":"error"}""");
        Assert.False(s.Enabled);
        Assert.True(s.IncludeReads);
        Assert.Equal(@"C:\x\audit", s.Directory);
        Assert.Equal(AuditParamsMode.Redacted, s.LogParams);
        Assert.Equal(VerifyFailureMode.Error, s.VerifyFailure);
        Assert.Empty(s.Errors);
    }

    [Fact]
    public void Bad_values_fall_back_one_by_one_and_are_reported()
    {
        var s = AuditConfig.Parse("""{"enabled":"yes","logParams":"everything","verifyFailure":"panic","dir":5}""");
        Assert.True(s.Enabled);
        Assert.Equal(AuditParamsMode.Hash, s.LogParams);
        Assert.Equal(VerifyFailureMode.Report, s.VerifyFailure);
        Assert.Equal(4, s.Errors.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Malformed_file_uses_defaults_and_says_why(string json)
    {
        var s = AuditConfig.Parse(json);
        Assert.True(s.Enabled);
        Assert.NotEmpty(s.Errors);
    }
}

public class AuditLogTests
{
    private static AuditRecord Sample(JsonNode? parameters = null) => new()
    {
        TimestampUtc = new DateTime(2026, 10, 4, 12, 0, 0, 123, DateTimeKind.Utc),
        Id = "6f1c",
        RevitVersion = "2027",
        DocumentKey = @"C:\Projects\Secret Tower\Tower.rvt",
        Command = "set_parameter",
        Kind = "ModelWrite",
        DryRun = false,
        Ok = true,
        DurationMs = 42,
        Parameters = parameters ?? JsonNode.Parse("""{"id":123456,"parameterName":"Mark","value":"D-101-SECRET"}"""),
        Affected = Affected.Modified(123456),
        Verify = new JsonObject { ["status"] = "failed", ["detail"] = "Mark: expected 'D-101-SECRET', stored 'D-10'" },
        Client = "harness",
        Trace = "run-1/q017",
    };

    [Fact]
    public void Hash_mode_line_has_the_schema_and_no_values_or_document_name()
    {
        var line = AuditLog.Format(Sample(), AuditParamsMode.Hash).ToJsonString();
        foreach (var key in new[] { "ts", "id", "revit", "docHash", "command", "kind", "batchId", "step", "dryRun", "ok",
                                    "errorCode", "durationMs", "paramsHash", "affected", "verify", "client", "trace" })
            Assert.Contains($"\"{key}\":", line);
        Assert.DoesNotContain("D-101-SECRET", line);   // param value and the mismatch detail quoting it
        Assert.DoesNotContain("Secret Tower", line);   // document path
        Assert.DoesNotContain("\"params\":", line);
        Assert.Contains("\"ts\":\"2026-10-04T12:00:00.123Z\"", line);
        Assert.Contains("\"status\":\"failed\"", line);
    }

    [Fact]
    public void Redacted_mode_keeps_keys_and_numbers_but_no_strings()
    {
        var line = AuditLog.Format(Sample(), AuditParamsMode.Redacted).ToJsonString();
        Assert.Contains("\"parameterName\":\"***\"", line);
        Assert.Contains("\"id\":123456", line);
        Assert.DoesNotContain("D-101-SECRET", line);
    }

    [Fact]
    public void Full_mode_keeps_params_and_the_verify_detail()
    {
        var line = AuditLog.Format(Sample(), AuditParamsMode.Full).ToJsonString();
        Assert.Contains("D-101-SECRET", line);
        Assert.DoesNotContain("Secret Tower", line);   // the document stays hashed in every mode
    }

    [Fact]
    public void Writes_one_valid_line_per_record_into_a_daily_utc_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rmcp-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new AuditLog(AuditConfig.Parse(null) with { Directory = dir }, _ => { });
            log.Write(Sample());
            log.Write(Sample());
            var file = Path.Combine(dir, "audit-2026-10-04.jsonl");
            var lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            foreach (var l in lines) Assert.Equal("set_parameter", JsonNode.Parse(l)!["command"]!.GetValue<string>());
            Assert.Null(log.LastError);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void An_unwritable_folder_never_throws_and_is_reported_once()
    {
        // A file where the folder should be: Directory.CreateDirectory fails.
        var blocker = Path.Combine(Path.GetTempPath(), "rmcp-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var printed = 0;
            var log = new AuditLog(AuditConfig.Parse(null) with { Directory = blocker }, _ => printed++);
            log.Write(Sample());
            log.Write(Sample());
            Assert.NotNull(log.LastError);
            Assert.Equal(1, printed);
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void Disabled_log_writes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rmcp-audit-off-" + Guid.NewGuid().ToString("N"));
        var log = new AuditLog(AuditConfig.Parse("""{"enabled":false}""") with { Directory = dir }, _ => { });
        log.Write(Sample());
        Assert.False(Directory.Exists(dir));
    }

    [Theory]
    [InlineData(null, 64, null)]
    [InlineData("   ", 64, null)]
    [InlineData("  harness-A  ", 64, "harness-A")]
    [InlineData("a\u0001b\r\nc", 64, "abc")]
    public void Trace_header_values_are_cleaned(string? raw, int max, string? expected)
        => Assert.Equal(expected, McpHttpServer.CleanHeader(raw, max));

    [Fact]
    public void Trace_header_values_are_cut_to_the_limit()
        => Assert.Equal(128, McpHttpServer.CleanHeader(new string('x', 500), 128)!.Length);
}
