using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// 清單 #4 的真瀏覽器驗收:Chromium 的計算值保留原本的色彩空間,Tailwind v4 的半透明色與 color-mix() 會是 oklab(… / 0.5)。
/// 以前這些一律讀不懂,背景被報成透明。現在讀得懂;讀不懂的(rec2020)記下來,不比、明講。
/// </summary>
public class ColorCaptureTests : IDisposable
{
    private const string Html = """
        <!doctype html>
        <html><head><meta charset="utf-8"><style>body { margin: 0; } div { width: 100px; height: 40px; }</style></head>
        <body>
          <div id="tailwind" style="--tw: oklch(62.3% 0.214 259.815); background: color-mix(in oklab, var(--tw) 50%, transparent)">a</div>
          <div id="lab" style="background: lab(50% 40 -20); color: lch(20 30 270)">b</div>
          <div id="rec2020" style="background: color(rec2020 0.5 0.2 0.1)">c</div>
        </body></html>
        """;

    private readonly string _htmlPath;

    public ColorCaptureTests()
    {
        _htmlPath = Path.Combine(Path.GetTempPath(), $"parity-colors-{Guid.NewGuid():N}.html");
        File.WriteAllText(_htmlPath, Html);
    }

    public void Dispose()
    {
        if (File.Exists(_htmlPath)) File.Delete(_htmlPath);
    }

    [Fact]
    public async Task Lab_family_colors_are_read_and_unreadable_ones_are_recorded()
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
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        RenderedNode ById(string id) => root.DescendantsAndSelf().Single(n => n.DomId == id);

        // Tailwind 的 bg-blue-500/50:Chromium 給 oklab(0.623 … / 0.5)
        var tailwind = ById("tailwind");
        Assert.NotNull(tailwind.Background);
        Assert.Equal(0.5, tailwind.Background!.Value.A, 2);
        Assert.Null(tailwind.UnreadColors);

        // lab() 背景、lch() 字色:期望值是 Chromium 自己換算的(color-mix(in srgb, X 100%, black 0%))
        var lab = ById("lab");
        Assert.InRange(lab.Background!.Value.R, 170, 172);
        Assert.InRange(lab.Background!.Value.G, 89, 91);
        Assert.InRange(lab.Color!.Value.B, 92, 94);

        // rec2020 讀不懂:背景是 null,但記下來了——比對會跳過,不會報成透明
        var rec2020 = ById("rec2020");
        Assert.Null(rec2020.Background);
        var unread = Assert.Single(rec2020.UnreadColors!, u => u.Field == "background");
        Assert.StartsWith("color(rec2020", unread.Value, StringComparison.Ordinal);
    }

    private static bool BrowserMissing(Exception ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
}
