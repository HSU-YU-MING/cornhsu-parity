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
    private readonly Dictionary<string, byte[]> _screenshots = [];

    /// <summary>CaptureScreenshot 開啟時,每次擷取後的整頁 PNG(key = URL)。本機報告 UI 的疊圖底圖。</summary>
    public IReadOnlyDictionary<string, byte[]> Screenshots => _screenshots;

    public async Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default)
    {
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
        finally
        {
            await page.CloseAsync();
        }
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

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null) return _browser;
        _playwright = await Playwright.CreateAsync();
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
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
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
