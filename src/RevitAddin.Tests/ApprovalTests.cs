using System;
using System.Text.Json.Nodes;
using RevitMCPAddin;
using RevitMCPAddin.Commands;
using RevitMCPAddin.Server;
using Xunit;

namespace RevitMCPAddin.Tests;

public class ApprovalStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static (ApprovalStore Store, Action<double> Advance) Clocked()
    {
        var now = T0;
        var store = new ApprovalStore(TimeSpan.FromSeconds(300), () => now);
        return (store, s => now = now.AddSeconds(s));
    }

    private static string Bind(string value = "D-101", string doc = @"C:\m\a.rvt") =>
        ApprovalBinding.ForCommand("set_parameter",
            new JsonObject { ["id"] = 7, ["parameterName"] = "Mark", ["value"] = value }, doc);

    [Fact]
    public void A_token_approves_its_request_once()
    {
        var (store, _) = Clocked();
        var (token, expires) = store.Issue(Bind());
        Assert.StartsWith("apv_", token);
        Assert.Equal(T0.AddSeconds(300), expires);

        Assert.True(store.Consume(token, Bind()).Ok);
        var again = store.Consume(token, Bind());
        Assert.False(again.Ok);
        Assert.Equal(ApprovalCheck.Mismatch, again.Code);
        Assert.Contains("already used", again.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_token_is_approval_required(string? token)
    {
        var (store, _) = Clocked();
        var r = store.Consume(token, Bind());
        Assert.False(r.Ok);
        Assert.Equal(ApprovalCheck.Required, r.Code);
        Assert.Contains("dryRun", r.Message);
    }

    [Fact]
    public void Changed_params_do_not_fit_and_do_not_burn_the_token()
    {
        var (store, _) = Clocked();
        var (token, _) = store.Issue(Bind("D-101"));

        var r = store.Consume(token, Bind("D-999"));
        Assert.False(r.Ok);
        Assert.Equal(ApprovalCheck.Mismatch, r.Code);
        Assert.Contains("different request", r.Message);

        // The request that was actually previewed still goes through.
        Assert.True(store.Consume(token, Bind("D-101")).Ok);
    }

    [Fact]
    public void Another_document_does_not_fit()
    {
        var (store, _) = Clocked();
        var (token, _) = store.Issue(Bind(doc: @"C:\m\a.rvt"));
        Assert.Equal(ApprovalCheck.Mismatch, store.Consume(token, Bind(doc: @"C:\m\b.rvt")).Code);
    }

    [Fact]
    public void Expired_token_is_mismatch_and_removed()
    {
        var (store, advance) = Clocked();
        var (token, _) = store.Issue(Bind());
        advance(300);
        Assert.True(store.Consume(token, Bind()).Ok); // exactly at the TTL it is still valid
        var (token2, _) = store.Issue(Bind());
        advance(301);
        var r = store.Consume(token2, Bind());
        Assert.False(r.Ok);
        Assert.Contains("expired", r.Message);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Valid_until_the_ttl_and_not_after()
    {
        var (store, advance) = Clocked();
        var (a, _) = store.Issue(Bind());
        var (b, _) = store.Issue(Bind());
        advance(299);
        Assert.True(store.Consume(a, Bind()).Ok);
        advance(2);
        Assert.False(store.Consume(b, Bind()).Ok);
    }

    [Fact]
    public void Unknown_token_is_mismatch()
    {
        var (store, _) = Clocked();
        Assert.Equal(ApprovalCheck.Mismatch, store.Consume("apv_00000000000000000000000000000000", Bind()).Code);
    }

    [Fact]
    public void Tokens_are_random_not_derived_from_the_request()
    {
        var (store, _) = Clocked();
        Assert.NotEqual(store.Issue(Bind()).Token, store.Issue(Bind()).Token);
    }

    [Fact]
    public void Outstanding_tokens_are_capped()
    {
        var (store, _) = Clocked();
        for (var i = 0; i < 1200; i++) store.Issue(Bind(i.ToString()));
        Assert.Equal(1000, store.Count);
    }

    [Fact]
    public void Fingerprint_is_short_stable_and_not_the_token()
    {
        var fp = ApprovalStore.Fingerprint("apv_abc");
        Assert.Equal(16, fp.Length);
        Assert.Equal(fp, ApprovalStore.Fingerprint("apv_abc"));
        Assert.DoesNotContain("abc", fp);
    }
}

public class ApprovalBindingTests
{
    [Fact]
    public void Param_key_order_does_not_matter()
        => Assert.Equal(
            ApprovalBinding.ForCommand("x", JsonNode.Parse("""{"a":1,"b":2}""")!.AsObject(), "d"),
            ApprovalBinding.ForCommand("x", JsonNode.Parse("""{"b":2,"a":1}""")!.AsObject(), "d"));

    [Fact]
    public void Command_name_matters()
        => Assert.NotEqual(ApprovalBinding.ForCommand("a", null, "d"), ApprovalBinding.ForCommand("b", null, "d"));

    [Fact]
    public void Missing_params_equal_empty_params()
        => Assert.Equal(ApprovalBinding.ForCommand("a", null, "d"), ApprovalBinding.ForCommand("a", new JsonObject(), "d"));

    [Fact]
    public void Batch_binds_steps_order_and_stopOnError()
    {
        var s1 = ("create_level", (JsonObject?)new JsonObject { ["elevation"] = 3 });
        var s2 = ("create_grid", (JsonObject?)new JsonObject { ["name"] = "Z" });
        var baseline = ApprovalBinding.ForBatch(new[] { s1, s2 }, true, "d");
        Assert.Equal(baseline, ApprovalBinding.ForBatch(new[] { s1, s2 }, true, "d"));
        Assert.NotEqual(baseline, ApprovalBinding.ForBatch(new[] { s2, s1 }, true, "d"));
        Assert.NotEqual(baseline, ApprovalBinding.ForBatch(new[] { s1, s2 }, false, "d"));
        Assert.NotEqual(baseline, ApprovalBinding.ForBatch(new[] { s1 }, true, "d"));
        Assert.NotEqual(baseline, ApprovalBinding.ForCommand("batch", null, "d"));
    }
}

public class ApprovalHostTests
{
    [Fact]
    public void Status_codes()
    {
        Assert.Equal(428, McpHttpServer.StatusForResult(JsonResult.Error("approval_required", "x")));
        Assert.Equal(409, McpHttpServer.StatusForResult(JsonResult.Error("approval_mismatch", "x")));
    }

    [Fact]
    public void Token_is_read_from_the_body_top_level_only()
    {
        var meta = new RequestMeta("r1", "c", "t");
        var withToken = McpHttpServer.WithApproval(meta,
            JsonNode.Parse("""{"command":"x","params":{},"approvalToken":"  apv_1  "}""")!.AsObject());
        Assert.Equal("apv_1", withToken.ApprovalToken);
        Assert.Equal("c", withToken.Client);

        var inParams = McpHttpServer.WithApproval(meta,
            JsonNode.Parse("""{"command":"x","params":{"approvalToken":"apv_1"}}""")!.AsObject());
        Assert.Null(inParams.ApprovalToken);

        var notString = McpHttpServer.WithApproval(meta, JsonNode.Parse("""{"approvalToken":5}""")!.AsObject());
        Assert.Null(notString.ApprovalToken);
    }

    [Fact]
    public void Config_defaults_to_direct_and_reads_preview_required()
    {
        Assert.Equal(MutationMode.Direct, AuditConfig.Parse(null).MutationMode);
        var s = AuditConfig.Parse("""{"mutationMode":"preview_required"}""");
        Assert.Equal(MutationMode.PreviewRequired, s.MutationMode);
        Assert.Empty(s.Errors);
    }

    [Fact]
    public void Bad_mutation_mode_falls_back_to_direct_and_is_reported()
    {
        var s = AuditConfig.Parse("""{"mutationMode":"ask_me"}""");
        Assert.Equal(MutationMode.Direct, s.MutationMode);
        Assert.Single(s.Errors);
    }

    [Fact]
    public void Audit_line_carries_the_approval_fingerprint_never_the_token()
    {
        var line = AuditLog.Format(new AuditRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Command = "set_parameter",
            Approval = new JsonObject { ["state"] = "consumed", ["token"] = ApprovalStore.Fingerprint("apv_secret") },
        }, AuditParamsMode.Full);
        Assert.Equal("consumed", line["approval"]!["state"]!.GetValue<string>());
        Assert.DoesNotContain("apv_secret", line.ToJsonString());

        var none = AuditLog.Format(new AuditRecord { TimestampUtc = DateTime.UtcNow, Command = "x" }, AuditParamsMode.Hash);
        Assert.True(none.ContainsKey("approval"));
        Assert.Null(none["approval"]);
    }
}
