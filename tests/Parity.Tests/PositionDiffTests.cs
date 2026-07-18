using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 相對位置比對(規畫書 4.8「比相對位置」):
/// 非 auto-layout 父層的子節點,比「相對最近兄弟/父層」的偏移。
/// 兩個底線:抓得到「擺錯位置」、不能因 flow 連鎖而誤報一排。
/// </summary>
public class PositionDiffTests
{
    private sealed class StubDesign(DesignNode root) : IDesignSource
    {
        public Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default)
            => Task.FromResult(root);
    }

    private sealed class StubImpl(RenderedNode root) : IImplementationSource
    {
        public Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default)
            => Task.FromResult(root);
    }

    private static async Task<FidelityReport> Run(DesignNode design, RenderedNode rendered, bool comparePosition = true)
    {
        var engine = new FidelityEngine(new StubDesign(design), new StubImpl(rendered),
            new EngineOptions(new Tolerances()) { ComparePosition = comparePosition });
        return await engine.RunAsync(new ScanRequest(new DesignRef("stub", ""), new ImplRef("http://stub")));
    }

    private static IEnumerable<PropDiff> OffsetDiffs(FidelityReport report)
        => report.Nodes.SelectMany(n => n.Diffs).Where(d => d.Prop is "offsetX" or "offsetY");

    // ---------- 抓得到「擺錯位置」 ----------

    [Fact]
    public async Task Misplaced_child_is_caught()
    {
        // badge 設計在右上角 (600,40),實作跑到左上角 (0,40)——尺寸顏色全對,位置全錯
        var design = Design("1:1", "home", box: new Box(0, 0, 800, 600), children:
            Design("1:2", "badge", box: new Box(600, 40, 120, 32)));
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
            Rendered(".pill", box: new Box(0, 40, 120, 32), explicitMatch: "badge"));

        var report = await Run(design, rendered);

        var dx = Assert.Single(OffsetDiffs(report));
        Assert.Equal("offsetX", dx.Prop);
        Assert.Equal(600, dx.Delta);
        Assert.Equal(Severity.Critical, dx.Severity); // 600px vs 容差 4 → critical
    }

    [Fact]
    public async Task Small_drift_within_tolerance_is_ignored()
    {
        // flow 版面的次像素/字渲染漂移(2px < 容差 3)不该報
        var design = Design("1:1", "home", box: new Box(0, 0, 800, 600), children:
            Design("1:2", "title", box: new Box(40, 40, 200, 30)));
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
            Rendered("h1", box: new Box(41, 42, 200, 30), explicitMatch: "title"));

        Assert.Empty(OffsetDiffs(await Run(design, rendered)));
    }

    // ---------- 不誤報:兄弟參照的自癒 ----------

    [Fact]
    public async Task Sibling_reference_prevents_cascade_false_positives()
    {
        // A 垂直偏了 10px;B 跟 A 的間距沒變(一起被推下去)。
        // 用兄弟當參照 → 只有 A 被報,B 不被連坐。
        var design = Design("1:1", "home", box: new Box(0, 0, 800, 600), children:
        [
            Design("1:2", "block-a", box: new Box(40, 40, 100, 20)),
            Design("1:3", "block-b", box: new Box(40, 76, 100, 20)), // 與 A 底部相距 16
        ]);
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered(".a", box: new Box(40, 50, 100, 20), explicitMatch: "block-a"), // 偏 10
            Rendered(".b", box: new Box(40, 86, 100, 20), explicitMatch: "block-b"), // 間距仍 16
        ]);

        var report = await Run(design, rendered);

        var offsets = OffsetDiffs(report).ToList();
        var dy = Assert.Single(offsets);
        Assert.Equal("offsetY", dy.Prop);
        Assert.Equal(10, dy.Delta);
        // 被報的是 A(相對父層偏 10),不是 B
        Assert.Contains(report.Nodes, n => n.DesignLayer == "block-a" && n.Diffs.Contains(dy));
        Assert.DoesNotContain(report.Nodes, n => n.DesignLayer == "block-b" && n.Diffs.Count > 0);
    }

    [Fact]
    public async Task Text_sibling_is_not_a_position_reference()
    {
        // 標題是 TEXT:DOM 的 block 寬(720)≠ Figma 文字框寬(300)。
        // 拿它右緣當 badge 的水平參照,「正確的頁面」會被誤報 → TEXT 不可靠,退回父層參照。
        var design = Design("1:1", "home", box: new Box(0, 0, 800, 600), children:
        [
            Design("1:2", "title", DesignNodeType.Text, box: new Box(40, 40, 300, 40), characters: "Hello"),
            Design("1:3", "badge", box: new Box(640, 40, 120, 32)),
        ]);
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered("h1", text: "Hello", box: new Box(40, 40, 720, 38)),   // block 滿寬
            Rendered(".pill", box: new Box(640, 40, 120, 32), explicitMatch: "badge"),
        ]);

        Assert.Empty(OffsetDiffs(await Run(design, rendered)));
    }

    [Fact]
    public async Task Vertical_offset_skipped_when_only_text_siblings_above()
    {
        // 按鈕上方是一疊文字(高度不可靠):它的 Y 是流經那些文字高度累積出來的。
        // 行高差讓按鈕實際落點偏 6px——這不是設計錯,不該報;誠實跳過 Y。
        var design = Design("1:1", "home", box: new Box(0, 0, 800, 600), children:
        [
            Design("1:2", "title", DesignNodeType.Text, box: new Box(40, 40, 300, 40), characters: "Hi"),
            Design("1:3", "cta", box: new Box(40, 152, 160, 48)),
        ]);
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered("h1", text: "Hi", box: new Box(40, 40, 720, 34)),          // 行高不同
            Rendered(".cta", box: new Box(40, 146, 160, 48), explicitMatch: "cta"), // 被推上去 6px
        ]);

        Assert.Empty(OffsetDiffs(await Run(design, rendered)));
    }

    // ---------- 該跳過的都跳過 ----------

    [Fact]
    public async Task Auto_layout_children_skip_position()
    {
        // auto-layout 容器的子節點位置由 padding/gap 決定——那邊已有比對,不再比偏移
        var design = Design("1:1", "list", box: new Box(0, 0, 800, 600), layoutMode: "VERTICAL", children:
        [
            Design("1:2", "item-1", box: new Box(0, 0, 100, 20)),
            Design("1:3", "item-2", box: new Box(0, 40, 100, 20)),
        ]);
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered(".i1", box: new Box(0, 10, 100, 20), explicitMatch: "item-1"),
            Rendered(".i2", box: new Box(0, 90, 100, 20), explicitMatch: "item-2"),
        ]);

        Assert.Empty(OffsetDiffs(await Run(design, rendered)));
    }

    [Fact]
    public async Task Position_none_disables_comparison()
    {
        var design = Design("1:1", "home", box: new Box(0, 0, 800, 600), children:
            Design("1:2", "badge", box: new Box(600, 40, 120, 32)));
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
            Rendered(".pill", box: new Box(0, 40, 120, 32), explicitMatch: "badge"));

        Assert.Empty(OffsetDiffs(await Run(design, rendered, comparePosition: false)));
    }
}
