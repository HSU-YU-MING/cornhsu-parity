using Parity.Engine;

namespace Parity.Tests;

public class ReportingTests
{
    private static PropDiff Diff(string prop, string exp, string act, Severity sev = Severity.Serious, bool soft = false)
        => new(prop, exp, act, null, null, 2, sev, Soft: soft);

    private static NodeResult Node(string layer, params PropDiff[] diffs)
        => new(layer, layer, "sel", "auto-text", diffs.Length > 0 ? Severity.Serious : Severity.None, diffs);

    private static FidelityReport Report(int designNodes, IReadOnlyList<NodeResult>? nodes = null,
        IReadOnlyList<UnmatchedNode>? unmatched = null, string route = "/")
    {
        nodes ??= [];
        unmatched ??= [];
        var summary = new ReportSummary(designNodes, nodes.Count, unmatched.Count,
            nodes.Count(n => n.Diffs.Count > 0), 0, 0, 0, 0, Severity.None);
        return new FidelityReport(route, "url", "design", nodes, unmatched, summary);
    }

    // ---------- 分數 ----------

    [Fact]
    public void Score_is_percentage_of_clean_matched_nodes()
    {
        // 12 個設計節點,9 個乾淨(有配對且無落差)→ 75
        var nodes = new List<NodeResult>();
        for (var i = 0; i < 9; i++) nodes.Add(Node($"ok{i}"));
        for (var i = 0; i < 3; i++) nodes.Add(Node($"bad{i}", Diff("fontSize", "32", "30")));

        Assert.Equal(75, FidelityScore.Compute([Report(12, nodes)]));
    }

    [Fact]
    public void Score_penalizes_unmatched_nodes()
    {
        // 4 個設計節點,只有 2 個配上且乾淨,2 個未配對 → 50
        var report = Report(4, [Node("a"), Node("b")], [new UnmatchedNode("c", "c", "no-anchor"), new UnmatchedNode("d", "d", "no-anchor")]);
        Assert.Equal(50, FidelityScore.Compute([report]));
    }

    [Fact]
    public void Empty_reports_score_100() => Assert.Equal(100, FidelityScore.Compute([]));

    // ---------- 修法建議 ----------

    [Theory]
    [InlineData("paddingLeft", "16", "padding-left: 16px")]
    [InlineData("fontSize", "32", "font-size: 32px")]
    [InlineData("background", "#2563EB", "background: #2563EB")]
    [InlineData("itemSpacing", "24", "gap: 24px")]
    [InlineData("cornerRadius", "8", "border-radius: 8px")]
    public void Fix_hint_maps_prop_to_css(string prop, string expected, string hint)
        => Assert.Equal(hint, FixHint.For(Diff(prop, expected, "x")));

    [Fact]
    public void Fix_hint_null_for_unknown_prop()
        => Assert.Null(FixHint.For(Diff("mysteryProp", "1", "2")));

    // ---------- Markdown ----------

    [Fact]
    public void Markdown_has_score_gate_and_fix_hints()
    {
        var report = Report(2, [Node("cta", Diff("paddingLeft", "20", "8")), Node("ok")]);
        var md = MarkdownReport.Render([report], gateFail: true);

        Assert.Contains("還原度 50/100", md);
        Assert.Contains("GATE FAIL", md);
        Assert.Contains("`cta`", md);
        Assert.Contains("padding-left: 20px", md); // 建議修法
    }

    [Fact]
    public void Markdown_includes_baseline_change_section()
    {
        var report = Report(1, [Node("cta", Diff("color", "#fff", "#000"))]);
        var cmp = new BaselineComparison(
            Regressions: [new DiffRecord("/", "cta", "sel", "color", Severity.Serious, "#fff", "#000")],
            Worsened: [], Fixed: [], Unchanged: 0);

        var md = MarkdownReport.Render([report], gateFail: true, baseline: cmp);

        Assert.Contains("相對基準", md);
        Assert.Contains("新增 **1**", md);
    }

    [Fact]
    public void Markdown_pipe_in_value_is_escaped()
    {
        // 值裡有 | 不能破壞表格
        var report = Report(1, [Node("x", Diff("fontFamily", "A|B", "C"))]);
        var md = MarkdownReport.Render([report], gateFail: false);
        Assert.Contains("A\\|B", md);
    }
}
