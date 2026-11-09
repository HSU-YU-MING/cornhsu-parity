using Parity.Cli;
using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 清單 #4:讀得懂 oklab()、lab()、lch();真的讀不懂(color(rec2020 …) 這類)時,不比、明講。
/// 以前讀不懂的背景會落到「(transparent)」報成假落差,陰影、漸層會被當成不存在。
/// </summary>
public class UnreadColorTests
{
    private static readonly Rgba Blue = new(0, 0, 255);
    private static readonly UnreadColor[] Rec2020Background = [new("background", "color(rec2020 0.5 0.2 0.1)")];

    private static NodePair Pair(DesignNode d, RenderedNode r) => new(d, r, "auto-name");

    [Fact]
    public void Gradients_and_shadows_read_the_lab_family()
    {
        // Chromium 的計算值保留原本的色彩空間:Tailwind v4 的陰影與漸層常見 oklab(… / 0.1)
        var gradient = CssEffects.ParseGradient("linear-gradient(oklab(0.6 0.1 -0.1) 0%, lab(50 40 -20) 100%)");
        var shadows = CssEffects.ParseShadows("oklab(0 0 0 / 0.1) 0px 4px 6px -1px");

        Assert.NotNull(gradient!.Start);
        Assert.NotNull(gradient.End);
        Assert.Equal(0.1, Assert.Single(shadows!).Color.A, 3);
    }

    [Fact]
    public void Unreadable_colors_are_found_and_readable_ones_are_not()
    {
        Assert.Equal(["color(rec2020 0.5 0.2 0.1)"],
            CssEffects.UnreadableColors("color(rec2020 0.5 0.2 0.1) 0px 4px 6px, oklab(0 0 0 / 0.1) 0px 1px 2px"));
        Assert.Empty(CssEffects.UnreadableColors("oklab(0.6 0.1 -0.1)"));
        Assert.Empty(CssEffects.UnreadableColors("rgba(0, 0, 0, 0)"));
        Assert.Empty(CssEffects.UnreadableColors(null));
    }

    [Fact]
    public void An_unreadable_background_is_not_reported_as_transparent()
    {
        // 以前:背景讀不懂 → null → 祖先也沒有 → 「(transparent)」,一條 medium 的假落差
        var design = Design("2", "card", fill: Blue, box: new Box(0, 0, 100, 40));
        var rendered = Rendered("#card", box: new Box(0, 0, 100, 40)) with { UnreadColors = Rec2020Background };

        var result = new DiffEngine(new Tolerances()).Diff(Pair(design, rendered));

        Assert.DoesNotContain(result.Diffs, d => d.Prop == "background");
    }

    [Fact]
    public void An_unreadable_inherited_background_is_not_reported_as_transparent_either()
    {
        // 自己透明、往上找到的祖先背景讀不懂:一樣不知道看起來是什麼顏色,不比
        var design = Design("2", "card", fill: Blue, box: new Box(0, 0, 100, 40));
        UnreadColor[] inherited = [new("effectiveBackground", "color(a98-rgb 0.4 0.5 0.6)")];
        var rendered = Rendered("#card", background: new Rgba(0, 0, 0, 0), box: new Box(0, 0, 100, 40)) with { UnreadColors = inherited };

        Assert.DoesNotContain(new DiffEngine(new Tolerances()).Diff(Pair(design, rendered)).Diffs, d => d.Prop == "background");
    }

    [Fact]
    public void A_background_that_really_is_transparent_is_still_reported()
    {
        // 讀得懂、真的透明、祖先也沒有背景:照舊報(這條防止把「不比」寫成「全部放過」)
        var design = Design("2", "card", fill: Blue, box: new Box(0, 0, 100, 40));
        var rendered = Rendered("#card", background: new Rgba(0, 0, 0, 0), box: new Box(0, 0, 100, 40));

        var diff = Assert.Single(new DiffEngine(new Tolerances()).Diff(Pair(design, rendered)).Diffs, d => d.Prop == "background");
        Assert.Equal("(transparent)", diff.Actual);
    }

    [Fact]
    public void A_shadow_with_an_unreadable_color_is_not_reported_missing()
    {
        // 陰影那一層的顏色讀不懂 → 解析時那層被丟掉 → 以前報「陰影不見」
        var design = Design("2", "card", box: new Box(0, 0, 100, 40)) with
        {
            Shadows = [new ShadowSpec(0, 4, 6, 0, new Rgba(0, 0, 0, 0.1))],
        };
        var rendered = Rendered("#card", box: new Box(0, 0, 100, 40)) with
        {
            Shadows = [],
            UnreadColors = [new("boxShadow", "color(rec2020 0 0 0 / 0.1)")],
        };

        Assert.DoesNotContain(new DiffEngine(new Tolerances()).Diff(Pair(design, rendered)).Diffs, d => d.Prop.StartsWith("shadow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_console_note_counts_unread_colors_on_matched_elements_only()
    {
        // 走完整路徑拿 ScanResult:配上的元素有一個讀不懂的顏色、沒配上的元素也有一個,只算前者
        var design = Design("1", "page", box: new Box(0, 0, 800, 600), children:
            Design("2", "Buy", DesignNodeType.Text, fill: Blue, box: new Box(0, 0, 60, 20), characters: "Buy"));
        var body = Rendered("body", "body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered("#buy", "button", text: "Buy", box: new Box(0, 0, 60, 20)) with { UnreadColors = Rec2020Background },
            Rendered("#other", box: new Box(0, 100, 60, 20)) with { UnreadColors = Rec2020Background },
        ]);
        var engine = new FidelityEngine(new StubDesign(design), new StubImpl(body), EngineOptions.Default);

        var result = await engine.RunDetailedAsync(new ScanRequest(new DesignRef("d.json", "page"), new ImplRef("http://x/"), "/home"));
        var note = ScanSession.UnreadColorsNote(result, "/home");

        Assert.Equal("note: 1 color value(s) on matched elements of target /home could not be read, so those colors were not compared " +
            "(e.g. color(rec2020 0.5 0.2 0.1) on ‹#buy›).", note);
    }

    [Fact]
    public async Task No_note_when_every_color_was_read()
    {
        var design = Design("1", "page", box: new Box(0, 0, 800, 600), children:
            Design("2", "Buy", DesignNodeType.Text, box: new Box(0, 0, 60, 20), characters: "Buy"));
        var body = Rendered("body", "body", box: new Box(0, 0, 800, 600), children:
            Rendered("#buy", "button", text: "Buy", box: new Box(0, 0, 60, 20)));
        var engine = new FidelityEngine(new StubDesign(design), new StubImpl(body), EngineOptions.Default);

        var result = await engine.RunDetailedAsync(new ScanRequest(new DesignRef("d.json", "page"), new ImplRef("http://x/"), "/home"));

        Assert.Null(ScanSession.UnreadColorsNote(result, "/home"));
    }

    private sealed class StubDesign(DesignNode root) : IDesignSource
    {
        public Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default) => Task.FromResult(root);
    }

    private sealed class StubImpl(RenderedNode root) : IImplementationSource
    {
        public Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default) => Task.FromResult(root);
    }
}
