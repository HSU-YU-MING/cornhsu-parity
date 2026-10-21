using Parity.Engine;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 引擎唯一進入點 FidelityEngine 自己負責的事(配對/比對細節見 MatcherTests、DiffEngineTests):
/// 用什麼視窗去渲染、報告信封裡的欄位怎麼填、summary 怎麼數。
/// </summary>
public class FidelityEngineTests
{
    private sealed class StubDesign(DesignNode root) : IDesignSource
    {
        public Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default) => Task.FromResult(root);
    }

    /// <summary>記下引擎實際要求的視窗大小。</summary>
    private sealed class RecordingImpl(RenderedNode root) : IImplementationSource
    {
        public ImplRef? Requested { get; private set; }

        public Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default)
        {
            Requested = reference;
            return Task.FromResult(root);
        }
    }

    private static readonly RenderedNode Body = Rendered("body", box: new Box(0, 0, 400, 300));

    [Fact]
    public async Task Viewport_defaults_to_the_design_frame_size_rounded_up()
    {
        // 規畫書:在設計 frame 的寬度下渲染(手機 frame 375 寬 → media query 自然生效)
        var impl = new RecordingImpl(Body);
        var engine = new FidelityEngine(new StubDesign(Design("f", "frame", box: new Box(10, 20, 375.2, 811.5))), impl, EngineOptions.Default);

        await engine.RunAsync(new ScanRequest(new DesignRef("d.json", "f"), new ImplRef("http://x/")));

        Assert.Equal(376, impl.Requested!.ViewportWidth);
        Assert.Equal(812, impl.Requested.ViewportHeight);
    }

    [Fact]
    public async Task Explicit_viewport_wins_over_the_frame_size()
    {
        var impl = new RecordingImpl(Body);
        var engine = new FidelityEngine(new StubDesign(Design("f", "frame", box: new Box(0, 0, 1440, 900))), impl, EngineOptions.Default);

        await engine.RunAsync(new ScanRequest(new DesignRef("d.json", "f"), new ImplRef("http://x/", 390, 844)));

        Assert.Equal(390, impl.Requested!.ViewportWidth);
        Assert.Equal(844, impl.Requested.ViewportHeight);
    }

    [Fact]
    public async Task Zero_size_frame_without_a_viewport_is_an_error_not_a_guess()
    {
        var engine = new FidelityEngine(new StubDesign(Design("f", "Empty", box: default)), new RecordingImpl(Body), EngineOptions.Default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RunAsync(new ScanRequest(new DesignRef("d.json", "f"), new ImplRef("http://x/"))));

        Assert.Contains("design frame \"Empty\" has size 0×0", ex.Message);
        Assert.Contains("pointing at the wrong node", ex.Message);
    }

    [Fact]
    public async Task Report_envelope_fields_and_summary_counts()
    {
        var design = Design("f", "frame", box: new Box(0, 0, 400, 300), children:
        [
            Design("t", "Title", DesignNodeType.Text, box: new Box(0, 0, 200, 40), characters: "Hello",
                text: new Typography("Inter", 32)),
            Design("c", "card", box: new Box(0, 100, 100, 50)),
            Design("g", "ghost", box: new Box(0, 200, 10, 10)),
        ]);
        var rendered = Rendered("body", box: new Box(8, 8, 400, 300), children:
        [
            Rendered("h1", "h1", text: "Hello", box: new Box(8, 8, 200, 40),
                typography: new Typography("Arial", 20)),                       // fontSize 落差 + 軟的 fontFamily 落差
            Rendered("div.card", classes: "card", box: new Box(8, 108, 100, 50)), // 完全吻合
        ]);
        var engine = new FidelityEngine(new StubDesign(design), new RecordingImpl(rendered), EngineOptions.Default);

        var report = await engine.RunAsync(new ScanRequest(new DesignRef("design.json", "f"), new ImplRef("./page.html"), "/home"));

        Assert.Equal("/home", report.Route);
        Assert.Equal("./page.html", report.Url);               // 原樣,不被視窗大小的補值改掉
        Assert.Equal("design.json#f", report.DesignReference);   // <來源>#<frame>——web 儀表板會解析它
        Assert.Equal(["Title", "card"], report.Nodes.Select(n => n.DesignLayer));
        Assert.Equal("ghost", Assert.Single(report.Unmatched).DesignLayer);

        var s = report.Summary;
        Assert.Equal((3, 2, 1, 1), (s.DesignNodes, s.Matched, s.Unmatched, s.NodesWithDiffs));
        Assert.Equal(1, s.Critical); // fontSize 32 vs 20:12 / 0.5 = 24 倍
        Assert.Equal(1, s.Minor);    // 軟落差也算進計數(盤點 5.2:summary 包括 soft)
        Assert.Equal((0, 0), (s.Serious, s.Medium));
        Assert.Equal(Severity.Critical, s.MaxSeverity);
    }

    [Fact]
    public async Task Nothing_matched_means_no_severity_at_all()
    {
        var design = Design("f", "frame", box: new Box(0, 0, 400, 300), children: Design("x", "ghost", box: new Box(0, 0, 10, 10)));
        var engine = new FidelityEngine(new StubDesign(design), new RecordingImpl(Body), EngineOptions.Default);

        var report = await engine.RunAsync(new ScanRequest(new DesignRef("d", "f"), new ImplRef("u")));

        Assert.Empty(report.Nodes);
        Assert.Equal(Severity.None, report.Summary.MaxSeverity);
        Assert.Equal((1, 0, 1), (report.Summary.DesignNodes, report.Summary.Matched, report.Summary.Unmatched));
    }

    [Fact]
    public void Null_sources_are_rejected_up_front()
    {
        Assert.Throws<ArgumentNullException>(() => new FidelityEngine(null!, new RecordingImpl(Body), EngineOptions.Default));
        Assert.Throws<ArgumentNullException>(() => new FidelityEngine(new StubDesign(Design("f", "f")), null!, EngineOptions.Default));
    }
}
