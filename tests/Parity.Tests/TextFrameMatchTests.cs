using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 清單 #2:設計稿把按鈕畫成「框包一段字」,網頁上只有一個元素(Matcher 第 3.5 關)。
/// 框要配到大小對得上框的那一層:字的元素本身(auto-shared,兩個設計節點共用),或外面只包著它的 wrapper(auto-container)。
/// </summary>
public class TextFrameMatchTests
{
    private sealed class StubDesign(DesignNode root) : IDesignSource
    {
        public Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default) => Task.FromResult(root);
    }

    private sealed class StubImpl(RenderedNode root) : IImplementationSource
    {
        public Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default) => Task.FromResult(root);
    }

    private static readonly Box Page = new(0, 0, 1280, 900);

    /// <summary>設計稿:120×40 的框(名字對不上任何 class,跟模擬 Figma 一樣),裡面一段字。</summary>
    private static DesignNode ButtonDesign(Box? frame = null, Rgba? fill = null, Rgba? textColor = null)
        => Design("1", "page", box: Page, children:
            Design("2", "Frame 3", box: frame ?? new Box(0, 0, 120, 40), fill: fill, children:
                Design("3", "Buy now", DesignNodeType.Text, box: new Box(16, 10, 88, 20), fill: textColor, characters: "Buy now")));

    [Fact]
    public void The_frame_shares_the_button_its_label_matched()
    {
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > button", "button", text: "Buy now", box: new Box(0, 0, 120, 40)));

        var result = Matcher.Match(ButtonDesign(), rendered);

        Assert.Empty(result.Unmatched);
        var byId = result.Pairs.ToDictionary(p => p.Design.Id, p => (p.Rendered.Selector, p.MatchedBy));
        Assert.Equal(("body > button", "auto-text"), byId["3"]);
        Assert.Equal(("body > button", "auto-shared"), byId["2"]);
    }

    [Fact]
    public void A_tight_text_element_sends_the_frame_to_the_wrapper_that_has_its_size()
    {
        // <li class="pill"><a>Buy now</a></li>:a 只有字那麼大,框是外面的 li
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > li", "li", box: new Box(0, 0, 120, 40), children:
                Rendered("body > li > a", "a", text: "Buy now", box: new Box(16, 10, 88, 20))));

        var byId = Matcher.Match(ButtonDesign(), rendered).Pairs.ToDictionary(p => p.Design.Id, p => (p.Rendered.Selector, p.MatchedBy));

        Assert.Equal(("body > li > a", "auto-text"), byId["3"]);
        Assert.Equal(("body > li", "auto-container"), byId["2"]);
    }

    [Fact]
    public void Real_rendering_differences_within_the_slack_still_match()
    {
        // Figma 量的寬高跟瀏覽器差幾 px 是常態:120×40 的框,按鈕 123×42
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > button", "button", text: "Buy now", box: new Box(0, 0, 123, 42)));

        var frame = Assert.Single(Matcher.Match(ButtonDesign(), rendered).Pairs, p => p.Design.Id == "2");

        Assert.Equal("auto-shared", frame.MatchedBy);
    }

    [Fact]
    public void Two_layers_of_the_same_size_are_a_tie_and_the_frame_stays_unmatched()
    {
        // <li><a> 一樣大:分不出框畫的是哪一層,不猜
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > li", "li", box: new Box(0, 0, 120, 40), children:
                Rendered("body > li > a", "a", text: "Buy now", box: new Box(0, 0, 120, 40))));

        var result = Matcher.Match(ButtonDesign(), rendered);

        Assert.Equal("3", Assert.Single(result.Pairs).Design.Id);
        Assert.Equal("Frame 3", Assert.Single(result.Unmatched).DesignLayer);
    }

    [Fact]
    public void No_layer_near_the_frame_size_leaves_it_unmatched()
    {
        // 框 400×200,字的元素跟外面那層都差很遠:這個框畫的不是網頁上任何一層
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > div", "div", box: new Box(0, 0, 1280, 300), children:
                Rendered("body > div > a", "a", text: "Buy now", box: new Box(16, 10, 88, 20))));

        var result = Matcher.Match(ButtonDesign(frame: new Box(0, 0, 400, 200)), rendered);

        Assert.Equal("Frame 3", Assert.Single(result.Unmatched).DesignLayer);
    }

    [Fact]
    public void A_wrapper_that_holds_other_matched_content_is_not_the_frame()
    {
        // div 跟框一樣大,但裡面還有另一段已配對的字:那個 div 是別的東西的容器,不是這顆按鈕
        var design = Design("1", "page", box: Page, children:
        [
            Design("2", "Frame 3", box: new Box(0, 0, 300, 40), children:
                Design("3", "title", DesignNodeType.Text, box: new Box(0, 0, 100, 20), characters: "Title")),
            Design("4", "other", DesignNodeType.Text, box: new Box(200, 0, 100, 20), characters: "Other"),
        ]);
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > div", "div", box: new Box(0, 0, 300, 40), children:
            [
                Rendered("body > div > h3", "h3", text: "Title", box: new Box(0, 0, 100, 20)),
                Rendered("body > div > span", "span", text: "Other", box: new Box(200, 0, 100, 20)),
            ]));

        var result = Matcher.Match(design, rendered);

        Assert.Equal("Frame 3", Assert.Single(result.Unmatched).DesignLayer);
    }

    [Fact]
    public void A_single_inner_layer_that_fits_as_well_means_the_text_may_sit_too_far_out()
    {
        // 評估語料實測(pythonorg):字配到外層的 <li>,真正畫框和字的是裡面一樣大的 <a>(有內距)。
        // 框跟著字配到 <li> 會報四條假的內距落差,所以裡面還有一層對得上時不配
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > li", "li", text: "Buy now", box: new Box(0, 0, 122, 40), children:
                Rendered("body > li > a", "a", box: new Box(1, 0, 120, 40))));

        var result = Matcher.Match(ButtonDesign(), rendered);

        Assert.Equal("body > li", Assert.Single(result.Pairs).Rendered.Selector); // 字照舊(那是第 2 關的事)
        Assert.Equal("Frame 3", Assert.Single(result.Unmatched).DesignLayer);
    }

    [Fact]
    public void A_frame_with_more_than_the_text_is_left_to_container_inference()
    {
        // 框裡有字也有圖示:不是「只包一段字」,這一關不碰(要 ≥2 個已配對子孫才走第 4 關)
        var design = Design("1", "page", box: Page, children:
            Design("2", "Frame 3", box: new Box(0, 0, 120, 40), children:
            [
                Design("3", "Buy now", DesignNodeType.Text, box: new Box(16, 10, 88, 20), characters: "Buy now"),
                Design("4", "Vector", DesignNodeType.Shape, box: new Box(100, 12, 16, 16)),
            ]));
        var rendered = Rendered("body", box: Page, children:
            Rendered("body > button", "button", text: "Buy now", box: new Box(0, 0, 120, 40)));

        var result = Matcher.Match(design, rendered);

        Assert.DoesNotContain(result.Pairs, p => p.Design.Id == "2");
    }

    [Fact]
    public async Task The_shared_button_reports_its_background_and_its_label_color_once_each()
    {
        // 走完整路徑:框比背景色,字比字色;字不比尺寸與位置,所以同一個元素不會被報兩次寬高
        var blue = new Rgba(0, 0, 255);
        var white = new Rgba(255, 255, 255);
        var design = ButtonDesign(fill: blue, textColor: white);
        var body = Rendered("body", "body", box: Page, children:
            Rendered("body > button", "button", text: "Buy now", box: new Box(0, 0, 122, 40),
                color: new Rgba(0, 0, 0), background: new Rgba(255, 0, 0)));
        var engine = new FidelityEngine(new StubDesign(design), new StubImpl(body), EngineOptions.Default);

        var report = await engine.RunAsync(new ScanRequest(new DesignRef("d.json", "page"), new ImplRef("http://x/")));

        var frame = Assert.Single(report.Nodes, n => n.DesignId == "2");
        var label = Assert.Single(report.Nodes, n => n.DesignId == "3");
        Assert.Equal("auto-shared", frame.MatchedBy);
        Assert.Contains(frame.Diffs, d => d.Prop == "background");
        Assert.DoesNotContain(frame.Diffs, d => d.Prop == "color");
        Assert.Contains(label.Diffs, d => d.Prop == "color");
        Assert.DoesNotContain(label.Diffs, d => d.Prop is "background" or "width" or "height" or "offsetX" or "offsetY");
    }
}
