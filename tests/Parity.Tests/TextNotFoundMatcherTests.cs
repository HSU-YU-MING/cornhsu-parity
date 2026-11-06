using System.Text.Json;
using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 未配對原因 text-not-found:設計稿的文字整頁都找不到(元件不見、文字被改掉),跟「有但分不出來」
/// (ambiguous-or-missing-text)分開——gate.failOnUnmatched 才擋得準。判斷靠擷取端給的整頁文字
/// (RenderedNode.PageText);沒有整頁文字(假來源、舊測試)時原因完全照舊,MatcherTests 一條都不用改。
/// 真瀏覽器那半(整頁文字收不收得到 shadow DOM、iframe……)見 PageTextCaptureTests。
/// </summary>
public class TextNotFoundMatcherTests
{
    private static RenderedNode Page(string? pageText, params RenderedNode[] children)
        => Rendered("body", children: children) with { PageText = pageText };

    private static string ReasonOf(DesignNode textNode, RenderedNode rendered)
        => Assert.Single(Matcher.Match(Design("1", "frame", children: textNode), rendered).Unmatched).Reason;

    [Fact]
    public void Text_absent_from_the_whole_page_is_text_not_found()
    {
        var label = Design("2", "cta-label", DesignNodeType.Text, characters: "Get Started");

        Assert.Equal("text-not-found", ReasonOf(label, Page("Parity Demo\nLocal first")));
    }

    [Fact]
    public void An_empty_layer_name_does_not_hide_a_missing_text()
    {
        // 圖層名正規化後是空字串(「—」、純符號)原本一律 no-anchor:文字真的不見時同樣該講清楚
        var label = Design("2", "—", DesignNodeType.Text, characters: "Get Started");

        Assert.Equal("text-not-found", ReasonOf(label, Page("Parity Demo")));
        Assert.Equal("no-anchor", ReasonOf(label, Page("Parity Demo · Get Started"))); // 文字在 → 照舊
    }

    [Theory]
    [InlineData("Hello world")]              // 跨元素斷開:Hello <b>world</b>,byText 只有 "Hello" 和 "world"
    [InlineData("HELLO   WORLD")]            // text-transform: uppercase、多空白
    [InlineData("say hello\n world again")]  // 換行也是空白
    public void Text_present_somewhere_on_the_page_keeps_the_old_reason(string pageText)
    {
        var greeting = Design("2", "greeting", DesignNodeType.Text, characters: "Hello world");
        var rendered = Page(pageText,
            Rendered("body > p", "p", text: "Hello"),
            Rendered("body > p > b", "b", text: "world"));

        Assert.Equal("ambiguous-or-missing-text", ReasonOf(greeting, rendered));
    }

    [Theory]
    [InlineData("歡迎使用\n我們的服務", "歡迎使用我們的服務")]    // 設計稿手動斷行,頁面是一整行:中文沒有字間空白
    [InlineData("歡迎使用我們的服務", "歡迎使用\n我們的服務")]    // 反過來:頁面斷行(<br>、區塊元素),設計稿一整行
    [InlineData("Price: $10", "Price:$10")]                       // 行內元素靠 margin 隔開,innerText 沒有空白
    [InlineData("Don't miss", "Don’t miss")]                 // 設計稿直引號,頁面彎引號
    [InlineData("Don’t miss", "Don't miss")]                 // 反過來
    [InlineData("“Quoted”", "\"Quoted\"")]              // 雙引號
    public void A_whitespace_or_quote_difference_is_not_a_missing_text(string characters, string pageText)
    {
        // 開了 gate.failOnUnmatched 就會擋 PR:文字看得到卻標 text-not-found 是誤擋。文案差異不是這條要抓的
        var copy = Design("2", "copy-under-test", DesignNodeType.Text, characters: characters);

        Assert.Equal("ambiguous-or-missing-text", ReasonOf(copy, Page(pageText)));
    }

    [Theory]
    [InlineData("歡迎使用我們的服務", "歡迎光臨我們的服務")]      // 中文真的不一樣
    [InlineData("Don't miss", "Do not miss")]                     // 忽略引號不等於忽略文字
    public void A_really_different_text_is_still_not_found(string characters, string pageText)
    {
        var copy = Design("2", "copy-under-test", DesignNodeType.Text, characters: characters);

        Assert.Equal("text-not-found", ReasonOf(copy, Page(pageText)));
    }

    // ---------- 配對:精確比對對不到時,退回放寬的文字比對 ----------

    private static MatchResult MatchOne(DesignNode textNode, params RenderedNode[] children)
        => Matcher.Match(Design("1", "frame", children: textNode), Rendered("body", children: children));

    [Theory]
    [InlineData("歡迎使用\n我們的服務", "歡迎使用我們的服務")]   // 設計稿手動斷行,頁面是一整行
    [InlineData("歡迎使用\n\n我們的服務", "歡迎使用我們的服務")] // 空一行
    [InlineData("歡迎使用我們的服務", "歡迎使用 我們的服務")]    // 反過來:原始碼換行,自己的文字壓成一個空白
    [InlineData("Don't miss", "Don’t miss")]                     // 設計稿直引號,頁面彎引號
    [InlineData("Inter­national", "International")]         // 軟連字號
    public void Text_that_only_differs_by_whitespace_or_typography_is_paired(string characters, string pageText)
    {
        var copy = Design("2", "copy-under-test", DesignNodeType.Text, characters: characters);
        var p = Rendered("body > p", "p", text: pageText);

        var result = MatchOne(copy, p);

        Assert.Empty(result.Unmatched);
        var pair = Assert.Single(result.Pairs);
        Assert.Same(p, pair.Rendered);
        Assert.Equal("auto-text", pair.MatchedBy);
    }

    [Fact]
    public void The_exact_text_wins_over_the_relaxed_one()
    {
        // 頁面同時有 "AB" 和 "A B":設計稿寫 "A B" 就是 "A B",不能因為放寬而變成兩個候選、配不起來
        var copy = Design("2", "copy-under-test", DesignNodeType.Text, characters: "A\nB");
        var spaced = Rendered("body > p:nth-of-type(1)", "p", text: "A B");
        var joined = Rendered("body > p:nth-of-type(2)", "p", text: "AB");

        var pair = Assert.Single(MatchOne(copy, spaced, joined).Pairs);

        Assert.Same(spaced, pair.Rendered);
    }

    [Fact]
    public void The_relaxed_text_still_has_to_be_unique()
    {
        // 放寬不等於猜:兩個候選分不出來就不配,維持未配對
        var copy = Design("2", "copy-under-test", DesignNodeType.Text, characters: "歡迎使用\n我們\n的服務");
        var result = MatchOne(copy,
            Rendered("body > p:nth-of-type(1)", "p", text: "歡迎使用我們的服務"),
            Rendered("body > p:nth-of-type(2)", "p", text: "歡迎使用 我們的服務"));

        Assert.Empty(result.Pairs);
        Assert.Equal("ambiguous-or-missing-text", Assert.Single(result.Unmatched).Reason);
    }

    [Fact]
    public void A_really_different_text_is_not_paired_by_the_relaxed_comparison()
    {
        var copy = Design("2", "copy-under-test", DesignNodeType.Text, characters: "歡迎使用\n我們的服務");

        Assert.Empty(MatchOne(copy, Rendered("body > p", "p", text: "歡迎光臨我們的服務")).Pairs);
    }

    [Fact]
    public void Duplicate_text_that_cannot_be_disambiguated_stays_ambiguous()
    {
        var edit = Design("2", "label-a", DesignNodeType.Text, characters: "Edit");
        var rendered = Page("Edit\nEdit",
            Rendered("body > button:nth-of-type(1)", "button", text: "Edit"),
            Rendered("body > button:nth-of-type(2)", "button", text: "Edit"));

        Assert.Equal("ambiguous-or-missing-text", ReasonOf(edit, rendered));
    }

    [Fact]
    public void Without_page_text_every_reason_is_exactly_the_old_one()
    {
        // 假來源與舊測試沒有整頁文字:不能因為「不知道」就說「找不到」
        var label = Design("2", "cta-label", DesignNodeType.Text, characters: "Get Started");
        var symbol = Design("2", "—", DesignNodeType.Text, characters: "Get Started");

        Assert.Equal("ambiguous-or-missing-text", ReasonOf(label, Page(null)));
        Assert.Equal("no-anchor", ReasonOf(symbol, Page(null)));
    }

    [Fact]
    public void Nodes_without_text_and_size_implausible_names_keep_their_reasons()
    {
        // 只看「有非空 Characters 的設計節點」;其餘原因不受整頁文字影響
        var badge = Design("2", "hero-badge", box: new Box(0, 0, 120, 32));
        var blank = Design("2", "spacer", DesignNodeType.Text, characters: "   ");
        var content = Design("2", "Content", DesignNodeType.Text, box: new Box(0, 0, 1000, 1000), characters: "Lorem");
        var tiny = Rendered("body > div#content", domId: "content", box: new Box(0, 0, 10, 10));

        Assert.Equal("no-anchor", ReasonOf(badge, Page("")));
        Assert.Equal("no-anchor", ReasonOf(blank, Page("")));
        Assert.Equal("size-implausible", ReasonOf(content, Page("", tiny)));
    }

    [Fact]
    public void A_text_found_only_across_two_page_text_segments_is_not_found()
    {
        // 擷取端用 \u001f 隔開各段(innerText / 組合樹走訪):子字串不能橫跨兩段,否則尾巴接頭會湊出假的命中
        var label = Design("2", "cta-label", DesignNodeType.Text, characters: "first second");

        Assert.Equal("text-not-found", ReasonOf(label, Page("the first\u001fsecond one")));
    }

    [Fact]
    public void Normalizing_the_rendered_tree_keeps_the_page_text()
    {
        // FidelityEngine 先正規化再配對:Normalizer 用 with 運算式,丟了整頁文字就等於永遠沒有 text-not-found
        var root = Page("Parity Demo") with { Box = new Box(8, 8, 800, 600) };

        Assert.Equal("Parity Demo", Normalizer.NormalizeRendered(root).PageText);
    }

    [Fact]
    public void Page_text_is_never_serialized()
    {
        // serve 的 /api/detail 把整棵實作樹序列化給本機 UI;整頁文字只給配對用,不外流
        var root = Page("secret page words", Rendered("body > p", "p", text: "hi"));

        var json = JsonSerializer.Serialize(root, ReportWire.Compact);

        Assert.DoesNotContain("secret page words", json);
        Assert.DoesNotContain("pageText", json, StringComparison.OrdinalIgnoreCase);
    }
}
