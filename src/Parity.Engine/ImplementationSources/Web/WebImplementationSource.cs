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
    // attach 到既有 Chromium/Electron 的連線,按 endpoint 各一條(與自啟的 _browser 分開)。
    // 只留一條的話,多 target 指向不同 cdp 端點時,第二個會沿用第一條連線抓錯 app。
    private readonly Dictionary<string, IBrowser> _cdpBrowsers = new(StringComparer.OrdinalIgnoreCase);
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
        if (reference.StorageStatePath is { } state && !File.Exists(state))
            throw new InvalidOperationException(
                $"the login state file does not exist: {state} — run `parity login` to sign in once and save it.");
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = reference.ViewportWidth ?? 1280,
                Height = reference.ViewportHeight ?? 800,
            },
            StorageStatePath = reference.StorageStatePath,
            ExtraHTTPHeaders = reference.ExtraHeaders is { Count: > 0 } headers ? headers : null,
        });
        try
        {
            try
            {
                await page.GotoAsync(reference.Url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = _options.TimeoutMs,
                });
            }
            catch (TimeoutException)
            {
                // 長輪詢/websocket/分析心跳的頁面永遠不會 network idle——頁面本身多半早就載好了。
                // 別讓整份報告因此炸掉:確認 DOM load 完成、再給一點渲染緩衝,照常擷取。
                await page.WaitForLoadStateAsync(LoadState.Load, new() { Timeout = 5_000 });
                await page.WaitForTimeoutAsync(500);
            }
            await RunStepsAsync(page, reference);
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

        if (!_cdpBrowsers.TryGetValue(endpoint, out var cdpBrowser))
        {
            var pw = await GetPlaywrightAsync();
            cdpBrowser = await pw.Chromium.ConnectOverCDPAsync(endpoint);
            _cdpBrowsers[endpoint] = cdpBrowser;
        }

        var pages = cdpBrowser.Contexts.SelectMany(c => c.Pages).ToList();
        if (pages.Count == 0)
            throw new InvalidOperationException($"the CDP endpoint has no open page (has the app window finished loading?): {endpoint}");

        var page = pageMatch is null
            ? pages[0]
            : pages.FirstOrDefault(p => p.Url.Contains(pageMatch, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException(
                  $"no page at the CDP endpoint has a URL containing \"{pageMatch}\"; available: {string.Join(", ", pages.Select(p => p.Url))}");

        return await CaptureFromPageAsync(page, reference);
    }

    /// <summary>單一步驟的等待上限:選單、分頁、彈窗幾秒內就該出現,等不到多半是 selector 寫錯。</summary>
    internal const float StepTimeoutMs = 10_000;

    /// <summary>
    /// 設定檔的 steps(2.5.0):頁面載完、凍結動畫之前依序執行。selector 對到好幾個時用第一個。
    /// 失敗就整個擷取失敗(exit 2),訊息說是第幾步、哪個動作——頁面沒到指定狀態就量,量到的是錯的畫面。
    /// 做完再等一下網路安靜(點了分頁常會載資料),等不到不算錯。
    /// </summary>
    private static async Task RunStepsAsync(IPage page, ImplRef reference)
    {
        if (reference.Steps is not { Count: > 0 } steps) return;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            try
            {
                var target = step.Selector is null ? null : page.Locator(step.Selector).First;
                switch (step.Action)
                {
                    case "click": await target!.ClickAsync(new() { Timeout = StepTimeoutMs }); break;
                    case "hover": await target!.HoverAsync(new() { Timeout = StepTimeoutMs }); break;
                    case "fill": await target!.FillAsync(step.Value ?? "", new() { Timeout = StepTimeoutMs }); break;
                    case "waitFor": await target!.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs }); break;
                    case "press":
                        if (target is null) await page.Keyboard.PressAsync(step.Value!);
                        else await target.PressAsync(step.Value!, new() { Timeout = StepTimeoutMs });
                        break;
                    case "wait": await page.WaitForTimeoutAsync(step.Milliseconds ?? 0); break;
                    default: throw new InvalidOperationException($"unknown step action \"{step.Action}\"");
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Playwright 的訊息很長(附呼叫紀錄),只留第一行
                var first = ex.Message.Split('\n', 2)[0].Trim();
                throw new InvalidOperationException($"{reference.Url}: step {i + 1} ({step.Describe()}) failed: {first}", ex);
            }
        }
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); }
        catch (TimeoutException) { /* 長輪詢的頁面永遠不安靜;步驟已經做完,照常量 */ }
    }

    /// <summary>
    /// <c>parity login</c>(2.5.0):開一個看得見的瀏覽器到 <paramref name="url"/>,使用者自己登入,
    /// <paramref name="waitForUser"/> 回來之後把這個分頁的 cookie 與 localStorage 存成 storage state。
    /// 使用者在那之前把視窗關掉 → 什麼都不寫,丟錯說明。
    /// </summary>
    public async Task RecordLoginAsync(string url, string outPath, Func<Task> waitForUser, CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = _options.TimeoutMs });

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Close += (_, _) => closed.TrySetResult();
        var user = waitForUser();
        if (await Task.WhenAny(user, closed.Task).WaitAsync(ct) != user || page.IsClosed)
            throw new InvalidOperationException("the browser window was closed before you confirmed — nothing was saved. Run `parity login` again.");
        await user;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = outPath });
    }

    /// <summary>共用的擷取:在給定頁面上跑擷取腳本 → RenderedNode(啟動導頁與 CDP attach 兩條路都用)。</summary>
    private async Task<RenderedNode> CaptureFromPageAsync(IPage page, ImplRef reference)
    {
        // 量測前凍結動畫/轉場:進場 transition 與 infinite animation 會讓「同一頁的兩次擷取」
        // 量到不同座標(flaky 誤報,dogfooding 實測抓到)。量的是設計意圖的版面,不是動畫中途格。
        // 完成後移除——attach 模式面對的是使用者活著的 app,不能留下痕跡。
        // 已知限制:注入的樣式不會穿進 shadow DOM(文件樣式不 cascade 進去)。
        var freezeStyle = await page.EvaluateHandleAsync("""
            () => {
              const s = document.createElement('style');
              s.textContent = '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }';
              document.head.appendChild(s);
              return s;
            }
            """);
        try
        {
            await page.WaitForTimeoutAsync(100); // 讓凍結後的最終版面套用完

            // 等字型、記下沒載到的(2.5.0)。字型載不到時瀏覽器改用系統字型,文字寬度、換行、位置全跟著變,
            // 整頁的落差都是這一個原因(作品集的守門就這樣整批誤報過)。要在定案之前等:字型換掉版面就會動
            var fontsNotLoaded = await page.EvaluateAsync<string[]>(FontsScript);

            // 凍結之後還要讓版面「定案」——凍結 transition 只保證不再動,不保證停在最終狀態。
            // 見 SettleScript 的說明(含走過的兩條彎路)。
            await SettleLayoutAsync(page, reference);

            var arg = new
            {
                mapSelectors = reference.MapSelectors ?? new Dictionary<string, string>(),
                ignoreSelectors = reference.IgnoreSelectors ?? [],
                // null 原樣傳(腳本端 null = 不限制);空清單語意是「全部 id 都不可信」,不可混同
                allowedIdAnchors = reference.AllowedIdAnchors,
            };
            // 擷取腳本回傳 JSON 字串(見 CaptureScript:避開 Playwright 值序列化的深度放大)。
            // 用放寬的 MaxDepth 解析——真實網站 DOM 常有十幾層巢狀,預設 64 不夠。
            var raw = await page.EvaluateAsync<string>(CaptureScript.Js, arg);
            var json = JsonSerializer.Deserialize<JsonElement>(raw, CaptureParseOptions);
            if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException($"page capture failed (is <body> invisible?): {reference.Url}");

            if (_options.CaptureScreenshot && reference.CaptureScreenshot)
                _screenshots[reference.Url] = await page.ScreenshotAsync(
                    new PageScreenshotOptions { FullPage = true });

            // 整頁文字只掛在根節點(見 RenderedNode.PageText:只給配對用,不外流)
            var root = ParseNode(json) with { FontsNotLoaded = fontsNotLoaded is { Length: > 0 } ? fontsNotLoaded : null };
            return json.TryGetProperty("pageText", out var pageText) && pageText.ValueKind == JsonValueKind.String
                ? root with { PageText = pageText.GetString() }
                : root;
        }
        finally
        {
            await freezeStyle.EvaluateAsync("s => s.remove()");
            await freezeStyle.DisposeAsync();
        }
    }

    /// <summary>
    /// 等 web font 載完(最多 3 秒),回傳沒載到的:status 是 error(網址錯、被擋、CORS),或等完還是 loading。
    /// 瀏覽器只下載頁面用到的字型,所以這裡列出的都是「頁面要、但拿不到」的。unloaded(宣告了沒用到)不算。
    /// 回傳「字型名 粗細 樣式」,去重,最多 20 個。
    /// </summary>
    private const string FontsScript = """
        async () => {
          if (!document.fonts) return [];
          await Promise.race([document.fonts.ready, new Promise(r => setTimeout(r, 3000))]);
          const out = new Set();
          for (const f of document.fonts)
            if (f.status === 'error' || f.status === 'loading')
              out.add(`${f.family.replace(/^["']|["']$/g, '')} ${f.weight} ${f.style}`);
          return [...out].slice(0, 20);
        }
        """;

    /// 撐高視窗時的上限。真實網站整頁高多在數千 px;設上限是防呆(無限捲動、異常長頁),
    /// 免得為了觸發進場效果去配置一張過大的 layout viewport。
    private const int MaxSettleViewportHeight = 20_000;

    /// <summary>
    /// 讓版面定案:暫時把視窗撐到整頁高 → 所有元素同時落在畫面內 → IntersectionObserver
    /// **必然**觸發(不靠捲動、不賭 callback 送達時機) → 還原視窗高 → 最後定案。
    /// class 一旦加上就不會被移除,所以還原後元素仍在展開後的位置。
    ///
    /// attach 模式(使用者活著的 Electron app)刻意跳過:改視窗大小會被使用者看見,
    /// 違反「不留痕跡」。那個情境的頁面通常也不是捲動觸發的行銷頁。
    /// </summary>
    private async Task SettleLayoutAsync(IPage page, ImplRef reference)
    {
        var restoreHeight = IsAttachUrl(reference.Url) ? null : page.ViewportSize;
        if (restoreHeight is not null)
        {
            var docHeight = await page.EvaluateAsync<int>(SettleScript.DocumentHeight);
            var tall = Math.Min(Math.Max(docHeight, restoreHeight.Height), MaxSettleViewportHeight);
            if (tall > restoreHeight.Height)
            {
                await page.SetViewportSizeAsync(restoreHeight.Width, tall);
                await page.EvaluateAsync(SettleScript.Trigger);
                await page.SetViewportSizeAsync(restoreHeight.Width, restoreHeight.Height);
            }
        }

        await page.EvaluateAsync(SettleScript.Settle);
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
                LetterSpacing: Px(el, "letterSpacing"))
            {
                FontStyle = Str(el, "fontStyle"),
                TextDecoration = Str(el, "textDecoration"),
                TextTransform = Str(el, "textTransform"),
                TextAlign = Str(el, "textAlign"),
            },
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
            Margin = Px(el, "marginTop") is { } marginTop
                ? new Insets(marginTop, Px(el, "marginRight") ?? 0, Px(el, "marginBottom") ?? 0, Px(el, "marginLeft") ?? 0)
                : null,
            TextContent = Str(el, "textContent"),
            TextIsInline = el.TryGetProperty("textIsInline", out var textIsInline) && textIsInline.ValueKind == JsonValueKind.True,
            OutOfFlow = el.TryGetProperty("outOfFlow", out var outOfFlow) && outOfFlow.ValueKind == JsonValueKind.True,
            HasBackgroundImage = el.TryGetProperty("hasBackgroundImage", out var hasBackgroundImage) && hasBackgroundImage.ValueKind == JsonValueKind.True,
            ImageBroken = el.TryGetProperty("imageBroken", out var imageBroken) && imageBroken.ValueKind == JsonValueKind.True,
            Opacity = Px(el, "opacity"),
            BorderWidth = Px(el, "borderTopWidth") is { } borderTop
                ? new Insets(borderTop, Px(el, "borderRightWidth") ?? 0, Px(el, "borderBottomWidth") ?? 0, Px(el, "borderLeftWidth") ?? 0)
                : null,
            BorderColor = Color(el, "borderColor"),
            OutlineWidth = Px(el, "outlineWidth"),
            OutlineColor = Color(el, "outlineColor"),
            Shadows = CssEffects.ParseShadows(Str(el, "boxShadow")),
            TextShadows = CssEffects.ParseShadows(Str(el, "textShadow")),
            Gradient = CssEffects.ParseGradient(Str(el, "backgroundImage")),
            LineCount = el.TryGetProperty("lineCount", out var lineCount) && lineCount.ValueKind == JsonValueKind.Number ? lineCount.GetInt32() : null,
            UnreadColors = Unread(el),
        };

        // 讀不懂的顏色記下來(2.5.0):比對跳過、主控台明講,不要當成「沒有」
        static IReadOnlyList<UnreadColor>? Unread(JsonElement e)
        {
            List<UnreadColor>? list = null;
            foreach (var field in ColorFields)
                foreach (var value in CssEffects.UnreadableColors(Str(e, field)))
                    (list ??= []).Add(new UnreadColor(field, value));
            return list;
        }
        static string? Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double? Px(JsonElement e, string prop)
            => Normalizer.ParseCssPx(Str(e, prop));
        static Rgba? Color(JsonElement e, string prop)
            => Rgba.TryParseCss(Str(e, prop), out var c) ? c : null;
    }

    /// <summary>擷取結果裡帶顏色的欄位(CaptureScript 的鍵名)。</summary>
    private static readonly string[] ColorFields =
        ["color", "background", "effectiveBackground", "borderColor", "outlineColor", "boxShadow", "textShadow", "backgroundImage"];

    /// <summary>
    /// any 與 linux-musl-*(Alpine)的平台包不帶 Playwright driver:Playwright 沒有那些平台的 Node。
    /// install-browser 也用這段。exit code 跟單一套件時代一樣(check 這類 2、install-browser 1),只是看得懂。
    /// 平台清單對應 scripts/verify-tool-packages.py 的 NODE_DIR(ToolPackagePlatformTests 守著)。
    /// </summary>
    public const string NoDriverMessage =
        "This platform cannot run commands that open a browser: Playwright has no driver for it, so this package does not include one. "
        + "They need win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 or osx-arm64. "
        + "Commands that do not open a browser, such as `parity lint` and `parity baseline list`, still work.";

    private async Task<IPlaywright> GetPlaywrightAsync()
    {
        if (_playwright is not null) return _playwright;
        try
        {
            return _playwright = await Playwright.CreateAsync();
        }
        catch (PlaywrightException ex) when (ex.Message.StartsWith("Driver not found", StringComparison.Ordinal))
        {
            // Playwright 原文只有一條找不到的 node 路徑,使用者看不出是平台不支援;路徑留著,真的裝壞時查得到
            throw new InvalidOperationException($"{NoDriverMessage} ({ex.Message})", ex);
        }
    }

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
                "Chromium is not installed yet. Run `parity install-browser` first.", ex);
        }
        return _browser;
    }

    public async ValueTask DisposeAsync()
    {
        // CDP 連線 dispose 只斷線,不會關掉對方(Electron app)的視窗。
        foreach (var cdp in _cdpBrowsers.Values) await cdp.DisposeAsync();
        _cdpBrowsers.Clear();
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
