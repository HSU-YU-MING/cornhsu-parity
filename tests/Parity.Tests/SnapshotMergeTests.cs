using Parity.Cli;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Json;
using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// <c>parity snapshot --target</c> 只重拍一頁、併回既有快照。2.5.0 以前整份快照換成那一頁
/// (作者網站 26 頁的基準變 1 頁)。真瀏覽器的端到端驗證在 CliBrowserTests。
/// </summary>
public class SnapshotMergeTests
{
    private static DesignNode Frame(string route, double width = 1280)
        => new(route, route, DesignNodeType.Frame, new Box(0, 0, width, 800), null, null, null, null, null, []);

    private static SnapshotDocument Current(DesignNode root, IReadOnlyList<string>? anchors = null)
        => SnapshotDocument.Of(root, anchors, styleDefaultsOmitted: true);

    [Fact]
    public void Replacing_a_page_keeps_the_others_in_place()
    {
        var existing = Current(SnapshotMerge.MultiRoot([Frame("/"), Frame("/a"), Frame("/b")]));

        var r = SnapshotMerge.Merge(existing, Frame("/a", 640), [], false);

        Assert.Equal(SnapshotMerge.MultiRootId, r.Root.Id);
        Assert.Equal(["/", "/a", "/b"], r.Root.Children.Select(c => c.Id));
        Assert.Equal(640, r.Root.Children[1].Box.W);
        Assert.Same(existing.Root.Children[0], r.Root.Children[0]);
        Assert.Same(existing.Root.Children[2], r.Root.Children[2]);
        Assert.Equal(1, r.FrameIndex); // 參考截圖 x.1.png:check 照 frame 的位置找
        Assert.False(r.SingleBecameMulti);
    }

    [Fact]
    public void A_route_not_in_the_snapshot_yet_is_appended()
    {
        var r = SnapshotMerge.Merge(Current(SnapshotMerge.MultiRoot([Frame("/"), Frame("/a")])), Frame("/new"), [], false);

        Assert.Equal(["/", "/a", "/new"], r.Root.Children.Select(c => c.Id));
        Assert.Equal(2, r.FrameIndex);
    }

    [Fact]
    public void A_single_page_snapshot_of_another_route_becomes_two_pages()
    {
        var r = SnapshotMerge.Merge(Current(Frame("/")), Frame("/a"), [], false);

        Assert.Equal(SnapshotMerge.MultiRootId, r.Root.Id);
        Assert.Equal(["/", "/a"], r.Root.Children.Select(c => c.Id));
        Assert.Equal(1, r.FrameIndex);
        Assert.True(r.SingleBecameMulti); // 原本的 x.png 要改名成 x.0.png
    }

    [Fact]
    public void The_same_single_page_is_simply_replaced_whatever_wrote_it()
    {
        // 整份換掉、不涉及別頁:舊版格式也沒有混寫的問題
        var old = SnapshotDocument.Of(Frame("/"), null, styleDefaultsOmitted: false);

        var r = SnapshotMerge.Merge(old, Frame("/", 375), ["app"], true);

        Assert.Equal("/", r.Root.Id);
        Assert.Equal(375, r.Root.Box.W);
        Assert.Equal(-1, r.FrameIndex);
        Assert.Equal(["app"], r.StableIdAnchors!);
    }

    [Theory]
    [InlineData(false, null)] // 2.3 以前:沒寫的樣式 = 沒量過
    [InlineData(true, null)]  // 2.3、2.4:沒寫外框 = 沒量過
    [InlineData(true, 1)]
    public void An_older_snapshot_format_is_refused_instead_of_mixed(bool omitted, int? version)
    {
        var old = new SnapshotDocument(1, SnapshotMerge.MultiRoot([Frame("/"), Frame("/a")]))
        {
            StyleDefaultsOmitted = omitted ? true : null,
            StyleDefaultsVersion = version,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => SnapshotMerge.Merge(old, Frame("/a"), [], false));
        Assert.Contains("without --target", ex.Message);
        Assert.Throws<InvalidOperationException>(() => SnapshotMerge.EnsureMergeable(old, "/a")); // 開瀏覽器之前就擋
        SnapshotMerge.EnsureMergeable(old with { Root = Frame("/a") }, "/a"); // 同一頁的單頁快照:整份換掉,不擋
    }

    [Fact]
    public void Random_ids_on_the_page_need_an_allowlist_covering_every_page()
    {
        var existing = Current(SnapshotMerge.MultiRoot([Frame("/"), Frame("/a")]));

        var ex = Assert.Throws<InvalidOperationException>(() => SnapshotMerge.Merge(existing, Frame("/a"), ["nav"], true));
        Assert.Contains("/a now has ids that change on every page load", ex.Message);
    }

    [Fact]
    public void An_existing_allowlist_gains_the_pages_stable_ids()
    {
        var existing = Current(SnapshotMerge.MultiRoot([Frame("/"), Frame("/a")]), ["app", "nav"]);

        var r = SnapshotMerge.Merge(existing, Frame("/a"), ["main", "nav"], true);

        Assert.Equal(["app", "main", "nav"], r.StableIdAnchors!);
        // 這頁沒有隨機 id 也一樣:名單整份共用,check 擷取這頁時名單外的 id 不當錨點
        Assert.Equal(["app", "main", "nav"], SnapshotMerge.Merge(existing, Frame("/a"), ["main"], false).StableIdAnchors!);
    }

    [Fact]
    public void Reading_keeps_omitted_style_defaults_omitted()
    {
        // 其他頁要原封不動寫回去:讀的時候還原預設值,寫回去每個節點都多出透明度、邊框、陰影,檔案無端變大
        using var dir = new TempDir();
        var path = dir.Write("s.json", ReportJson.SerializeSnapshotTree(
            SnapshotMerge.MultiRoot([Frame("/")]), ["app"], styleDefaultsOmitted: true));

        var doc = JsonDesignSource.ReadSnapshotDocument(path)!;

        Assert.Null(doc.Root.Children[0].Opacity);
        Assert.Null(doc.Root.Children[0].Shadows);
        Assert.True(doc.StyleDefaultsOmitted);
        Assert.Equal(SnapshotDocument.CurrentStyleDefaultsVersion, doc.StyleDefaultsVersion);
        Assert.Equal(["app"], doc.StableIdAnchors!);
        Assert.Equal(File.ReadAllText(path), ReportJson.SerializeSnapshotTree(doc.Root, doc.StableIdAnchors, styleDefaultsOmitted: true));
    }

    [Fact]
    public void Reading_a_bare_tree_returns_null()
    {
        using var dir = new TempDir();
        var path = dir.Write("s.json", """{ "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 10, "height": 10 } }""");

        Assert.Null(JsonDesignSource.ReadSnapshotDocument(path));
    }
}
