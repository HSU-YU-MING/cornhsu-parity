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
    public void Score_never_rounds_up_to_a_perfect_100()
    {
        // 野生實查 F5(Stripe 3306/3322 忠實 = 99.5%):四捨五入進成 100,
        // 和 GATE FAIL 同框出現。100 保留給「全部忠實」,差一個節點就是 99。
        var nodes = new List<NodeResult>();
        for (var i = 0; i < 995; i++) nodes.Add(Node($"ok{i}"));
        nodes.Add(Node("bad", Diff("fontSize", "32", "30")));

        Assert.Equal(99, FidelityScore.Compute([Report(996, nodes)]));
    }

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

    [Theory]
    [InlineData("width", "160", "width: 160px")]
    [InlineData("height", "48", "height: 48px")]
    [InlineData("paddingTop", "12", "padding-top: 12px")]
    [InlineData("paddingRight", "20", "padding-right: 20px")]
    [InlineData("paddingBottom", "12", "padding-bottom: 12px")]
    [InlineData("fontWeight", "700", "font-weight: 700")]
    [InlineData("lineHeight", "24", "line-height: 24px")]
    [InlineData("letterSpacing", "0.5", "letter-spacing: 0.5px")]
    [InlineData("color", "#111827", "color: #111827")]
    [InlineData("fontFamily", "Inter", "font-family should include Inter")]
    [InlineData("offsetX", "16", "horizontal offset from its sibling/parent should be 16px")]
    [InlineData("offsetY", "8", "vertical offset from its sibling/parent should be 8px")]
    public void Fix_hint_maps_the_remaining_props_to_css(string prop, string expected, string hint)
        => Assert.Equal(hint, FixHint.For(Diff(prop, expected, "x")));

    [Fact]
    public void Every_prop_the_diff_engine_emits_has_a_fix_hint()
    {
        // 上面兩張表寫死了現有的 prop;DiffEngine 新增一個 prop 卻忘了補 FixHint 時,
        // FixHint 回 null、報告那格靜靜空白,表格不會紅。所以直接掃 DiffEngine.cs(去掉註解)
        // 裡的 camelCase 字串常值——prop 名稱都長這樣,其他常值(HUG/FILL、"0.##")不是。
        // 新常值若根本不是 prop,把它加進 notProps 並寫一行理由
        string[] notProps =
        [
            "px", // PropDiff 的 Unit 欄位
        ];
        var code = string.Join('\n', File.ReadAllLines(Repo.PathOf("src", "Parity.Engine", "Comparison", "DiffEngine.cs"))
            .Select(l => l.IndexOf("//", StringComparison.Ordinal) is var i and >= 0 ? l[..i] : l));
        var props = System.Text.RegularExpressions.Regex.Matches(code, "\"([a-z][a-zA-Z]*)\"")
            .Select(m => m.Groups[1].Value).Except(notProps).Distinct().Order().ToList();

        // 掃描本身要有效:regex 對不上時會安靜地變成「零項通過」
        Assert.Contains("paddingLeft", props);
        Assert.Contains("fontFamily", props);
        Assert.DoesNotContain(props, p => FixHint.For(Diff(p, "1", "2")) is null);
    }

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
    public void Markdown_faithful_count_matches_score_for_soft_only_nodes()
    {
        // 只有軟落差的節點:分數不扣分 → 表頭計數也要算「忠實」,不然標題自相矛盾
        var softOnly = Node("softOnly", Diff("fontFamily", "Arial", "Helvetica", Severity.Minor, soft: true));
        var md = MarkdownReport.Render([Report(2, [softOnly, Node("clean")])], gateFail: false);

        Assert.Contains("Fidelity 100/100", md);
        Assert.Contains("2/2 design nodes implemented faithfully", md);
    }

    [Fact]
    public void Markdown_has_score_gate_and_fix_hints()
    {
        var report = Report(2, [Node("cta", Diff("paddingLeft", "20", "8")), Node("ok")]);
        var md = MarkdownReport.Render([report], gateFail: true);

        Assert.Contains("Fidelity 50/100", md);
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

        Assert.Contains("vs baseline", md);
        Assert.Contains("new **1**", md);
    }

    [Fact]
    public void Markdown_links_layers_to_figma_when_filekey_given()
    {
        // 設計來源是 Figma → 圖層名連回該節點(node-id 的 ":" 要換 "-");沒給 fileKey 就是純文字
        var node = new NodeResult("cta", "10:2", "sel", "auto-text", Severity.Serious,
            [Diff("fontSize", "32", "30")]);
        var report = Report(2, [node], [new UnmatchedNode("badge", "10:7", "no-anchor")]);

        var linked = MarkdownReport.Render([report], gateFail: true, figmaFileKey: "abcKey");
        Assert.Contains("[`cta`](https://www.figma.com/design/abcKey?node-id=10-2)", linked);
        Assert.Contains("[`badge`](https://www.figma.com/design/abcKey?node-id=10-7)", linked);

        var plain = MarkdownReport.Render([report], gateFail: true);
        Assert.DoesNotContain("figma.com", plain);
    }

    [Fact]
    public void Markdown_shows_score_trend_against_baseline()
    {
        // 2 個節點 1 個乾淨 → 50 分;基準 75 → 顯示走勢往下
        var report = Report(2, [Node("bad", Diff("fontSize", "32", "30")), Node("ok")]);
        var md = MarkdownReport.Render([report], gateFail: true, baselineScore: 75);
        Assert.Contains("**Fidelity 50/100** (baseline 75 ↓ -25)", md);
    }

    [Theory]
    [InlineData(25, "(baseline 25 ↑ +25)")]
    [InlineData(50, "(baseline 50 → ±0)")]
    public void Markdown_score_trend_also_reads_up_and_flat(int baselineScore, string trend)
    {
        var report = Report(2, [Node("bad", Diff("fontSize", "32", "30")), Node("ok")]); // 50 分
        Assert.Contains($"**Fidelity 50/100** {trend}", MarkdownReport.Render([report], gateFail: true, baselineScore: baselineScore));
    }

    [Fact]
    public void Markdown_lists_new_worsened_and_fixed_items_under_their_own_headings()
    {
        // reviewer 在 PR 留言裡最先看的是「這次變了什麼」:三類各自一段,空的那類不出標題
        var report = Report(1, [Node("cta", Diff("color", "#fff", "#000"))]);
        DiffRecord Rec(string layer, string prop) => new("/", layer, "sel", prop, Severity.Serious, "12", "8");

        var all = MarkdownReport.Render([report], gateFail: true, baseline: new BaselineComparison(
            Regressions: [Rec("cta", "paddingTop")], Worsened: [Rec("hero", "height")], Fixed: [Rec("nav", "itemSpacing")], Unchanged: 2));

        Assert.Contains("vs baseline: 🔴 new **1**, 🟠 worsened **1**, 🟢 fixed **1**, ⚪ unchanged 2", all);
        Assert.Contains("**🔴 New diffs**\n- `cta` · paddingTop · `12` → `8`", all.ReplaceLineEndings("\n"));
        Assert.Contains("**🟠 Worsened**\n- `hero` · height · `12` → `8`", all.ReplaceLineEndings("\n"));
        Assert.Contains("**🟢 Fixed**\n- `nav` · itemSpacing · `12` → `8`", all.ReplaceLineEndings("\n"));

        var onlyFixed = MarkdownReport.Render([report], gateFail: false, baseline: new BaselineComparison(
            Regressions: [], Worsened: [], Fixed: [Rec("nav", "itemSpacing")], Unchanged: 0));
        Assert.Contains("**🟢 Fixed**", onlyFixed);
        Assert.DoesNotContain("**🔴 New diffs**", onlyFixed);
        Assert.DoesNotContain("**🟠 Worsened**", onlyFixed);
    }

    [Fact]
    public void Markdown_pipe_in_value_is_escaped()
    {
        // 值裡有 | 不能破壞表格
        var report = Report(1, [Node("x", Diff("fontFamily", "A|B", "C"))]);
        var md = MarkdownReport.Render([report], gateFail: false);
        Assert.Contains("A\\|B", md);
    }

    [Fact]
    public void Markdown_for_several_routes_adds_a_route_column_and_prefixes_unmatched()
    {
        // 多頁的 PR 留言:同名圖層(Header/Footer)出現在每一頁,沒有 route 欄分不出是哪一頁
        var home = Report(2, [Node("Header", Diff("height", "64", "80"))], [new UnmatchedNode("Badge", "b", "no-anchor")], route: "/");
        var pricing = Report(1, [Node("Header", Diff("height", "64", "72"))], route: "/pricing");

        var md = MarkdownReport.Render([home, pricing], gateFail: true);

        Assert.Contains("| Route | Element | Property | Expected → Actual | Severity | Suggested fix |", md);
        Assert.Contains("| / | `Header` | height |", md);
        Assert.Contains("| /pricing | `Header` | height |", md);
        Assert.Contains("- `/` `Badge` — no-anchor", md);

        var single = MarkdownReport.Render([home], gateFail: true);
        Assert.Contains("| Element | Property | Expected → Actual | Severity | Suggested fix |", single);
        Assert.DoesNotContain("| Route |", single);
    }

    [Fact]
    public void Markdown_soft_diffs_are_labelled_soft()
    {
        var report = Report(1, [Node("title", Diff("fontFamily", "Inter", "Arial", Severity.Minor, soft: true))]);

        Assert.Contains("| minor(soft) |", MarkdownReport.Render([report], gateFail: false));
    }

    [Fact]
    public void Tokens_file_loading_is_lenient_about_missing_or_empty_files()
    {
        using var dir = new TempDir();

        Assert.Null(DesignTokens.LoadJson(dir.File("nope.json")));
        Assert.Null(DesignTokens.LoadJson(dir.Write("empty.json", "{}")));
        var tokens = DesignTokens.LoadJson(dir.Write("ok.json", """{ "brand": "#2563EB", "space-2": "8px", "font": "Inter" }"""));
        Assert.NotNull(tokens);
        Assert.Equal(["brand"], tokens.Colors.Select(c => c.Name));
        Assert.Equal(["space-2"], tokens.Sizes.Select(s => s.Name)); // 既不是顏色也不是 px 的值被略過
    }

    [Fact]
    public void Tokens_file_that_fails_to_parse_is_currently_treated_as_missing()
    {
        // 固定現況,不是規格(ROADMAP「已知盲點」、盤點第 9 節):LoadJson 用 catch 把解析錯誤吞成 null,
        // lint 接著叫使用者去「set `tokensFile` in the config」——但使用者明明設了。
        // 改成回報解析錯誤時,把這條換成預期丟出帶檔名與錯誤位置的例外
        using var dir = new TempDir();

        Assert.Null(DesignTokens.LoadJson(dir.Write("broken.json", "{ not json")));
        Assert.Null(DesignTokens.LoadJson(dir.Write("number.json", """{ "space-2": 8 }""")));
    }
}
