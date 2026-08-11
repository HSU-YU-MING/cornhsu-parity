using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// 隨機 id 根治(野生實查 F2):擷取時高熵 id 不當 selector 錨點、改走結構路徑,
/// 路徑才能跨載入穩定——snapshot 模式的 selector 身分配對在隨機 id 頁面上才成立。
/// 穩定 id 照常當錨點(零回歸)。
/// </summary>
public class RandomIdSelectorTests : IDisposable
{
    // id 由頁面腳本在載入時隨機生成——模擬 React useId / MDN label-xxxx 的行為
    private const string Html = """
        <!doctype html>
        <html><head><meta charset="utf-8"><style>body { margin: 0; }</style></head>
        <body>
          <div id="app">
            <p>stable text</p>
            <span>randomly labelled</span>
          </div>
          <script>
            // 'x9' + 'k2z' 保證 token 至少 2 位數字、3 個字母(命中偵測規則);中段每次載入都不同
            document.querySelector('span').id =
              'label-x9' + Math.random().toString(36).slice(2, 10) + 'k2z';
          </script>
        </body></html>
        """;

    private readonly string _htmlPath;

    public RandomIdSelectorTests()
    {
        _htmlPath = Path.Combine(Path.GetTempPath(), $"parity-randid-{Guid.NewGuid():N}.html");
        File.WriteAllText(_htmlPath, Html);
    }

    public void Dispose()
    {
        if (File.Exists(_htmlPath)) File.Delete(_htmlPath);
    }

    [Fact]
    public async Task 隨機id不進selector_兩次擷取路徑一致_穩定id照常當錨點()
    {
        await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));
        var implRef = new ImplRef(Url: new Uri(_htmlPath).AbsoluteUri, ViewportWidth: 800, ViewportHeight: 600);

        RenderedNode first, second;
        try
        {
            first = await source.CaptureAsync(implRef);
            second = await source.CaptureAsync(implRef); // 重新載入 → id 重新隨機
        }
        catch (Exception ex) when (BrowserMissing(ex))
        {
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        var span1 = first.DescendantsAndSelf().Single(n => n.Tag == "span");
        var span2 = second.DescendantsAndSelf().Single(n => n.Tag == "span");

        Assert.StartsWith("label-", span1.DomId!);            // id 真的是隨機生成的(前提成立)
        Assert.DoesNotContain(span1.DomId!, span1.Selector);  // 但不進 selector
        Assert.Equal(span1.Selector, span2.Selector);         // 結構路徑跨載入穩定 → snapshot 配對成立

        var p = first.DescendantsAndSelf().Single(n => n.Tag == "p");
        Assert.StartsWith("#app", p.Selector); // 穩定 id 照常當錨點(零回歸)
    }

    private static bool BrowserMissing(Exception ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
}
