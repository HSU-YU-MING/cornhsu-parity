using System.Text.Json;
using Microsoft.Playwright;
using Parity.Engine.Comparison;
using Parity.Engine.Model;

namespace Parity.Engine.ImplementationSources.Web;

public sealed record WebCaptureOptions(bool Headless = true, float? TimeoutMs = 30_000, bool CaptureScreenshot = false);

/// <summary>
/// 網頁實作來源(規畫書 4.5):Playwright 開 Chromium、視窗寬 = frame 寬、
/// 注入腳本走訪 DOM → RenderedNode 樹。跑在本機 → localhost / 內網天生連得到。
/// 第一次使用需 `parity install-browser`(下載 Chromium)。
/// </summary>
public sealed class WebImplementationSource(WebCaptureOptions? options = null) : IImplementationSource, IAsyncDisposable
{
    private readonly WebCaptureOptions _options = options ?? new WebCaptureOptions();
    private static readonly JsonSerializerOptions CaptureParseOptions = new() { MaxDepth = 512 };
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowser? _cdpBrowser; // attach 到既有 Chromium/Electron 的連線(與自啟的 _browser 分開)
    private readonly Dictionary<string, byte[]> _screenshots = [];

    /// <summary>CaptureScreenshot 開啟時,每次擷取後的整頁 PNG(key = URL)。本機報告 UI 的疊圖底圖。</summary>
    public IReadOnlyDictionary<string, byte[]> Screenshots => _screenshots;

    /// <summary>"cdp:http://host:port" = attach 到已在跑的 Chromium/Electron(Electron 桌面 app 走這條)。</summary>
    public static bool IsAttachUrl(string url) => url.StartsWith("cdp:", StringComparison.OrdinalIgnoreCase);

    public async Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default)
    {
        if (IsAttachUrl(reference.Url))
            return await CaptureAttachedAsync(reference);

        var browser = await GetBrowserAsync();
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = reference.ViewportWidth ?? 1280,
                Height = reference.ViewportHeight ?? 800,
            },
        });
        try
        {
            await page.GotoAsync(reference.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = _options.TimeoutMs,
            });
            return await CaptureFromPageAsync(page, reference);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// 連到已在跑的 Chromium/Electron(它用 --remote-debugging-port 開了 CDP 端點)抓「活視窗」的 DOM。
    /// 不啟自己的瀏覽器、不導頁、也不關對方的頁面——只讀取現況。Electron app 走這條。
    /// </summary>
    private async Task<RenderedNode> CaptureAttachedAsync(ImplRef reference)
    {
        // "cdp:http://host:port"           → 第一個頁面
        // "cdp:http://host:port#index.html" → URL 含「index.html」的頁面(多視窗 Electron 指定用)
        var rest = reference.Url["cdp:".Length..];
        var hash = rest.IndexOf('#');
        var endpoint = hash >= 0 ? rest[..hash] : rest;
        var pageMatch = hash >= 0 ? rest[(hash + 1)..] : null;

        var pw = await GetPlaywrightAsync();
        _cdpBrowser ??= await pw.Chromium.ConnectOverCDPAsync(endpoint);

        var pages = _cdpBrowser.Contexts.SelectMany(c => c.Pages).ToList();
        if (pages.Count == 0)
            throw new InvalidOperationException($"CDP 端點沒有開啟中的頁面(app 視窗還沒載入?):{endpoint}");

        var page = pageMatch is null
            ? pages[0]
            : pages.FirstOrDefault(p => p.Url.Contains(pageMatch, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException(
                  $"CDP 端點找不到 URL 含「{pageMatch}」的頁面;現有:{string.Join(" 、 ", pages.Select(p => p.Url))}");

        return await CaptureFromPageAsync(page, reference);
    }

    /// <summary>共用的擷取:在給定頁面上跑擷取腳本 → RenderedNode(啟動導頁與 CDP attach 兩條路都用)。</summary>
    private async Task<RenderedNode> CaptureFromPageAsync(IPage page, ImplRef reference)
    {
        var arg = new
        {
            mapSelectors = reference.MapSelectors ?? new Dictionary<string, string>(),
            ignoreSelectors = reference.IgnoreSelectors ?? [],
        };
        // 擷取腳本回傳 JSON 字串(見 CaptureScript:避開 Playwright 值序列化的深度放大)。
        // 用放寬的 MaxDepth 解析——真實網站 DOM 常有十幾層巢狀,預設 64 不夠。
        var raw = await page.EvaluateAsync<string>(CaptureScript.Js, arg);
        var json = JsonSerializer.Deserialize<JsonElement>(raw, CaptureParseOptions);
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidOperationException($"頁面擷取失敗(body 不可見?):{reference.Url}");

        if (_options.CaptureScreenshot)
            _screenshots[reference.Url] = await page.ScreenshotAsync(
                new PageScreenshotOptions { FullPage = true });

        return ParseNode(json);
    }

    /// <summary>擷取腳本輸出 → RenderedNode。CSS 字串在這裡解析成數值(px、顏色)。</summary>
    internal static RenderedNode ParseNode(JsonElement el)
    {
        var box = el.GetProperty("box");
        var children = new List<RenderedNode>();
        if (el.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var kid in kids.EnumerateArray())
                children.Add(ParseNode(kid));

        return new RenderedNode(
            Selector: Str(el, "selector") ?? "",
            Tag: Str(el, "tag") ?? "",
            Text: Str(el, "text"),
            Box: new Box(
                box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(),
                box.GetProperty("w").GetDouble(), box.GetProperty("h").GetDouble()),
            Color: Color(el, "color"),
            Background: Color(el, "background"),
            Typography: new Typography(
                FontFamily: Str(el, "fontFamily"),
                FontSize: Px(el, "fontSize"),
                FontWeight: Px(el, "fontWeight"),
                LineHeight: Px(el, "lineHeight"),
                LetterSpacing: Px(el, "letterSpacing")),
            Padding: new Insets(
                Px(el, "paddingTop") ?? 0, Px(el, "paddingRight") ?? 0,
                Px(el, "paddingBottom") ?? 0, Px(el, "paddingLeft") ?? 0),
            Children: children)
        {
            ExplicitMatch = Str(el, "explicitMatch"),
            EffectiveBackground = Color(el, "effectiveBackground"),
            DomId = Str(el, "domId"),
            Classes = Str(el, "classes"),
            AriaLabel = Str(el, "ariaLabel"),
            CornerRadius = Px(el, "cornerRadius"),
        };

        static string? Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double? Px(JsonElement e, string prop)
            => Normalizer.ParseCssPx(Str(e, prop));
        static Rgba? Color(JsonElement e, string prop)
            => Rgba.TryParseCss(Str(e, prop), out var c) ? c : null;
    }

    private async Task<IPlaywright> GetPlaywrightAsync()
        => _playwright ??= await Playwright.CreateAsync();

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null) return _browser;
        var pw = await GetPlaywrightAsync();
        try
        {
            _browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _options.Headless,
            });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            throw new InvalidOperationException(
                "Chromium 尚未安裝。請先執行:parity install-browser", ex);
        }
        return _browser;
    }

    public async ValueTask DisposeAsync()
    {
        // CDP 連線 dispose 只斷線,不會關掉對方(Electron app)的視窗。
        if (_cdpBrowser is not null) await _cdpBrowser.DisposeAsync();
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
