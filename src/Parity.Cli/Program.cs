using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Cli;
using Parity.Engine;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;
using Parity.Engine.DesignSources.Json;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var rest = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "check" => await CheckCommand.RunAsync(rest),
        "init" => InitCommand.Run(rest),
        "install-browser" => InstallBrowserCommand.Run(),
        "map" => StubCommand.Map(),
        "serve" => StubCommand.Serve(),
        "help" or "--help" or "-h" => HelpCommand.Run(),
        "version" or "--version" => VersionCommand.Run(),
        _ => UnknownCommand(command),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\x1b[31m錯誤:{ex.Message}\x1b[0m");
    return 2;
}

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"未知指令:{cmd}");
    HelpCommand.Run();
    return 2;
}

// ─────────────────────────────────────────────────────────────

internal static class CheckCommand
{
    /// <summary>parity check:讀 parity.config.json → 跑引擎 → 人看的摘要 + report.json + exit code。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = ParseOptions(args);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("找不到 parity.config.json(可用 `parity init` 產生範本)。");

        var config = ParityConfig.Load(configPath);
        if (config.Targets.Count == 0)
            throw new InvalidOperationException("設定檔裡沒有任何 target。");

        var targetFilter = opts.GetValueOrDefault("--target");
        var targets = targetFilter is null
            ? config.Targets
            : config.Targets.Where(t => t.Route == targetFilter).ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException($"找不到 route 為 {targetFilter} 的 target。");

        var mapSelectors = LoadMapFile(config);
        var designSource = CreateDesignSource(config, opts.ContainsKey("--refresh"));
        await using var implSource = new WebImplementationSource(
            new WebCaptureOptions(Headless: !opts.ContainsKey("--headed")));

        var engine = new FidelityEngine(designSource, implSource,
            new EngineOptions(config.ToEngineTolerances()));

        Console.WriteLine($"\x1b[1mParity\x1b[0m — 數值級設計還原度檢查\n設定:{configPath}\n");

        var reports = new List<FidelityReport>();
        var anyGateFail = false;

        foreach (var target in targets)
        {
            var designRef = new DesignRef(
                Source: config.DesignFile is { } df
                    ? Path.GetFullPath(Path.Combine(config.BaseDirectory, df))
                    : config.FigmaFileKey ?? throw new InvalidOperationException(
                        "設定檔需要 figmaFileKey 或 designFile 其中之一。"),
                NodeId: target.Frame);

            var implRef = new ImplRef(ResolveUrl(target.Url, config.BaseDirectory))
            {
                MapSelectors = mapSelectors,
                IgnoreSelectors = config.Ignore,
            };

            Console.WriteLine($"目標 \x1b[1m{target.Route}\x1b[0m → {implRef.Url}");
            var report = await engine.RunAsync(new ScanRequest(designRef, implRef, target.Route));
            reports.Add(report);

            PrintReport(report);
            if (config.ShouldFail(report)) anyGateFail = true;
        }

        // JSON 輸出:機器與人都要(規畫書 0.1 決策總表)
        var outPath = opts.GetValueOrDefault("--out")
            ?? Path.Combine(config.BaseDirectory, ".parity", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(reports, JsonOptions));
        Console.WriteLine($"報告已寫入:{outPath}");

        if (anyGateFail)
        {
            Console.WriteLine($"\n\x1b[31m✘ GATE FAIL\x1b[0m(fail on: {string.Join(", ", config.Gate.FailOn)})");
            return 1;
        }
        Console.WriteLine($"\n\x1b[32m✔ PASS\x1b[0m(fail on: {string.Join(", ", config.Gate.FailOn)})");
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

    private static Dictionary<string, string>? LoadMapFile(ParityConfig config)
    {
        if (config.MapFile is null) return null;
        var path = Path.Combine(config.BaseDirectory, config.MapFile);
        if (!File.Exists(path)) return null; // map 檔是「補漏」,沒有就全靠自動配對
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
    }

    /// <summary>相對路徑(如 "./index.html")→ file:// URI,方便離線示範;其餘原樣。</summary>
    private static string ResolveUrl(string url, string baseDir)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("file://"))
            return url;
        return new Uri(Path.GetFullPath(Path.Combine(baseDir, url))).AbsoluteUri;
    }

    private static void PrintReport(FidelityReport report)
    {
        var s = report.Summary;
        Console.WriteLine($"  已配對 {s.Matched}/{s.DesignNodes} 個設計節點;{s.NodesWithDiffs} 個有落差");

        foreach (var node in report.Nodes.Where(n => n.Diffs.Count > 0))
        {
            Console.WriteLine($"  \x1b[33m✘ {node.DesignLayer}\x1b[0m ‹{node.Selector}› " +
                $"[{node.Severity.ToString().ToLowerInvariant()}] ({node.MatchedBy})");
            foreach (var diff in node.Diffs)
            {
                var isColor = diff.Prop is "color" or "background";
                var expected = isColor ? diff.Expected : $"{diff.Expected}{diff.Unit}";
                var actual = isColor
                    ? $"{diff.Actual}{(diff.Delta is { } de ? $" (ΔE {de})" : "")}"
                    : $"{diff.Actual}{diff.Unit}";
                var soft = diff.Soft ? " [soft]" : "";
                Console.WriteLine($"      {diff.Prop,-14} 期望 {expected}  實際 {actual}{soft}");
            }
        }

        if (report.Unmatched.Count > 0)
            Console.WriteLine($"  \x1b[90m未配對:{string.Join("、", report.Unmatched.Select(u => $"{u.DesignLayer} ({u.Reason})"))}\x1b[0m");
        Console.WriteLine();
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            result[args[i]] = hasValue ? args[++i] : null;
        }
        return result;
    }
}

internal static class InitCommand
{
    public static int Run(string[] args)
    {
        const string path = "parity.config.json";
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} 已存在,不覆蓋。");
            return 2;
        }
        File.WriteAllText(path, """
            {
              "figmaFileKey": "你的 Figma 檔案 key",
              "designToken": "env:FIGMA_TOKEN",
              "mapFile": "parity.map.json",
              "targets": [
                { "route": "/", "frame": "10:2", "url": "http://localhost:8080/" }
              ],
              "compare": { "position": "relative" },
              "tolerances": { "sizePx": 2, "spacingPx": 2, "colorDeltaE": 2.0 },
              "ignore": ["[data-parity-ignore]"],
              "gate": { "failOn": ["critical", "serious"] }
            }
            """);
        Console.WriteLine($"已建立 {path}。接著:");
        Console.WriteLine("  1. 填入 figmaFileKey 與 target(frame nodeId + URL)");
        Console.WriteLine("  2. 設定環境變數 FIGMA_TOKEN");
        Console.WriteLine("  3. parity install-browser(第一次)");
        Console.WriteLine("  4. parity check");
        return 0;
    }
}

internal static class InstallBrowserCommand
{
    public static int Run()
    {
        Console.WriteLine("下載 Playwright Chromium(第一次需要幾分鐘)…");
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        Console.WriteLine(exitCode == 0 ? "完成。" : "安裝失敗。");
        return exitCode;
    }
}

internal static class StubCommand
{
    public static int Map()
    {
        Console.WriteLine("`parity map` 互動配對將在 M3(本機報告 UI)提供。");
        Console.WriteLine("現在可以手動編輯 parity.map.json(圖層名 → CSS selector):");
        Console.WriteLine("""  { "cta-button": "main > button.cta" }""");
        return 0;
    }

    public static int Serve()
    {
        Console.WriteLine("`parity serve` 本機報告 UI 排在 M3,尚未實作。");
        Console.WriteLine("目前可先用 `parity check --out report.json` 拿 JSON 報告。");
        return 0;
    }
}

internal static class HelpCommand
{
    public static int Run()
    {
        Console.WriteLine("""
            Parity — 數值級設計還原度檢查工具

            用法:
              parity check [--config <path>] [--target <route>] [--out <path>] [--refresh] [--headed]
                  讀 parity.config.json,抓設計端與實作端真實數值比對,輸出報告 + exit code
                  --refresh   忽略 Figma 本機快取重抓
                  --headed    顯示瀏覽器視窗(除錯用)
              parity init             產生 parity.config.json 範本
              parity install-browser  下載 Playwright Chromium(第一次必要)
              parity map              互動配對(M3)
              parity serve            本機報告 UI(M3)

            exit code:0 = 通過;1 = 落差超過 gate 門檻;2 = 執行錯誤
            """);
        return 0;
    }
}

internal static class VersionCommand
{
    public static int Run()
    {
        Console.WriteLine($"parity {typeof(VersionCommand).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}");
        return 0;
    }
}
