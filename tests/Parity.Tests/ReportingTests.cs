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

    [Fact]
    public void Score_ignores_pure_soft_diff_nodes()
    {
        // 只有軟落差(font-family)的節點算「忠實」,不扣分(與 gate 判定一致)
        var softOnly = Node("softOnly", Diff("fontFamily", "Arial", "Helvetica", Severity.Minor, soft: true));
        var report = Report(2, [softOnly, Node("clean")]);
        Assert.Equal(100, FidelityScore.Compute([report]));
    }

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

    [Fact]
    public void Fix_hint_references_token_when_expected_matches()
    {
        var tokens = new DesignTokens(new Dictionary<string, string>
        {
            ["color-primary"] = "#2563EB",
            ["space-6"] = "24px",
        });
        Assert.Equal("background: #2563EB(token: color-primary)", FixHint.For(Diff("background", "#2563EB", "#000"), tokens));
        Assert.Equal("gap: 24px(token: space-6)", FixHint.For(Diff("itemSpacing", "24", "16"), tokens));
        // 沒對到 token → 維持純 CSS
        Assert.Equal("font-size: 15px", FixHint.For(Diff("fontSize", "15", "20"), tokens));
    }

    [Fact]
    public void Token_size_index_does_not_leak_into_font_weight()
    {
        // 700px 的 size token 不該誤配到 font-weight 700
        var tokens = new DesignTokens(new Dictionary<string, string> { ["weight-bold"] = "700px" });
        Assert.Equal("font-weight: 700", FixHint.For(Diff("fontWeight", "700", "400"), tokens));
    }

    // ---------- 衝擊度排序 ----------

    [Fact]
    public void Impact_orders_by_severity_then_area()
    {
        NodeResult N(string layer, Severity sev, double w, double h)
            => new(layer, layer, "sel", "m", sev, [Diff("width", "1", "2")],
                default, new Parity.Engine.Model.Box(0, 0, w, h));

        var minorBig = N("minorBig", Severity.Minor, 100, 100);      // 面積 10000,但只 minor
        var seriousSmall = N("seriousSmall", Severity.Serious, 10, 10); // 面積 100
        var seriousBig = N("seriousBig", Severity.Serious, 200, 200);   // 面積 40000

        var ordered = Impact.Order([minorBig, seriousSmall, seriousBig]).Select(n => n.DesignLayer).ToList();

        // 嚴重度優先(serious 都在 minor 前);同嚴重度時面積大的先
        Assert.Equal(["seriousBig", "seriousSmall", "minorBig"], ordered);
    }

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
