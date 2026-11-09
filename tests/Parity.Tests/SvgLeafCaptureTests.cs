using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// SVG 當葉子(野生實查 F3):內部繪圖指令(path/g/defs)沒有 padding/字體語意,
/// 對設計比對是雜訊;動畫/重繪下 nth-of-type 又不穩,配對必 flaky
/// (tailwindcss.com 實測 44 個 unmatched、兩次配對數不同)。
/// svg 本身照量(圖示的尺寸/位置還是要比),內部不展開。
/// </summary>
public class SvgLeafCaptureTests : IDisposable
{
    private const string Html = """
        <!doctype html>
        <html><head><meta charset="utf-8"><style>body { margin: 0; }</style></head>
        <body>
          <div id="toolbar" class="toolbar main-bar">
            <svg id="icon" class="icon icon-save" width="24" height="24" viewBox="0 0 24 24">
              <defs><linearGradient id="grad"><stop offset="0"/></linearGradient></defs>
              <g><path d="M4 4h16v16H4z"/><path d="M6 6h12v12H6z"/></g>
            </svg>
            <span id="label">Save</span>
            <svg class="icon icon-close" width="16" height="16"><path d="M0 0h16v16H0z"/></svg>
          </div>
        </body></html>
        """;

    private readonly string _htmlPath;

    public SvgLeafCaptureTests()
    {
        _htmlPath = Path.Combine(Path.GetTempPath(), $"parity-svgleaf-{Guid.NewGuid():N}.html");
        File.WriteAllText(_htmlPath, Html);
    }

    public void Dispose()
    {
        if (File.Exists(_htmlPath)) File.Delete(_htmlPath);
    }

    [Fact]
    public async Task Svg本身有量到_內部的path_g_defs不展開()
    {
        await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));

        RenderedNode root;
        try
        {
            root = await source.CaptureAsync(new ImplRef(
                Url: new Uri(_htmlPath).AbsoluteUri, ViewportWidth: 800, ViewportHeight: 600));
        }
        catch (Exception ex) when (BrowserMissing(ex))
        {
            // 沒瀏覽器就不算數。刻意印出來:靜靜地綠燈會讓人以為這條驗過了。
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        var svg = root.DescendantsAndSelf().SingleOrDefault(n => n.DomId == "icon");
        Assert.NotNull(svg); // 圖示本身是版面成員,尺寸/位置照比
        Assert.Equal(24, svg!.Box.W, precision: 0);
        Assert.Empty(svg.Children ?? []); // 內部繪圖指令不進樹

        Assert.DoesNotContain(root.DescendantsAndSelf(), n => n.Tag is "path" or "g" or "defs");
        Assert.NotNull(root.DescendantsAndSelf().SingleOrDefault(n => n.DomId == "label")); // 旁邊的元素不受影響
    }

    [Fact]
    public async Task Svg的class讀得到_圖層名配得上圖示()
    {
        // 清單 #5:SVG 元素的 className 是 SVGAnimatedString 不是字串,以前一律收成 null,圖示靠名字永遠配不上
        // (論文評估條件 A:尺寸案例裡圖示只有 15.2% 配對正確)。改讀 class 屬性
        await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));

        RenderedNode root;
        try
        {
            root = await source.CaptureAsync(new ImplRef(
                Url: new Uri(_htmlPath).AbsoluteUri, ViewportWidth: 800, ViewportHeight: 600));
        }
        catch (Exception ex) when (BrowserMissing(ex))
        {
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        var svgs = root.DescendantsAndSelf().Where(n => n.Tag == "svg").ToList();
        Assert.Equal(["icon icon-save", "icon icon-close"], svgs.Select(s => s.Classes));
        Assert.Equal("toolbar main-bar", root.DescendantsAndSelf().Single(n => n.DomId == "toolbar").Classes); // HTML 元素照舊

        // 設計稿的圖層名「icon-save」對得上 class:兩顆圖示都有 icon,但只有一顆有 icon-save,唯一才配
        var design = TestData.Design("1", "page", box: new Parity.Engine.Model.Box(0, 0, 800, 600), children:
            TestData.Design("2", "icon-save", box: new Parity.Engine.Model.Box(0, 0, 24, 24)));
        var pair = Assert.Single(Parity.Engine.Comparison.Matcher.Match(design, root).Pairs);
        Assert.Equal(("svg", "auto-name"), (pair.Rendered.Tag, pair.MatchedBy));
        Assert.Equal("icon icon-save", pair.Rendered.Classes);
    }

    private static bool BrowserMissing(Exception ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
}
