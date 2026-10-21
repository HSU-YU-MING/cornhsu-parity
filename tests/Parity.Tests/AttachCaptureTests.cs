using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// `cdp:http://host:port[#fragment]`——attach 到已在跑的 Chromium/Electron,讀「活視窗」的 DOM
/// (README 的第三種 target)。這條路面對的是使用者正在用的 app,所以承諾是**不留痕跡**:
/// 不導頁、不關頁、不改視窗大小、凍結動畫的樣式用完就拿掉。
/// 這裡用 Playwright 自己起一個開了 remote-debugging-port 的 Chromium 扮演那個 app。
/// </summary>
public class AttachCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"parity-attach-{Guid.NewGuid():N}");

    public AttachCaptureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* Chromium 的 profile 檔可能還被鎖著;殘檔在 %TEMP%,無害 */ }
        catch (UnauthorizedAccessException) { }
    }

    private string WritePage(string name, string heading)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"<!doctype html><html><head><meta charset=\"utf-8\"><style>body{{margin:0}}</style></head><body><h1>{heading}</h1></body></html>");
        return new Uri(path).AbsoluteUri;
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Attach_picks_the_window_by_url_fragment_and_leaves_no_trace()
    {
        var port = FreeLoopbackPort();
        using var pw = await Playwright.CreateAsync();
        IBrowserContext app;
        try
        {
            app = await pw.Chromium.LaunchPersistentContextAsync(Path.Combine(_dir, "profile"), new()
            {
                Headless = true,
                Args = [$"--remote-debugging-port={port}"],
                ViewportSize = new ViewportSize { Width = 700, Height = 500 },
            });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        await using var _ = app;
        var first = app.Pages.Count > 0 ? app.Pages[0] : await app.NewPageAsync();
        await first.GotoAsync(WritePage("main.html", "Main window"));
        var settings = await app.NewPageAsync();
        await settings.GotoAsync(WritePage("settings.html", "Settings window"));
        var settingsUrl = settings.Url;
        var stylesBefore = await settings.EvaluateAsync<int>("() => document.querySelectorAll('style').length");

        await using var source = new WebImplementationSource();
        var endpoint = $"cdp:http://127.0.0.1:{port}";

        var picked = await source.CaptureAsync(new ImplRef($"{endpoint}#settings.html"));
        var unspecified = await source.CaptureAsync(new ImplRef(endpoint));

        Assert.Contains(picked.DescendantsAndSelf(), n => n.Text == "Settings window");
        // 沒給 fragment → CDP 列出的第一個視窗;多視窗時那個順序不保證(實測會變),所以只驗「拿到其中一個」
        Assert.Contains(unspecified.DescendantsAndSelf(), n => n.Text is "Main window" or "Settings window");

        // 不留痕跡:沒導頁、沒關頁、視窗大小沒動、凍結樣式已移除
        Assert.Equal(settingsUrl, settings.Url);
        Assert.Equal(2, app.Pages.Count);
        Assert.Equal(700, await settings.EvaluateAsync<int>("() => window.innerWidth"));
        Assert.Equal(500, await settings.EvaluateAsync<int>("() => window.innerHeight"));
        Assert.Equal(stylesBefore, await settings.EvaluateAsync<int>("() => document.querySelectorAll('style').length"));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.CaptureAsync(new ImplRef($"{endpoint}#nope.html")));
        Assert.Contains("no page at the CDP endpoint has a URL containing \"nope.html\"", missing.Message);
        Assert.Contains("settings.html", missing.Message); // 列出現有的視窗,讓人知道該填什麼
    }
}
