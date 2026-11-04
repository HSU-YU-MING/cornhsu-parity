using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// gate 的配對可信度檢查:全部沒配到 → 沒落差可擋 → 不能給假的 PASS。
/// </summary>
public class GateTests
{
    private static PropDiff Diff(Severity sev = Severity.Serious)
        => new("fontSize", "32", "30", "px", 2, 0.5, sev);

    private static NodeResult Node(string layer, params PropDiff[] diffs)
        => new(layer, layer, "sel", "auto-text", diffs.Length > 0 ? diffs.Max(d => d.Severity) : Severity.None, diffs);

    private static FidelityReport Report(int designNodes, int matched, params NodeResult[] nodes)
    {
        var unmatched = Enumerable.Range(0, designNodes - matched)
            .Select(i => new UnmatchedNode($"u{i}", $"u{i}", "no-anchor")).ToList();
        var summary = new ReportSummary(designNodes, matched, unmatched.Count,
            nodes.Count(n => n.Diffs.Count > 0), 0, 0, 0, 0, Severity.None);
        return new FidelityReport("/", "url", "design", nodes, unmatched, summary);
    }

    // ---------- 0 配對 / 0 設計節點:結果不可信,必擋 ----------

    [Fact]
    public void Zero_matched_fails_gate_even_with_no_diffs()
    {
        var config = new ParityConfig();
        var report = Report(designNodes: 5, matched: 0);

        Assert.True(config.ShouldFail(report));
        Assert.Contains("0/5", config.GateFailReason(report));
    }

    [Fact]
    public void Zero_design_nodes_fails_gate()
    {
        var config = new ParityConfig();
        var report = Report(designNodes: 0, matched: 0);

        Assert.True(config.ShouldFail(report));
        Assert.Contains("0 design nodes", config.GateFailReason(report));
    }

    // ---------- minMatchRate ----------

    [Fact]
    public void Low_match_rate_fails_when_min_match_rate_set()
    {
        var config = new ParityConfig { Gate = { MinMatchRate = 0.5 } };
        Assert.Contains("minMatchRate", config.GateFailReason(Report(designNodes: 4, matched: 1, Node("a"))));
        Assert.Null(config.GateFailReason(Report(designNodes: 4, matched: 3, Node("a"), Node("b"), Node("c"))));
    }

    [Fact]
    public void Partial_match_passes_by_default()
    {
        // 預設不設配對率門檻:只要不是全 0,配一半也照常只看落差
        var config = new ParityConfig();
        Assert.Null(config.GateFailReason(Report(designNodes: 4, matched: 2, Node("a"), Node("b"))));
    }

    // ---------- 原本的 failOn 行為不變 ----------

    [Fact]
    public void Soft_diffs_never_gate_even_when_fail_on_includes_their_severity()
    {
        // 決策 #2(2.0.0):font-family 這種 soft 落差不扣分、也不擋 gate。1.x 在 failOn 含 minor 時照樣
        // GATE FAIL,出現「100/100 卻 GATE FAIL」的矛盾表頭
        var config = new ParityConfig { Gate = { FailOn = ["minor"] } };
        var soft = new PropDiff("fontFamily", "Inter", "Arial", null, null, 0, Severity.Minor, Soft: true);
        var hard = new PropDiff("fontSize", "32", "31", "px", 1, 0.5, Severity.Minor);

        Assert.Null(config.GateFailReason(Report(designNodes: 1, matched: 1, Node("title", soft))));
        Assert.NotNull(config.GateFailReason(Report(designNodes: 1, matched: 1, Node("title", soft, hard))));
    }

    [Fact]
    public void FailOn_severity_still_gates()
    {
        var config = new ParityConfig();
        var failing = Report(designNodes: 2, matched: 2, Node("a", Diff(Severity.Serious)), Node("b"));
        var passing = Report(designNodes: 2, matched: 2, Node("a", Diff(Severity.Minor)), Node("b"));

        Assert.Contains("severity diffs", config.GateFailReason(failing));
        Assert.Null(config.GateFailReason(passing));
    }

    [Fact]
    public void An_empty_fail_on_turns_off_only_the_severity_gate()
    {
        // CHANGELOG 2.0.0 給 1.x「failOn: none」的遷移方式(none 在 2.0.0 是 exit 2):改寫空陣列。
        // 嚴重度不再擋,配對可信度的 gate 照常——0 配對仍然 FAIL
        using var dir = new TempDir();
        var config = ParityConfig.Load(dir.Write("parity.config.json", """{ "gate": { "failOn": [] } }"""));

        Assert.Empty(config.Gate.FailOn);
        Assert.Null(config.GateFailReason(Report(designNodes: 1, matched: 1, Node("a", Diff(Severity.Critical)))));
        Assert.NotNull(config.GateFailReason(Report(designNodes: 1, matched: 0)));
    }

    // ---------- gate.failOnUnmatched:元件不見可以選擇擋 ----------

    private static FidelityReport WithUnmatched(params (string Layer, string Reason)[] unmatched)
    {
        var list = unmatched.Select(u => new UnmatchedNode(u.Layer, u.Layer, u.Reason)).ToList();
        var nodes = new[] { Node("title") };
        return new FidelityReport("/", "url", "design", nodes, list,
            new ReportSummary(nodes.Length + list.Count, nodes.Length, list.Count, 0, 0, 0, 0, 0, Severity.None));
    }

    [Fact]
    public void Unmatched_nodes_never_gate_by_default()
    {
        var config = new ParityConfig();

        Assert.Null(config.GateFailReason(WithUnmatched(("cta-label", "text-not-found"), ("badge", "no-anchor"))));
        Assert.Equal("fail on: critical, serious", config.GateCriteria()); // GATE 那一行與以前一字不差
    }

    [Fact]
    public void Only_the_listed_reasons_gate_and_the_reason_names_the_layers()
    {
        var config = new ParityConfig { Gate = { FailOn = [], FailOnUnmatched = ["text-not-found"] } };

        Assert.Null(config.GateFailReason(WithUnmatched(("badge", "no-anchor"), ("dup", "ambiguous-or-missing-text"))));
        Assert.Equal("1 unmatched design node blocked by gate.failOnUnmatched (text-not-found: cta-label)",
            config.GateFailReason(WithUnmatched(("badge", "no-anchor"), ("cta-label", "text-not-found"))));
        Assert.Equal("fail on unmatched: text-not-found", config.GateCriteria());
    }

    [Fact]
    public void The_star_gates_every_reason_and_long_lists_are_cut()
    {
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["*"] } };
        var many = Enumerable.Range(1, 7).Select(i => ($"t{i}", "text-not-found")).Append(("badge", "no-anchor")).ToArray();

        Assert.Equal("8 unmatched design nodes blocked by gate.failOnUnmatched " +
            "(text-not-found: t1, t2, t3, t4, t5, +2 more; no-anchor: badge)",
            config.GateFailReason(WithUnmatched(many)));
        Assert.Equal("fail on: critical, serious; fail on unmatched: *", config.GateCriteria());
    }

    [Fact]
    public void Reasons_match_in_any_case_like_fail_on()
    {
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["Text-Not-Found"] } };

        Assert.NotNull(config.GateFailReason(WithUnmatched(("cta-label", "text-not-found"))));
    }

    [Fact]
    public void Severity_and_unmatched_reasons_are_both_reported()
    {
        // 兩條都擋時都講出來,看得出各自是哪條
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["no-anchor"] } };
        var unmatched = new UnmatchedNode("badge", "badge", "no-anchor");
        var report = Report(designNodes: 2, matched: 1, Node("a", Diff(Severity.Serious))) with { Unmatched = [unmatched] };

        Assert.Equal("has critical/serious severity diffs; 1 unmatched design node blocked by gate.failOnUnmatched (no-anchor: badge)",
            config.GateFailReason(report));
    }

    [Fact]
    public void Match_integrity_still_wins_over_fail_on_unmatched()
    {
        // 0 配對是「結果不可信」(exit 3),不是元件不見(exit 1):failOnUnmatched 不能把它蓋掉
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["*"] } };
        var report = Report(designNodes: 3, matched: 0);

        Assert.Contains("0/3 design nodes matched", config.GateFailReason(report));
        Assert.DoesNotContain("failOnUnmatched", config.GateFailReason(report));
        Assert.NotNull(config.MatchIntegrityFailure(report));
    }

    [Fact]
    public void Layer_names_in_the_unmatched_reason_cannot_inject_workflow_commands()
    {
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["*"] } };

        var reason = config.GateFailReason(WithUnmatched(("label\n::warning::owned", "text-not-found")))!;

        Assert.DoesNotContain("\n", reason);
        Assert.Contains("label\\n::warning::owned", reason);
    }

    [Fact]
    public void Markdown_notes_list_what_fail_on_unmatched_blocked_per_route()
    {
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["text-not-found"] } };
        var blocked = WithUnmatched(("cta-label", "text-not-found"));
        var other = WithUnmatched(("badge", "no-anchor")) with { Route = "/about" };

        Assert.Equal(["/: 1 unmatched design node blocked by gate.failOnUnmatched (text-not-found: cta-label)"],
            config.UnmatchedGateFailures([blocked, other]));
    }

    // ---------- baseline 模式用的可信度檢查:與 failOn 分離 ----------

    [Fact]
    public void Match_integrity_ignores_severity_diffs()
    {
        // 有 serious 落差但配對健康 → 可信度 OK(baseline 模式下由回歸比對決定過不過)
        var config = new ParityConfig();
        var report = Report(designNodes: 2, matched: 2, Node("a", Diff(Severity.Serious)), Node("b"));

        Assert.Null(config.MatchIntegrityFailure(report));
        Assert.NotNull(config.GateFailReason(report));
    }

    // ---------- Markdown 警示 ----------

    [Fact]
    public void Markdown_shows_gate_notes()
    {
        var report = Report(designNodes: 3, matched: 0);
        var md = MarkdownReport.Render([report], gateFail: true,
            gateNotes: ["/: 0/3 design nodes matched — there is nothing to compare"]);

        Assert.Contains("⚠️", md);
        Assert.Contains("0/3", md);
    }

    // ---------- 設定檔解析 ----------

    [Theory]
    [InlineData("""{ "targets": [{ "route": "/", "url": "x" }], "gate": { "failOn": ["blocker"] } }""", "blocker")]
    [InlineData("""{ "targets": [{ "route": "/", "url": "x" }], "gate": { "minMatchRate": 1.5 } }""", "minMatchRate")]
    public void Invalid_gate_config_fails_at_load_with_friendly_message(string json, string expectInMessage)
    {
        var path = Path.Combine(Path.GetTempPath(), $"parity-badcfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ParityConfig.Load(path));
            Assert.Contains(expectInMessage, ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MinMatchRate_parses_from_config_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parity-gate-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "targets": [{ "route": "/", "url": "x" }], "gate": { "failOn": ["critical"], "minMatchRate": 0.7 } }
            """);
        try
        {
            var config = ParityConfig.Load(path);
            Assert.Equal(0.7, config.Gate.MinMatchRate);
            Assert.Equal(["critical"], config.Gate.FailOn);
        }
        finally { File.Delete(path); }
    }
}
