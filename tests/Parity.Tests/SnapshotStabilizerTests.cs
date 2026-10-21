using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>snapshot --stabilize 的核心:多次擷取間「會動」的區域偵測(野生實查 F4)。</summary>
public class SnapshotStabilizerTests
{
    [Fact]
    public void Identical_captures_are_stable()
    {
        var a = Rendered("body", box: new Box(0, 0, 800, 600),
            children: Rendered("body > div:nth-of-type(1)", box: new Box(0, 0, 100, 50)));
        var b = Rendered("body", box: new Box(0, 0, 800, 600),
            children: Rendered("body > div:nth-of-type(1)", box: new Box(0, 0, 100, 50)));

        Assert.Empty(SnapshotStabilizer.FindUnstable([a, b]));
    }

    [Fact]
    public void Text_change_is_flagged_with_reason()
    {
        var a = Rendered("body", children: Rendered("body > p:nth-of-type(1)", "p", text: "3 minutes ago"));
        var b = Rendered("body", children: Rendered("body > p:nth-of-type(1)", "p", text: "4 minutes ago"));

        var u = Assert.Single(SnapshotStabilizer.FindUnstable([a, b]));
        Assert.Equal("body > p:nth-of-type(1)", u.Selector);
        Assert.Equal("text", u.Reason);
    }

    [Fact]
    public void Node_missing_in_one_capture_is_presence_unstable_both_directions()
    {
        // 廣告版位:第一拍有 A、第二拍換成 B——兩個 selector 都要算不穩定
        var a = Rendered("body", children: Rendered("body > div#ad-a", box: new Box(0, 0, 300, 250)));
        var b = Rendered("body", children: Rendered("body > div#ad-b", box: new Box(0, 0, 300, 250)));

        var unstable = SnapshotStabilizer.FindUnstable([a, b]);

        Assert.Equal(2, unstable.Count);
        Assert.All(unstable, u => Assert.Equal("presence", u.Reason));
    }

    [Fact]
    public void Subpixel_jitter_is_not_flagged()
    {
        var a = Rendered("body", children: Rendered("body > div:nth-of-type(1)", box: new Box(10, 10, 100, 50)));
        var b = Rendered("body", children: Rendered("body > div:nth-of-type(1)", box: new Box(10.4, 10, 100.6, 50)));

        Assert.Empty(SnapshotStabilizer.FindUnstable([a, b]));
    }

    [Fact]
    public void Unstable_descendants_collapse_into_the_highest_unstable_ancestor()
    {
        // 輪播容器動了,裡面四十個內臟全都動——建議 ignore 一個容器,不是四十條 selector
        var a = Rendered("body", children:
            Rendered("body > div#hero", box: new Box(0, 0, 800, 400), children:
                Rendered("body > div#hero > img:nth-of-type(1)", "img", box: new Box(0, 0, 800, 300))));
        var b = Rendered("body", children:
            Rendered("body > div#hero", box: new Box(0, 40, 800, 400), children:
                Rendered("body > div#hero > img:nth-of-type(1)", "img", box: new Box(0, 40, 800, 300))));

        var u = Assert.Single(SnapshotStabilizer.FindUnstable([a, b]));
        Assert.Equal("body > div#hero", u.Selector);
    }

    [Theory]
    [InlineData("color")]
    [InlineData("background")]
    [InlineData("typography")]
    [InlineData("padding")]
    public void Style_only_changes_are_flagged_with_their_own_reason(string changed)
    {
        // 輪播的換色、hover 動畫殘留、字型換置:框沒動但量出來的值會變,一樣是「會動」。
        // 理由印在 --stabilize 的輸出上,讓人知道該 ignore 還是該等字型載入
        RenderedNode Node(bool second) => Rendered("body > div:nth-of-type(1)",
            color: changed == "color" && second ? new Rgba(255, 0, 0) : new Rgba(0, 0, 0),
            background: changed == "background" && second ? new Rgba(0, 0, 255) : new Rgba(255, 255, 255),
            typography: new Typography(FontSize: changed == "typography" && second ? 18 : 16),
            padding: changed == "padding" && second ? new Insets(8, 8, 8, 8) : new Insets(4, 4, 4, 4));

        var u = Assert.Single(SnapshotStabilizer.FindUnstable(
            [Rendered("body", children: Node(second: false)), Rendered("body", children: Node(second: true))]));

        Assert.Equal("body > div:nth-of-type(1)", u.Selector);
        Assert.Equal(changed == "background" ? "color" : changed, u.Reason);
    }

    [Fact]
    public void A_sibling_whose_selector_merely_shares_a_prefix_is_not_collapsed()
    {
        // 收攏只跨「真的子孫」(selector 接著 " > " 或 " >>> "):#hero2 字串上以 #hero 開頭,
        // 但它是兄弟——吃掉它,建議的 ignore 就漏了一塊會動的區域
        var a = Rendered("body", children:
        [
            Rendered("body > div#hero", box: new Box(0, 0, 800, 100)),
            Rendered("body > div#hero2", box: new Box(0, 100, 800, 100)),
            Rendered("body > mdn-ad:nth-of-type(1)", box: new Box(0, 200, 300, 250), children:
                Rendered("body > mdn-ad:nth-of-type(1) >>> img:nth-of-type(1)", "img", box: new Box(0, 200, 300, 250))),
        ]);
        var b = Rendered("body", children:
        [
            Rendered("body > div#hero", box: new Box(0, 10, 800, 100)),
            Rendered("body > div#hero2", box: new Box(0, 110, 800, 100)),
            Rendered("body > mdn-ad:nth-of-type(1)", box: new Box(0, 210, 300, 250), children:
                Rendered("body > mdn-ad:nth-of-type(1) >>> img:nth-of-type(1)", "img", box: new Box(0, 210, 300, 250))),
        ]);

        var selectors = SnapshotStabilizer.FindUnstable([a, b]).Select(u => u.Selector).Order(StringComparer.Ordinal);

        // shadow 內部的 img 收進宿主;兩個 hero 各自保留
        Assert.Equal(["body > div#hero", "body > div#hero2", "body > mdn-ad:nth-of-type(1)"], selectors);
    }

    [Theory]
    [InlineData("body > div:nth-of-type(2)", "body > div:nth-of-type(2)")]
    [InlineData("body > mdn-placement-top:nth-of-type(1) >>> section:nth-of-type(1)",
                "body > mdn-placement-top:nth-of-type(1)")] // shadow 內部打不進去 → 退回宿主
    public void Ignore_selector_stops_at_the_shadow_boundary(string selector, string expected)
        => Assert.Equal(expected, SnapshotStabilizer.ToIgnoreSelector(selector));

    [Fact]
    public void Single_capture_has_nothing_to_compare()
        => Assert.Empty(SnapshotStabilizer.FindUnstable([Rendered("body")]));
}
