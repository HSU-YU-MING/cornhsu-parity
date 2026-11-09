using System.Text.Json.Nodes;
using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;
using Parity.Engine.DesignSources.Json;
using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 清單 #6:外框(CSS outline)。不佔版面,以前完全沒比(論文評估快照模式加外框 0/22)。
/// 只提醒(軟落差)、不擋;設計端不知道時不比:Figma 有 stroke 的方框、2.3/2.4 拍的舊快照。
/// </summary>
public class OutlineTests
{
    private static readonly Rgba Red = new(255, 0, 0);
    private static readonly Rgba Blue = new(0, 0, 255);

    private static IReadOnlyList<PropDiff> Diff(DesignNode d, Parity.Engine.ImplementationSources.RenderedNode r)
        => new DiffEngine(new Tolerances()).Diff(new NodePair(d, r, "selector")).Diffs;

    [Fact]
    public void An_outline_the_design_does_not_have_is_a_soft_hint()
    {
        var design = Design("2", "card", box: new Box(0, 0, 100, 40)) with { OutlineWidth = 0 };
        var rendered = Rendered("#card", box: new Box(0, 0, 100, 40)) with { OutlineWidth = 2, OutlineColor = Red };

        var diff = Assert.Single(Diff(design, rendered));

        Assert.Equal(("outlineWidth", "0", "2", true), (diff.Prop, diff.Expected, diff.Actual, diff.Soft));
    }

    [Fact]
    public void Same_width_compares_the_color_and_a_matching_outline_is_quiet()
    {
        var design = Design("2", "card", box: new Box(0, 0, 100, 40)) with { OutlineWidth = 2, OutlineColor = Blue };

        var recolored = Diff(design, Rendered("#card", box: new Box(0, 0, 100, 40)) with { OutlineWidth = 2, OutlineColor = Red });
        var same = Diff(design, Rendered("#card", box: new Box(0, 0, 100, 40)) with { OutlineWidth = 2, OutlineColor = Blue });

        Assert.Equal("outlineColor", Assert.Single(recolored).Prop);
        Assert.Empty(same);
    }

    [Fact]
    public void Unknown_on_either_side_is_not_compared()
    {
        // 設計端不知道(Figma 有 stroke、舊快照)或擷取端沒給(假來源、舊測試)都不比
        var unknownDesign = Design("2", "card", box: new Box(0, 0, 100, 40));
        var outlined = Rendered("#card", box: new Box(0, 0, 100, 40)) with { OutlineWidth = 2, OutlineColor = Red };
        var noOutlineDesign = Design("2", "card", box: new Box(0, 0, 100, 40)) with { OutlineWidth = 0 };

        Assert.Empty(Diff(unknownDesign, outlined));
        Assert.Empty(Diff(noOutlineDesign, Rendered("#card", box: new Box(0, 0, 100, 40))));
    }

    [Fact]
    public void A_new_snapshot_restores_a_missing_outline_as_none()
    {
        // StyleDefaultsVersion 2:沒寫外框 = 沒有外框
        var expanded = SnapshotBuilder.ExpandStyleDefaults(Design("1", "f", children: Design("2", "c")), version: 2);

        Assert.All(expanded.DescendantsAndSelf(), n => Assert.Equal(0, n.OutlineWidth));
    }

    [Fact]
    public void An_older_snapshot_leaves_the_outline_unknown()
    {
        // 2.3、2.4 拍的快照(沒有 styleDefaultsVersion)根本沒量外框:當成「沒有」的話,頁面上本來就有的外框會被報成新增
        var expanded = SnapshotBuilder.ExpandStyleDefaults(Design("1", "f", children: Design("2", "c")));

        Assert.All(expanded.DescendantsAndSelf(), n => Assert.Null(n.OutlineWidth));
        Assert.All(expanded.DescendantsAndSelf(), n => Assert.Equal(new Insets(0, 0, 0, 0), n.BorderWidth)); // 舊欄位照常還原
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "styleDefaultsOmitted": true, "styleDefaultsVersion": 2, "root": { "id": "1", "name": "f", "type": "Frame" } }""", 0.0)]
    [InlineData("""{ "schemaVersion": 1, "styleDefaultsOmitted": true, "root": { "id": "1", "name": "f", "type": "Frame" } }""", null)]
    public async Task Reading_a_snapshot_file_follows_its_style_defaults_version(string json, double? outline)
    {
        using var dir = new TempDir();
        var path = dir.Write("parity.snapshot.json", json);

        var root = await new JsonDesignSource().GetFrameAsync(new DesignRef(path, ""));

        Assert.Equal(outline, root.OutlineWidth);
    }

    [Fact]
    public void A_snapshot_written_today_says_which_defaults_it_omits()
    {
        var doc = SnapshotDocument.Of(Design("1", "f"), styleDefaultsOmitted: true);

        Assert.Equal(SnapshotDocument.CurrentStyleDefaultsVersion, doc.StyleDefaultsVersion);
        Assert.Equal(2, SnapshotDocument.CurrentStyleDefaultsVersion);
        Assert.Null(SnapshotDocument.Of(Design("1", "f")).StyleDefaultsVersion); // 全部都寫的(沒省略)不需要
    }

    [Theory]
    // 沒有 stroke:設計上沒有外框
    [InlineData("""{ "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 1, "strokes": [] }""", 0.0)]
    // 外側 stroke:可能寫成 outline、border 或 box-shadow,不猜
    [InlineData("""{ "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 2, "strokeAlign": "OUTSIDE", "strokes": [{ "type": "SOLID", "color": { "r": 1, "g": 0, "b": 0, "a": 1 } }] }""", null)]
    [InlineData("""{ "id": "1", "name": "f", "type": "RECTANGLE", "strokeWeight": 1, "strokeAlign": "INSIDE", "strokes": [{ "type": "SOLID", "color": { "r": 0, "g": 0, "b": 0, "a": 1 } }] }""", null)]
    // 文字:沒有外框的概念
    [InlineData("""{ "id": "1", "name": "t", "type": "TEXT", "strokes": [] }""", null)]
    public void Figma_says_no_outline_only_for_a_box_without_any_stroke(string json, double? outline)
        => Assert.Equal(outline, FigmaNodeParser.Parse(JsonNode.Parse(json)!).OutlineWidth);
}
