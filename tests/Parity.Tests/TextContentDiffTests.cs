using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 文字內容比對(compare.text):靠圖層名配上、文字卻不一樣的,以前完全沒有提示。
/// 預設是軟落差:會列出、不扣分、不擋。真瀏覽器那半見 GapLabTests 的 G3。
/// </summary>
public class TextContentDiffTests
{
    private static NodeResult Diff(DesignNode design, RenderedNode rendered,
        string matchedBy = "auto-name", TextComparison mode = TextComparison.Soft)
        => new DiffEngine(new Tolerances(), mode).Diff(new NodePair(design, rendered, matchedBy));

    private static DesignNode Copy(string characters)
        => Design("2", "page-title", DesignNodeType.Text, characters: characters);

    [Fact]
    public void A_different_text_is_a_soft_diff_by_default()
    {
        var diff = Assert.Single(Diff(Copy("Parity Demo"), Rendered("h1", "h1", text: "Totally Wrong Words")).Diffs);

        Assert.Equal("text", diff.Prop);
        Assert.Equal("Parity Demo", diff.Expected);
        Assert.Equal("Totally Wrong Words", diff.Actual);
        Assert.True(diff.Soft);
        Assert.Equal(Severity.Minor, diff.Severity);
        Assert.Null(diff.Delta);
    }

    [Theory]
    [InlineData("Parity Demo", "PARITY DEMO")]                   // text-transform 不算
    [InlineData("Parity Demo", "parity   demo")]                 // 空白不算
    [InlineData("歡迎使用\n我們的服務", "歡迎使用我們的服務")]   // 設計稿手動斷行
    [InlineData("Don't miss", "Don’t miss")]                     // 引號樣式
    [InlineData("Inter­national", "International")]         // 軟連字號
    public void Whitespace_case_and_typography_are_not_a_text_diff(string design, string page)
        => Assert.Empty(Diff(Copy(design), Rendered("h1", "h1", text: page)).Diffs);

    [Fact]
    public void Text_inside_child_elements_counts()
    {
        var h1 = Rendered("h1", "h1") with { TextContent = "Title" }; // <h1><span>Title</span></h1>:Text 是 null

        Assert.Empty(Diff(Copy("Title"), h1).Diffs);
    }

    [Fact]
    public void Mixed_content_keeps_its_order()
    {
        // <p>Hello <b>world</b> again</p>:Text 只有 "Hello again",TextContent 才是畫面上的順序
        var p = Rendered("p", "p", text: "Hello again") with { TextContent = "Hello world again" };

        Assert.Empty(Diff(Copy("Hello world again"), p).Diffs);
        Assert.Equal("text", Assert.Single(Diff(Copy("Goodbye world"), p).Diffs).Prop);
    }

    [Fact]
    public void A_design_text_that_is_part_of_a_matched_container_is_not_a_diff()
    {
        // 圖層名配到外層容器:裡面不只這一段,但這段確實在
        var wrapper = Rendered("div.title", "div") with { TextContent = "Title Subtitle" };

        Assert.Empty(Diff(Copy("Title"), wrapper).Diffs);
    }

    [Fact]
    public void A_truncated_text_content_is_not_judged()
    {
        // 擷取端截斷了:沒看完不能說「沒有」
        var long1 = Rendered("div", "div") with { TextContent = new string('x', DiffEngine.TextContentLimit) };

        Assert.Empty(Diff(Copy("Title"), long1).Diffs);
    }

    [Fact]
    public void An_element_with_no_text_at_all_is_missing()
    {
        var diff = Assert.Single(Diff(Copy("Title"), Rendered("h1", "h1")).Diffs);

        Assert.Equal(DiffStatus.Missing, diff.Status);
        Assert.Equal("(no text)", diff.Actual);
        Assert.True(diff.Soft);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("TEXTAREA")]
    [InlineData("img")]
    [InlineData("svg")]
    [InlineData("canvas")]
    public void Elements_whose_text_is_not_in_the_tree_are_skipped(string tag)
        => Assert.Empty(Diff(Copy("Search"), Rendered("x", tag)).Diffs);

    [Fact]
    public void Explicit_matches_are_skipped_because_design_systems_use_placeholder_text()
        => Assert.Empty(Diff(Copy("Button"), Rendered("a", "a", text: "Sign up"), matchedBy: "explicit").Diffs);

    [Fact]
    public void Snapshot_baselines_are_skipped_unless_gated_because_dynamic_content_changes()
    {
        var rendered = Rendered("h1", "h1", text: "Today's headline");

        Assert.Empty(Diff(Copy("Yesterday's headline"), rendered, matchedBy: "selector").Diffs);
        Assert.Equal("text", Assert.Single(Diff(Copy("Yesterday's headline"), rendered, matchedBy: "selector", mode: TextComparison.Gate).Diffs).Prop);
    }

    [Fact]
    public void Gate_mode_is_a_serious_hard_diff()
    {
        var node = Diff(Copy("Parity Demo"), Rendered("h1", "h1", text: "Wrong"), mode: TextComparison.Gate);

        var diff = Assert.Single(node.Diffs);
        Assert.False(diff.Soft);
        Assert.Equal(Severity.Serious, diff.Severity);
        Assert.Equal(Severity.Serious, node.Severity);
    }

    [Fact]
    public void None_mode_does_not_compare()
        => Assert.Empty(Diff(Copy("Parity Demo"), Rendered("h1", "h1", text: "Wrong"), mode: TextComparison.None).Diffs);

    [Fact]
    public void Non_text_nodes_are_not_compared()
        => Assert.Empty(Diff(Design("2", "card", characters: "ignored"), Rendered("div", "div", text: "Other")).Diffs);

    [Fact]
    public void Long_text_is_cut_short_in_the_report()
    {
        // report.json 會被推到伺服器、貼進 PR 留言,整段文字不用都帶
        var diff = Assert.Single(Diff(Copy(new string('a', 300)), Rendered("p", "p", text: new string('b', 300))).Diffs);

        Assert.Equal(DiffEngine.MaxTextInReport, diff.Expected.Length);
        Assert.EndsWith("…", diff.Expected);
        Assert.Equal(DiffEngine.MaxTextInReport, diff.Actual.Length);
    }

    [Fact]
    public void A_line_break_in_the_text_does_not_reach_the_report()
    {
        var diff = Assert.Single(Diff(Copy("first\nsecond"), Rendered("p", "p", text: "other")).Diffs);

        Assert.Equal("first second", diff.Expected);
    }

    [Fact]
    public void Text_content_is_never_serialized()
    {
        // serve 的 /api/detail 把整棵實作樹序列化給本機 UI;這份只給比對用
        var node = Rendered("p", "p") with { TextContent = "secret words" };

        var json = System.Text.Json.JsonSerializer.Serialize(node, ReportWire.Compact);

        Assert.DoesNotContain("secret words", json);
    }
}
