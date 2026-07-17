using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Cli;
using Parity.Engine;

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var rest = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "check" => await CheckCommand.RunAsync(rest),
        "serve" => await ServeCommand.RunAsync(rest),
        "map" => await ServeCommand.RunAsync(rest, mapMode: true),
        "init" => InitCommand.Run(rest),
        "install-browser" => InstallBrowserCommand.Run(rest),
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
        var opts = CliOptions.Parse(args);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("找不到 parity.config.json(可用 `parity init` 產生範本)。");

        await using var session = new ScanSession(
            configPath,
            refreshCache: opts.ContainsKey("--refresh"),
            headless: !opts.ContainsKey("--headed"));

        Console.WriteLine($"\x1b[1mParity\x1b[0m — 數值級設計還原度檢查\n設定:{configPath}\n");

        var scans = await session.RunAsync(opts.GetValueOrDefault("--target"));
        foreach (var scan in scans)
        {
            Console.WriteLine($"目標 \x1b[1m{scan.Target.Route}\x1b[0m → {scan.Result.Report.Url}");
            PrintReport(scan.Result.Report);
        }

        // JSON 輸出:機器與人都要(規畫書 0.1 決策總表)
        var outPath = opts.GetValueOrDefault("--out")
            ?? Path.Combine(session.Config.BaseDirectory, ".parity", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath,
            JsonSerializer.Serialize(scans.Select(s => s.Result.Report), JsonOptions));
        Console.WriteLine($"報告已寫入:{outPath}");

        if (session.ShouldFail(scans))
        {
            Console.WriteLine($"\n\x1b[31m✘ GATE FAIL\x1b[0m(fail on: {string.Join(", ", session.Config.Gate.FailOn)})");
            return 1;
        }
        Console.WriteLine($"\n\x1b[32m✔ PASS\x1b[0m(fail on: {string.Join(", ", session.Config.Gate.FailOn)})");
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
    public static int Run(string[] args)
    {
        // --with-deps:連同系統相依一起裝(CI 的 Linux runner 需要,否則 Chromium 起不來)
        var withDeps = args.Contains("--with-deps");
        string[] pwArgs = withDeps ? ["install", "--with-deps", "chromium"] : ["install", "chromium"];
        Console.WriteLine(withDeps
            ? "下載 Chromium 並安裝系統相依(CI 用,第一次需要幾分鐘)…"
            : "下載 Playwright Chromium(第一次需要幾分鐘)…");
        var exitCode = Microsoft.Playwright.Program.Main(pwArgs);
        Console.WriteLine(exitCode == 0 ? "完成。" : "安裝失敗。");
        return exitCode;
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
                  抓設計端與實作端真實數值比對,輸出報告 + exit code
                  --refresh   忽略 Figma 本機快取重抓
                  --headed    顯示瀏覽器視窗(除錯用)
                  target 的 url 可以是:
                    http(s):// 或 file://   一般網頁 / 本機頁面
                    cdp:http://host:port    連進已在跑的 Electron 桌面 app(抓活視窗)
                    (Electron 端啟動時加 --remote-debugging-port=<port>)
              parity serve [--config <path>] [--port <n>] [--watch] [--open]
                  本機報告 UI(只綁 127.0.0.1):落差清單 + 截圖疊框視圖
                  --watch     設定/設計/頁面檔變更時自動重掃
              parity map [--config <path>] [--port <n>]
                  互動配對:點選未配對的設計節點 → 點頁面元素 → 寫入 parity.map.json
              parity init             產生 parity.config.json 範本
              parity install-browser [--with-deps]
                  下載 Playwright Chromium(第一次必要);--with-deps 連系統相依一起裝(CI 用)

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
