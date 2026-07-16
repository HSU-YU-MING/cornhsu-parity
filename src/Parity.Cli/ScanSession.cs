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

    public ScanSession(string configPath, bool refreshCache = false, bool headless = true, bool captureScreenshots = false)
    {
        ConfigPath = configPath;
        Config = ParityConfig.Load(configPath);
        if (Config.Targets.Count == 0)
            throw new InvalidOperationException("設定檔裡沒有任何 target。");

        _designSource = CreateDesignSource(Config, refreshCache);
        _implSource = new WebImplementationSource(
            new WebCaptureOptions(Headless: headless, CaptureScreenshot: captureScreenshots));
        _engine = new FidelityEngine(_designSource, _implSource,
            new EngineOptions(Config.ToEngineTolerances()));
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
            throw new InvalidOperationException($"找不到 route 為 {routeFilter} 的 target。");

        var mapSelectors = LoadMapFile();
        var scans = new List<TargetScan>();

        foreach (var target in targets)
        {
            var designRef = new DesignRef(
                Source: Config.DesignFile is { } df
                    ? Path.GetFullPath(Path.Combine(Config.BaseDirectory, df))
                    : Config.FigmaFileKey ?? throw new InvalidOperationException(
                        "設定檔需要 figmaFileKey 或 designFile 其中之一。"),
                NodeId: target.Frame);

            var url = ResolveUrl(target.Url, Config.BaseDirectory);
            var implRef = new ImplRef(url)
            {
                MapSelectors = mapSelectors,
                IgnoreSelectors = Config.Ignore,
            };

            var result = await _engine.RunDetailedAsync(new ScanRequest(designRef, implRef, target.Route), ct);
            _implSource.Screenshots.TryGetValue(url, out var screenshot);
            scans.Add(new TargetScan(target, result, screenshot));
        }
        return scans;
    }

    public bool ShouldFail(IEnumerable<TargetScan> scans)
        => scans.Any(s => Config.ShouldFail(s.Result.Report));

    /// <summary>把一筆「圖層名 → selector」寫進 map 檔(parity map 的儲存動作)。</summary>
    public void SaveMapping(string designLayer, string selector)
    {
        var map = LoadMapFile() ?? [];
        map[designLayer] = selector;
        File.WriteAllText(MapFilePath, JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private Dictionary<string, string>? LoadMapFile()
    {
        if (!File.Exists(MapFilePath)) return null; // map 檔是「補漏」,沒有就全靠自動配對
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MapFilePath));
    }

    private static IDesignSource CreateDesignSource(ParityConfig config, bool refresh)
    {
        if (config.DesignFile is not null) return new JsonDesignSource();

        var token = config.ResolveToken()
            ?? throw new InvalidOperationException(
                "缺 Figma token:請設定環境變數 FIGMA_TOKEN(scope 只需 file_content:read)。");
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
