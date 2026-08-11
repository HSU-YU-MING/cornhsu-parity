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

    [Fact]
    public void Name_match_with_absurd_size_difference_is_rejected()
    {
        // 路線 B 實查 B3:設計稿的「Content」(整頁容器)撞上無關文件站的 #content——
        // 面積比 300×,任何落差都是垃圾。幾何不合理 → 拒配,理由講明白
        var design = Design("1", "frame", children:
            Design("2", "Content", box: new Box(0, 0, 1969, 3948)));
        var rendered = Rendered("body", box: new Box(0, 0, 1280, 900),
            children: Rendered("#content", domId: "content", box: new Box(0, 0, 603, 41)));

        var result = Matcher.Match(design, rendered);

        Assert.Empty(result.Pairs);
        Assert.Equal("size-implausible", Assert.Single(result.Unmatched).Reason);
    }

    [Fact]
    public void Name_match_with_plausible_size_still_works()
    {
        // 防線不能誤殺正當配對(demo 實測:正當 auto-name 面積比全部 ≈1×)
        var design = Design("1", "frame", children:
            Design("2", "CTA Button", box: new Box(0, 0, 160, 48)));
        var rendered = Rendered("body", box: new Box(0, 0, 1280, 900),
            children: Rendered(".cta-button", classes: "cta-button", box: new Box(40, 40, 172, 52)));

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("auto-name", pair.MatchedBy);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Implausible_first_candidate_yields_to_a_plausible_second()
    {
        // 同名兩個候選:第一個幾何荒謬、第二個合理 → 配第二個,不是放棄
        var design = Design("1", "frame", children:
            Design("2", "card", box: new Box(0, 0, 300, 200)));
        var rendered = Rendered("body", box: new Box(0, 0, 1280, 900),
            children:
            [
                Rendered("div.card:nth-of-type(1)", classes: "card", box: new Box(0, 0, 8, 8)),
                Rendered("div.card:nth-of-type(2)", classes: "card", box: new Box(0, 20, 310, 210)),
            ]);

        var result = Matcher.Match(design, rendered);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("div.card:nth-of-type(2)", pair.Rendered.Selector);
    }

    [Fact]
    public void Randomized_id_in_selector_gets_a_specific_unmatched_reason()
    {
        // 野生實查 F2(MDN):snapshot 凍住的隨機 id 在下次載入不存在 → 配不到是必然,
        // 理由要講清楚是 randomized-id,不是籠統的 no-anchor
        var design = Design("1", "frame", children:
        [
            Design("body > div#label-92g58lqqado", "div"),           // 隨機 id 樣態
            Design("body > section:nth-of-type(9)", "div"),          // 穩定 selector,單純配不到
        ]);
        var rendered = Rendered("body",
            children: Rendered("body > p:nth-of-type(1)", "p"));

        var result = Matcher.Match(design, rendered);

        Assert.Equal(2, result.Unmatched.Count);
        Assert.Equal("randomized-id", result.Unmatched[0].Reason);
        Assert.Equal("no-anchor", result.Unmatched[1].Reason);
    }

    [Theory]
    [InlineData("body > div#label-92g58lqqado", true)]              // MDN 實測樣態
    [InlineData("#3jrOqJD10fXAt6AjqpbEG-shapes-title", true)]       // Excalidraw 實測樣態
    [InlineData("div.css-x9k2mq4w > span", true)]                   // CSS-in-JS class
    [InlineData("#label-bpi6prqmqwr", true)]                        // 單數字內嵌的長雜湊(MDN 實測)
    [InlineData("#wtz1gbigyt-input", true)]                         // 同上
    [InlineData("#label-rkxvdnnzty", false)]                        // 純字母隨機字串:分不出來,誠實漏放
    [InlineData("10:2", false)]                                     // Figma id 不誤中
    [InlineData("body > main#content > aside:nth-of-type(1)", false)]
    [InlineData("#hero > div.cta-button", false)]
    [InlineData("html5-video-player", false)]                       // 短數字 token 不誤中
    public void Randomized_token_detection(string selector, bool expected)
        => Assert.Equal(expected, Matcher.ContainsRandomizedToken(selector));
}
