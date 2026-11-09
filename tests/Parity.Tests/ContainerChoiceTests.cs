using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 清單 #3:容器推論(Matcher 第 4 關)從最近共同祖先往外,挑大小最接近設計框的一層(ClosestContainer)。
/// 評估語料裡配錯的容器 235 個有 229 個是停在太裡面那層。
/// </summary>
public class ContainerChoiceTests
{
    private static readonly Box Page = new(0, 0, 1280, 900);

    /// <summary>設計稿:一張卡片(名字對不上 class),裡面標題與價格兩段字。</summary>
    private static DesignNode Card(Box box, params DesignNode[] siblings) => Design("1", "page", box: Page, children:
    [
        Design("2", "Frame 12", box: box, children:
        [
            Design("3", "title", DesignNodeType.Text, box: new Box(20, 20, 100, 20), characters: "Widget"),
            Design("4", "price", DesignNodeType.Text, box: new Box(20, 60, 60, 20), characters: "$9.99"),
        ]),
        .. siblings,
    ]);

    private static RenderedNode[] Texts(string parent) =>
    [
        Rendered($"{parent} > h3", "h3", text: "Widget", box: new Box(20, 20, 100, 20)),
        Rendered($"{parent} > span", "span", text: "$9.99", box: new Box(20, 60, 60, 20)),
    ];

    private static string ContainerOf(MatchResult result) => result.Pairs.Single(p => p.Design.Id == "2").Rendered.Selector;

    [Fact]
    public void The_container_moves_out_to_the_layer_that_has_its_size()
    {
        // 卡片 300×200 有內距,裡面再包一層 260×160:最近共同祖先是裡面那層,設計框畫的是外面那層
        var rendered = Rendered("body", box: Page, children:
            Rendered("#card", box: new Box(0, 0, 300, 200), children:
                Rendered("#inner", box: new Box(20, 20, 260, 160), children: Texts("#inner"))));

        var result = Matcher.Match(Card(new Box(0, 0, 300, 200)), rendered);

        Assert.Equal("#card", ContainerOf(result));
        Assert.Equal("auto-container", result.Pairs.Single(p => p.Design.Id == "2").MatchedBy);
    }

    [Fact]
    public void Nested_wrappers_of_the_same_size_keep_the_inner_one()
    {
        // 一樣大的巢狀 wrapper 很常見,評估語料裡這時答案多半是裡面那層(也就是 2.4 以前的結果)
        var rendered = Rendered("body", box: Page, children:
            Rendered("#outer", box: new Box(0, 0, 300, 200), children:
                Rendered("#inner", box: new Box(0, 0, 300, 200), children: Texts("#inner"))));

        Assert.Equal("#inner", ContainerOf(Matcher.Match(Card(new Box(0, 0, 300, 200)), rendered)));
    }

    [Fact]
    public void It_does_not_climb_into_a_layer_that_holds_other_matched_content()
    {
        // 設計框很大,外面的 section 大小比較近,但 section 裡還有另一段已配對的字:那是更大的容器,不是這張卡片
        var other = Design("5", "note", DesignNodeType.Text, box: new Box(900, 0, 100, 20), characters: "Other");
        var rendered = Rendered("body", box: Page, children:
            Rendered("#section", box: new Box(0, 0, 1200, 600), children:
            [
                Rendered("#card", box: new Box(0, 0, 300, 200), children:
                    Rendered("#inner", box: new Box(20, 20, 260, 160), children: Texts("#inner"))),
                Rendered("#section > p", "p", text: "Other", box: new Box(900, 0, 100, 20)),
            ]));

        Assert.Equal("#card", ContainerOf(Matcher.Match(Card(new Box(0, 0, 1000, 500), other), rendered)));
    }

    [Fact]
    public void It_does_not_climb_into_an_element_that_already_stands_for_another_design_node()
    {
        // 外層已經被圖層名「panel」配走了:一個元素只代表一個設計節點,停在它裡面
        var panel = Design("5", "panel", box: new Box(0, 0, 300, 200));
        var rendered = Rendered("body", box: Page, children:
            Rendered("#panel", classes: "panel", box: new Box(0, 0, 300, 200), children:
                Rendered("#inner", box: new Box(20, 20, 260, 160), children: Texts("#inner"))));

        var result = Matcher.Match(Card(new Box(0, 0, 300, 200), panel), rendered);

        Assert.Equal("#panel", result.Pairs.Single(p => p.Design.Id == "5").Rendered.Selector);
        Assert.Equal("#inner", ContainerOf(result));
    }

    [Fact]
    public void No_layer_of_a_plausible_size_leaves_the_container_unmatched()
    {
        // 設計框 1200×800,包住兩段字的那幾層都只有 120×90 上下(面積差 16 倍以上):這個框畫的不是它們
        var rendered = Rendered("body", box: Page, children:
            Rendered("#wrap", box: new Box(0, 0, 130, 100), children:
                Rendered("#inner", box: new Box(10, 10, 120, 90), children: Texts("#inner"))));

        var result = Matcher.Match(Card(new Box(0, 0, 1200, 800)), rendered);

        Assert.DoesNotContain(result.Pairs, p => p.Design.Id == "2");
        Assert.Equal("Frame 12", Assert.Single(result.Unmatched).DesignLayer);
    }

    [Fact]
    public void A_design_box_without_a_size_falls_back_to_the_lowest_common_ancestor()
    {
        // 手寫 JSON 可以不寫 box:沒有大小可比,照 2.4 以前配最近共同祖先
        var rendered = Rendered("body", box: Page, children:
            Rendered("#card", box: new Box(0, 0, 300, 200), children:
                Rendered("#inner", box: new Box(20, 20, 260, 160), children: Texts("#inner"))));

        Assert.Equal("#inner", ContainerOf(Matcher.Match(Card(default), rendered)));
    }
}
