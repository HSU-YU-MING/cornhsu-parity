using Parity.Engine;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// Web font 載入失敗偵測(2.5.0):字型載不到時瀏覽器改用系統字型,文字寬度、換行、位置全跟著變,
/// 整頁的落差都是這一個原因(作品集的守門就這樣整批誤報過)。擷取端列出沒載到的,報告與主控台明講。
/// </summary>
public class FontLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"parity-fonts-{Guid.NewGuid():N}");

    public FontLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Page(string name, string css, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"""
            <!doctype html><html><head><meta charset="utf-8"><style>body {"{"} margin: 0; {"}"} {css}</style></head>
            <body>{body}</body></html>
            """);
        return new Uri(path).AbsoluteUri;
    }

    private static async Task<RenderedNode?> Capture(string url)
    {
        await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));
        try
        {
            return await source.CaptureAsync(new ImplRef(Url: url, ViewportWidth: 800, ViewportHeight: 600));
        }
        catch (Exception ex) when (ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return null;
        }
    }

    [Fact]
    public async Task A_web_font_the_page_uses_but_cannot_load_is_listed()
    {
        // 字型檔不存在:瀏覽器改用系統字型,FontFace 的 status 是 error。宣告了沒用到的(Unused)不會下載,不列
        var url = Page("broken.html", """
            @font-face { font-family: "Brand Sans"; src: url("missing-brand.woff2") format("woff2"); font-weight: 400; }
            @font-face { font-family: "Unused"; src: url("missing-unused.woff2") format("woff2"); }
            h1 { font-family: "Brand Sans", sans-serif; }
            """, "<h1>Welcome</h1>");

        var root = await Capture(url);
        if (root is null) return;

        Assert.Equal(["Brand Sans 400 normal"], root.FontsNotLoaded);
    }

    [Fact]
    public async Task A_page_without_web_fonts_lists_nothing()
    {
        var root = await Capture(Page("plain.html", "h1 { font-family: sans-serif; }", "<h1>Welcome</h1>"));
        if (root is null) return;

        Assert.Null(root.FontsNotLoaded);
    }

    private static FidelityReport Report(string route, IReadOnlyList<string>? fonts) => new(
        route, "http://x" + route, "d.json#1", [], [], new ReportSummary(0, 0, 0, 0, 0, 0, 0, 0, Severity.None), null, fonts);

    [Fact]
    public void The_markdown_report_warns_at_the_top_and_keeps_font_names_inert()
    {
        // 字型名來自頁面的 CSS:寫成連結或提及的字串只能留在程式碼區段裡
        var md = MarkdownReport.Render([Report("/", ["Brand Sans 400 normal", "[x](https://evil.example) @someone 400 normal"])], gateFail: false);

        Assert.Contains("> ⚠️ web font(s) did not load: `Brand Sans 400 normal`, `[x](https://evil.example) @someone 400 normal`. " +
            "Text was measured with a fallback font", md);
        Assert.DoesNotContain("> ⚠️ web font(s) did not load: [x]", md);
    }

    [Fact]
    public void Several_pages_name_the_route_and_pages_whose_fonts_loaded_say_nothing()
    {
        var md = MarkdownReport.Render([Report("/home", ["Brand Sans 400 normal"]), Report("/about", null)], gateFail: false);

        Assert.Contains("> ⚠️ /home: web font(s) did not load: `Brand Sans 400 normal`.", md);
        Assert.DoesNotContain("/about: web font", md);
    }
}
