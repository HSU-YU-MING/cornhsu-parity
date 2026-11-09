using System.Text.Json;
using Parity.Engine;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;
using Parity.Engine.DesignSources.Json;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Cli;

/// <summary>一個 target 的完整掃描結果(報告 + 疊圖原料 + 截圖)。</summary>
public sealed record TargetScan(TargetConfig Target, ScanResult Result, byte[]? Screenshot);

/// <summary>
/// check / serve 共用的掃描會話:載設定、建立兩端來源、跑所有 target。
/// 瀏覽器跨次重跑保持存活(serve 的 watch / map 重跑才夠快)。
/// </summary>
public sealed class ScanSession : IAsyncDisposable
{
    public ParityConfig Config { get; }
    public string ConfigPath { get; }

    private readonly IDesignSource _designSource;
    private readonly WebImplementationSource _implSource;
    private readonly FidelityEngine _engine;
    private readonly bool _compareImages;

    /// <param name="extrasAllowed">
    /// false = 不管設定檔怎麼寫都不算多餘元件(報告的 extras 是 null):<c>check --reverse</c> 的報告不帶它,
    /// <c>baseline save</c> 只存落差、用不到它。
    /// </param>
    /// <param name="imagesAllowed">
    /// false = 不管設定檔怎麼寫都不比圖片內容:<c>check --reverse</c> 的現況就是期望,<c>baseline save</c> 不存軟落差。
    /// </param>
    public ScanSession(string configPath, bool refreshCache = false, bool headless = true, bool captureScreenshots = false,
        bool extrasAllowed = true, bool imagesAllowed = true)
    {
        ConfigPath = configPath;
        Config = ParityConfig.Load(configPath);
        if (Config.Targets.Count == 0)
            throw new InvalidOperationException("the config file declares no targets.");


        // 比圖片內容要多拍一張整頁截圖,所以預設關閉
        _compareImages = imagesAllowed && Config.CompareImages;
        _designSource = CreateDesignSource(Config, refreshCache);
        _implSource = new WebImplementationSource(
            new WebCaptureOptions(Headless: headless, CaptureScreenshot: captureScreenshots || _compareImages));
        _engine = new FidelityEngine(_designSource, _implSource,
            new EngineOptions(Config.ToEngineTolerances())
            {
                ComparePosition = Config.ComparePosition,
                ListExtras = extrasAllowed && Config.ListExtras,
                CompareText = Config.CompareText,
            });
    }

    /// <summary>map 檔路徑(未設定時預設 parity.map.json,parity map 寫入用)。</summary>
    public string MapFilePath => Path.Combine(Config.BaseDirectory, Config.MapFile ?? "parity.map.json");

    /// <summary>跑指定(或全部)target。每次重讀 map 檔——map 配對寫入後重跑立即生效。</summary>
    public async Task<List<TargetScan>> RunAsync(string? routeFilter = null, CancellationToken ct = default)
    {
        var targets = routeFilter is null
            ? Config.Targets
            : Config.Targets.Where(t => t.Route == routeFilter).ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException($"no target with route {routeFilter}.");

        var mapSelectors = LoadMapFile();
        // snapshot 基準若帶「穩定 id 白名單」(連拍實測的產物),擷取現場頁面時只拿名單內的
        // id 當錨點——名單外的(現場新長出的隨機 id)走結構路徑,與快照端的 selector 才對得上
        var allowedIdAnchors = Config.DesignFile is { } designFile
            ? JsonDesignSource.TryReadStableIdAnchors(
                Path.GetFullPath(Path.Combine(Config.BaseDirectory, designFile)))
            : null;
        var scans = new List<TargetScan>();
        var accepted = new HashSet<int>(); // 這次有對到落差的 accept 條目

        foreach (var target in targets)
        {
            var designRef = new DesignRef(
                Source: Config.DesignFile is { } df
                    ? Path.GetFullPath(Path.Combine(Config.BaseDirectory, df))
                    : Config.FigmaFileKey ?? throw new InvalidOperationException(
                        "the config needs either figmaFileKey or designFile."),
                NodeId: target.Frame);

            var url = ResolveUrl(target.Url, Config.BaseDirectory);
            // target 有給視窗尺寸就用它(RWD 覆蓋);沒給 → 引擎照舊用設計 frame 尺寸
            var implRef = Config.WithPageSetup(new ImplRef(url, target.Width, target.Height ?? (target.Width is null ? null : 800))
            {
                MapSelectors = mapSelectors,
                IgnoreSelectors = Config.Ignore,
                AllowedIdAnchors = allowedIdAnchors,
            }, target);

            var result = await _engine.RunDetailedAsync(new ScanRequest(designRef, implRef, target.Route), ct);
            NoteUnreadColors(result, target);
            _implSource.Screenshots.TryGetValue(url, out var screenshot);
            if (_compareImages && screenshot is not null)
                result = await CompareImagesAsync(result, target, screenshot, ct);
            // 圖片比對之後才套:image / imageColor 也收得下
            result = result with { Report = Acceptance.Apply(result.Report, Config.Accept, accepted) };
            scans.Add(new TargetScan(target, result, screenshot));
        }
        foreach (var note in Acceptance.StaleNotes(Config.Accept, accepted, targets.Select(t => t.Route).ToHashSet(StringComparer.Ordinal), routeFilter is null))
            Console.Error.WriteLine(note);
        return scans;
    }

    /// <summary>
    /// 配上的元素裡有讀不懂的顏色(例如 <c>color(rec2020 …)</c>)時說一聲:那些顏色沒比(2.5.0)。
    /// 以前讀不懂就當成沒有,背景會被報成透明;現在比對跳過,這裡明講,不讓「沒報」被讀成「一樣」。
    /// 只算配上的元素:沒配上的本來就不比。頁面上的字串一律過 <see cref="ConsoleText.Safe"/>。
    /// </summary>
    internal static string? UnreadColorsNote(ScanResult result, string route)
    {
        var matched = result.Report.Nodes.Select(n => n.Selector).ToHashSet(StringComparer.Ordinal);
        var unread = result.RenderedTree.DescendantsAndSelf()
            .Where(n => n.UnreadColors is not null && matched.Contains(n.Selector))
            .SelectMany(n => n.UnreadColors!.Select(u => (n.Selector, u.Value)))
            .ToList();
        if (unread.Count == 0) return null;
        var (selector, value) = unread[0];
        return $"note: {unread.Count} color value(s) on matched elements of target {ConsoleText.Safe(route)} could not be read, " +
            $"so those colors were not compared (e.g. {ConsoleText.Safe(value)} on ‹{ConsoleText.Safe(selector)}›).";
    }

    private static void NoteUnreadColors(ScanResult result, TargetConfig target)
    {
        if (UnreadColorsNote(result, target.Route) is { } note) Console.Error.WriteLine(note);
    }

    /// <summary>
    /// 比圖片內容。這是軟落差,比不了不該讓 check 失敗:沒有參考圖、參考圖壞了、Figma 匯出失敗(例如被限流),都只在 stderr 說一聲。
    /// </summary>
    private async Task<ScanResult> CompareImagesAsync(ScanResult result, TargetConfig target, byte[] screenshot, CancellationToken ct)
    {
        try
        {
            // Figma 匯出的圖就是 frame 本身,左上角就是 frame 的原點
            if (_designSource is FigmaDesignSource figma)
                return await Parity.Engine.Comparison.ImageComparer.CompareAsync(result,
                    await figma.GetFrameImagePathAsync(new DesignRef(Config.FigmaFileKey!, target.Frame), ct), screenshot, default(Parity.Engine.Model.Box), ct);

            var reference = await ReferenceImagePathAsync(target, ct);
            if (reference is null)
            {
                Console.Error.WriteLine($"note: compare.images: no reference image for target {ConsoleText.Safe(target.Route)}, so images were not compared " +
                    "(a parity snapshot baseline writes one next to the JSON; designImage is its own reference).");
                return result;
            }
            return await Parity.Engine.Comparison.ImageComparer.CompareAsync(result, reference, screenshot, ct: ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Console.Error.WriteLine($"note: images were not compared for target {ConsoleText.Safe(target.Route)}: {ConsoleText.Safe(ex.Message)}");
            return result;
        }
    }

    /// <summary>
    /// 這個 target 的參考圖(Figma 以外)。designImage 就是它自己;快照的參考截圖在 JSON 旁邊:單頁 <c>x.png</c>,
    /// 多頁 <c>x.{第幾個 frame}.png</c>(parity snapshot 寫的,順序與快照裡的 frame 相同)。找不到是 null。
    /// </summary>
    private async Task<string?> ReferenceImagePathAsync(TargetConfig target, CancellationToken ct)
    {
        if (Config.DesignImage is { } image) return Path.GetFullPath(Path.Combine(Config.BaseDirectory, image));
        if (Config.DesignFile is not { } file) return null;

        var path = Path.GetFullPath(Path.Combine(Config.BaseDirectory, file));
        var root = await new JsonDesignSource().GetFrameAsync(new DesignRef(path, ""), ct);
        var index = root.Children.Select(c => c.Id).ToList().IndexOf(target.Frame);
        var candidate = root.Id == "snapshot" && index >= 0
            ? Path.ChangeExtension(path, $".{index}.png")
            : Path.ChangeExtension(path, ".png");
        return File.Exists(candidate) ? candidate : null;
    }

    public bool ShouldFail(IEnumerable<TargetScan> scans)
        => scans.Any(s => Config.ShouldFail(s.Result.Report));

    /// <summary>各 target 的 gate 不通過原因(空 = 全過),附 route 前綴。</summary>
    public List<string> GateFailReasons(IEnumerable<TargetScan> scans)
        => Config.GateFailReasons(scans.Select(s => s.Result.Report));

    /// <summary>各 target 的配對可信度問題(空 = 可信)。baseline 模式也不可豁免。</summary>
    public List<string> MatchIntegrityFailures(IEnumerable<TargetScan> scans)
        => Config.MatchIntegrityFailures(scans.Select(s => s.Result.Report));

    /// <summary>最後一次由本程式寫 map 檔的時間——watch 模式用來略過「自己寫檔」觸發的重掃(API 已經掃了)。</summary>
    public DateTimeOffset? LastMapSaveAt { get; private set; }

    /// <summary>把一筆「圖層名 → selector」寫進 map 檔(parity map 的儲存動作)。</summary>
    public void SaveMapping(string designLayer, string selector)
    {
        var map = LoadMapFile() ?? [];
        map[designLayer] = selector;
        LastMapSaveAt = DateTimeOffset.UtcNow;
        File.WriteAllText(MapFilePath, JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private Dictionary<string, string>? LoadMapFile() => LoadMapFile(MapFilePath);

    /// <summary>
    /// map 檔解析。與 parity.config.json 同一套寬鬆規則(// 註解、尾逗號)——兩個檔並排放,
    /// 只有一邊吃註解是暗雷:照 config 的習慣在 map 檔寫註解會直接炸 JSON 解析錯
    /// (2026-08-26 Codex 補測實踩,見路線 B 文件 B9)。
    /// 注意:`parity map` 儲存時整檔重寫,手寫的註解會消失——註解適合純手寫維護的 map 檔。
    /// </summary>
    internal static Dictionary<string, string>? LoadMapFile(string path)
    {
        if (!File.Exists(path)) return null; // map 檔是「補漏」,沒有就全靠自動配對
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), MapFileOptions);
    }

    private static readonly JsonSerializerOptions MapFileOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static IDesignSource CreateDesignSource(ParityConfig config, bool refresh)
    {
        if (config.DesignImage is { } img)
            return new Parity.Engine.DesignSources.Image.ImageDesignSource(
                Path.GetFullPath(Path.Combine(config.BaseDirectory, img)));
        if (config.DesignFile is not null) return new JsonDesignSource();

        var token = config.ResolveToken()
            ?? throw new InvalidOperationException(
                "missing Figma token: set the FIGMA_TOKEN environment variable (only the file_content:read scope is needed).");
        return new FigmaDesignSource(new FigmaOptions(
            Token: token,
            CacheDirectory: Path.Combine(config.BaseDirectory, ".parity", "cache"),
            RefreshCache: refresh));
    }

    /// <summary>相對路徑(如 "./index.html")→ file:// URI,方便離線示範;其餘原樣。</summary>
    internal static string ResolveUrl(string url, string baseDir)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("file://")
            || WebImplementationSource.IsAttachUrl(url)) // cdp:http://… = attach 到 Electron,原樣傳下去
            return url;
        return new Uri(Path.GetFullPath(Path.Combine(baseDir, url))).AbsoluteUri;
    }

    /// <summary>watch 模式要盯的本機檔案:設定、map、設計 JSON、file:// 的 target 頁面。</summary>
    public IEnumerable<string> WatchableFiles()
    {
        yield return Path.GetFullPath(ConfigPath);
        yield return Path.GetFullPath(MapFilePath);
        if (Config.DesignFile is { } df)
            yield return Path.GetFullPath(Path.Combine(Config.BaseDirectory, df));
        foreach (var t in Config.Targets)
        {
            var url = ResolveUrl(t.Url, Config.BaseDirectory);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                yield return uri.LocalPath;
        }
    }

    public ValueTask DisposeAsync()
    {
        (_designSource as IDisposable)?.Dispose();
        return _implSource.DisposeAsync();
    }
}
