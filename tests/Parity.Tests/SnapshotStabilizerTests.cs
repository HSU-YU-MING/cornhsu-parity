using Parity.Engine.DesignSources.Snapshot;
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
    [InlineData("body > div:nth-of-type(2)", "body > div:nth-of-type(2)")]
    [InlineData("body > mdn-placement-top:nth-of-type(1) >>> section:nth-of-type(1)",
                "body > mdn-placement-top:nth-of-type(1)")] // shadow 內部打不進去 → 退回宿主
    public void Ignore_selector_stops_at_the_shadow_boundary(string selector, string expected)
        => Assert.Equal(expected, SnapshotStabilizer.ToIgnoreSelector(selector));

    [Fact]
    public void Single_capture_has_nothing_to_compare()
        => Assert.Empty(SnapshotStabilizer.FindUnstable([Rendered("body")]));
}
