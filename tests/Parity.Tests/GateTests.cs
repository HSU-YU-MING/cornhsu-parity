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
        Assert.Contains("設計端 0 個節點", config.GateFailReason(report));
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
    public void FailOn_severity_still_gates()
    {
        var config = new ParityConfig();
        var failing = Report(designNodes: 2, matched: 2, Node("a", Diff(Severity.Serious)), Node("b"));
        var passing = Report(designNodes: 2, matched: 2, Node("a", Diff(Severity.Minor)), Node("b"));

        Assert.Contains("等級落差", config.GateFailReason(failing));
        Assert.Null(config.GateFailReason(passing));
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
            gateNotes: ["/:0/3 個設計節點配對成功——沒有東西可比"]);

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
