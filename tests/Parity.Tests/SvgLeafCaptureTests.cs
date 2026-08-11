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
          <div id="toolbar">
            <svg id="icon" width="24" height="24" viewBox="0 0 24 24">
              <defs><linearGradient id="grad"><stop offset="0"/></linearGradient></defs>
              <g><path d="M4 4h16v16H4z"/><path d="M6 6h12v12H6z"/></g>
            </svg>
            <span id="label">Save</span>
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

        var svg = root.DescendantsAndSelf().SingleOrDefault(n => n.Tag == "svg");
        Assert.NotNull(svg); // 圖示本身是版面成員,尺寸/位置照比
        Assert.Equal(24, svg!.Box.W, precision: 0);
        Assert.Empty(svg.Children ?? []); // 內部繪圖指令不進樹

        Assert.DoesNotContain(root.DescendantsAndSelf(), n => n.Tag is "path" or "g" or "defs");
        Assert.NotNull(root.DescendantsAndSelf().SingleOrDefault(n => n.DomId == "label")); // 旁邊的元素不受影響
    }

    private static bool BrowserMissing(Exception ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
}
