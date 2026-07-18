using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

public class MatcherTests
{
    [Fact]
    public void Matches_by_selector_identity_first()
    {
        // snapshot 設計來源:設計節點 Id = 擷取時的 selector → 第 0 關直接配,不靠文字/名字
        var design = Design("1", "frame",
            children: Design("body > div:nth-of-type(1)", "div"));
        var rendered = Rendered("body",
            children: Rendered("body > div:nth-of-type(1)", "div"));

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("selector", pair.MatchedBy);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Matches_text_nodes_by_content()
    {
        var design = Design("1", "frame",
            children: Design("2", "page-title", DesignNodeType.Text, characters: "Hello Parity"));
        var rendered = Rendered("body",
            children: Rendered("body > h1", "h1", text: "Hello  Parity")); // 多空白也要配上

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("auto-text", pair.MatchedBy);
        Assert.Equal("body > h1", pair.Rendered.Selector);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Ambiguous_text_is_not_force_matched()
    {
        // 同樣文字出現兩次 → 不硬湊(規畫書 4.7:不假裝全對上)
        var design = Design("1", "frame",
            children: Design("2", "label-a", DesignNodeType.Text, characters: "Edit"));
        var rendered = Rendered("body",
            children:
            [
                Rendered("body > button:nth-of-type(1)", "button", text: "Edit"),
                Rendered("body > button:nth-of-type(2)", "button", text: "Edit"),
            ]);

        var result = Matcher.Match(design, rendered);

        Assert.Empty(result.Pairs);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal("label-a", unmatched.DesignLayer);
    }

    [Fact]
    public void Matches_by_explicit_anchor_first()
    {
        // data-parity="cta-button" 對圖層名,不是天書節點 ID(規畫書 4.7 DX 重點)
        var design = Design("1", "frame",
            children: Design("2", "cta-button", characters: null));
        var rendered = Rendered("body",
            children: Rendered("main > button.cta", "button", explicitMatch: "cta-button"));

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("explicit", pair.MatchedBy);
    }

    [Fact]
    public void Matches_by_layer_name_to_dom_class()
    {
        // 圖層 "CTA Button" ↔ class "cta-button":正規化後等值
        var design = Design("1", "frame",
            children: Design("2", "CTA Button"));
        var rendered = Rendered("body",
            children: Rendered("main > button", "button", classes: "btn cta-button primary"));

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("auto-name", pair.MatchedBy);
    }

    [Fact]
    public void Unmatched_nodes_are_reported_honestly()
    {
        var design = Design("1", "frame",
            children: Design("2", "hero-badge"));
        var rendered = Rendered("body",
            children: Rendered("body > div", "div"));

        var result = Matcher.Match(design, rendered);

        Assert.Empty(result.Pairs);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal("hero-badge", unmatched.DesignLayer);
        Assert.Equal("no-anchor", unmatched.Reason);
    }

    [Fact]
    public void Explicit_match_beats_text_match()
    {
        var design = Design("1", "frame",
            children: Design("2", "cta-button", DesignNodeType.Text, characters: "Go"));
        var rendered = Rendered("body",
            children:
            [
                Rendered("body > a", "a", text: "Go"),
                Rendered("body > button", "button", text: "Go", explicitMatch: "cta-button"),
            ]);

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("explicit", pair.MatchedBy);
        Assert.Equal("body > button", pair.Rendered.Selector);
    }

    [Fact]
    public void Container_without_anchor_is_inferred_from_matched_children()
    {
        // 卡片容器沒文字、圖層名也對不上 class,但兩個子節點都配上了
        // → 用子節點的最近共同祖先(包住它們的 div)反推容器,不用手動 map
        var design = Design("1", "frame",
            children: Design("2", "product-card", DesignNodeType.Frame, box: new Box(0, 0, 200, 120),
                children:
                [
                    Design("3", "card-title", DesignNodeType.Text, characters: "Widget"),
                    Design("4", "card-price", DesignNodeType.Text, characters: "$9.99"),
                ]));
        var rendered = Rendered("body",
            children: Rendered("body > div", "div", classes: "sc-1a2b3c",
                children:
                [
                    Rendered("body > div > h3", "h3", text: "Widget"),
                    Rendered("body > div > span", "span", text: "$9.99"),
                ]));

        var result = Matcher.Match(design, rendered);

        var card = result.Pairs.Single(p => p.Design.Name == "product-card");
        Assert.Equal("auto-container", card.MatchedBy);
        Assert.Equal("body > div", card.Rendered.Selector);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Empty_container_without_anchor_stays_unmatched()
    {
        // 空容器(無子節點可推)→ 仍誠實留白,不硬湊
        var design = Design("1", "frame",
            children: Design("2", "spacer", DesignNodeType.Frame, box: new Box(0, 0, 100, 40)));
        var rendered = Rendered("body",
            children: Rendered("body > div", "div", classes: "unrelated"));

        var result = Matcher.Match(design, rendered);

        Assert.Empty(result.Pairs);
        Assert.Single(result.Unmatched);
    }

    [Fact]
    public void Ambiguous_text_is_disambiguated_by_layer_name()
    {
        // 同樣文字 "GOV.UK" 出現兩次;圖層名 "footer-copyright" 只對得上其中一個 class → 選它
        var design = Design("1", "frame",
            children: Design("2", "footer-copyright", DesignNodeType.Text, characters: "GOV.UK"));
        var rendered = Rendered("body",
            children:
            [
                Rendered("body > a", "a", text: "GOV.UK", classes: "header-logo"),
                Rendered("body > span", "span", text: "GOV.UK", classes: "footer-copyright"),
            ]);

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("body > span", pair.Rendered.Selector);
        Assert.Equal("auto-text", pair.MatchedBy);
    }

    [Theory]
    [InlineData("CTA Button", "cta-button")]
    [InlineData("ctaButton", "cta-button")]
    [InlineData("cta_button", "CTA-BUTTON")]
    public void Name_normalization_bridges_conventions(string layerName, string domName)
        => Assert.Equal(Matcher.NormalizeName(layerName), Matcher.NormalizeName(domName));
}
