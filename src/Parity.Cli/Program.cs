using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Cli;
using Parity.Engine;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.ImplementationSources;
using Parity.Storage;

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var rest = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "check" => await CheckCommand.RunAsync(rest),
        "report" => ReportCommand.Run(rest),
        "snapshot" => await SnapshotCommand.RunAsync(rest),
        "lint" => await LintCommand.RunAsync(rest),
        "serve" => await ServeCommand.RunAsync(rest),
        "map" => await ServeCommand.RunAsync(rest, mapMode: true),
        "baseline" => await BaselineCommand.RunAsync(rest),
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
        var opts = CliOptions.Parse(args,
            "--config=", "--target=", "--out=", "--md=", "--refresh", "--headed", "--baseline", "--reverse");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Check);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("找不到 parity.config.json(可用 `parity init` 產生範本)。");

        await using var session = new ScanSession(
            configPath,
            refreshCache: opts.ContainsKey("--refresh"),
            headless: !opts.ContainsKey("--headed"));

        Console.WriteLine($"\x1b[1mParity\x1b[0m — 數值級設計還原度檢查\n設定:{configPath}\n");

        // --reverse:方向反過來——「現況(實作)是真相,設計稿是被檢視的草稿」。
        // 場景:設計師照著現有頁面重畫/改版,想看自己的稿跟現況差在哪。
        // 資料對稱,只需:交換期望/實際欄位(在所有輸出之前)、不做把關
        // (設計師要的是 diff 清單,不是被打紅)。
        var reverse = opts.ContainsKey("--reverse");
        if (reverse && opts.ContainsKey("--baseline"))
            throw new InvalidOperationException("--reverse 與 --baseline 不能同時使用(reverse 不做把關)。");

        var scans = await session.RunAsync(opts.GetValueOrDefault("--target"));
        var reports = scans
            .Select(s => reverse ? SwapExpectations(s.Result.Report) : s.Result.Report)
            .ToList();

        foreach (var (scan, report) in scans.Zip(reports))
        {
            Console.WriteLine($"目標 \x1b[1m{scan.Target.Route}\x1b[0m → {report.Url}" +
                (reverse ? "\x1b[36m(reverse:期望 = 現況、實際 = 設計稿)\x1b[0m" : ""));
            PrintReport(report);
        }

        // JSON 輸出:機器與人都要(規畫書 0.1 決策總表)
        var outPath = opts.GetValueOrDefault("--out")
            ?? Path.Combine(session.Config.BaseDirectory, ".parity", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(reports, ReportJson.Indented));
        Console.WriteLine($"報告已寫入:{outPath}");

        Console.WriteLine($"還原度分數:\x1b[1m{FidelityScore.Compute(reports)}/100\x1b[0m");

        if (reverse)
        {
            WriteMarkdown(opts, session.Config, reports, gateFail: false,
                gateNotes: ["reverse 模式:「期望」= 現況(實作)、「實際」= 設計稿——差異是給設計師看的,不做把關"]);
            Console.WriteLine("\n\x1b[36mreverse 模式\x1b[0m:不做把關,exit 0。");
            return 0;
        }

        // 配對可信度(0 配對 / 低於 minMatchRate):沒配到就沒落差可擋,不能沉默 PASS
        var integrity = session.MatchIntegrityFailures(scans);

        // --baseline:回歸模式——只擋「相對基準新增/惡化」的落差(適合已有一堆落差的專案漸進導入)
        if (opts.ContainsKey("--baseline"))
            return await GateAgainstBaselineAsync(session, scans, opts, integrity);

        var gateReasons = session.GateFailReasons(scans);
        var gateFail = gateReasons.Count > 0;
        WriteMarkdown(opts, session.Config, reports, gateFail, gateNotes: integrity);
        if (gateFail)
        {
            Console.WriteLine($"\n\x1b[31m✘ GATE FAIL\x1b[0m(fail on: {string.Join(", ", session.Config.Gate.FailOn)})");
            foreach (var r in gateReasons)
                Console.WriteLine($"  \x1b[31m·\x1b[0m {r}");
            return 1;
        }
        Console.WriteLine($"\n\x1b[32m✔ PASS\x1b[0m(fail on: {string.Join(", ", session.Config.Gate.FailOn)})");
        return 0;
    }

    /// <summary>--md &lt;path&gt;:把報告輸出成 Markdown(可分享 / 貼 PR 留言);有設定 tokensFile 就帶進 token 提示。</summary>
    private static void WriteMarkdown(
        Dictionary<string, string?> opts, ParityConfig config, IReadOnlyList<FidelityReport> reports,
        bool gateFail, BaselineComparison? baseline = null, IReadOnlyList<string>? gateNotes = null,
        int? baselineScore = null)
    {
        if (opts.GetValueOrDefault("--md") is not { } mdPath) return;
        var tokens = config.TokensFile is { } tf
            ? DesignTokens.LoadJson(Path.Combine(config.BaseDirectory, tf))
            : null;
        var full = Path.GetFullPath(mdPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, MarkdownReport.Render(
            reports, gateFail, baseline, tokens, gateNotes,
            figmaFileKey: config.FigmaFileKey, baselineScore: baselineScore));
        Console.WriteLine($"Markdown 報告:{full}");
    }

    /// <summary>回歸把關:比對現況與最新 baseline,只在有新增/惡化時 GATE FAIL(規畫書 M5)。</summary>
    private static async Task<int> GateAgainstBaselineAsync(
        ScanSession session, List<TargetScan> scans, Dictionary<string, string?> opts,
        List<string> integrity)
    {
        var reports = scans.Select(s => s.Result.Report).ToList();

        // 配對可信度不過就不做 baseline 比對:殘缺的 current 會把 baseline 裡的一切誤判成「修好」
        if (integrity.Count > 0)
        {
            WriteMarkdown(opts, session.Config, reports, gateFail: true, gateNotes: integrity);
            Console.WriteLine("\n\x1b[31m✘ GATE FAIL\x1b[0m(配對可信度不足,不做 baseline 比對)");
            foreach (var r in integrity)
                Console.WriteLine($"  \x1b[31m·\x1b[0m {r}");
            return 1;
        }

        var current = DiffRecord.FromReports(reports);
        await using var store = new BaselineStore(BaselineCommand.BaselineDbPath(session.Config));
        var baseline = await store.GetLatestAsync();

        if (baseline is null)
        {
            Console.WriteLine("\n\x1b[33m(尚無 baseline)\x1b[0m 先跑 `parity baseline save` 建立基準;這次退回一般 gate。");
            Console.WriteLine($"\x1b[90m  提示:CI 要用 --baseline,得把 {Path.GetFileName(BaselineCommand.BaselineDbPath(session.Config))} commit 進 repo。\x1b[0m");
            var fail = session.ShouldFail(scans);
            WriteMarkdown(opts, session.Config, reports, fail);
            Console.WriteLine(fail ? "\x1b[31m✘ GATE FAIL\x1b[0m" : "\x1b[32m✔ PASS\x1b[0m");
            return fail ? 1 : 0;
        }

        var cmp = BaselineComparer.Compare(current, baseline.Diffs);
        WriteMarkdown(opts, session.Config, reports, cmp.HasRegressions, cmp, baselineScore: baseline.Score);
        Console.WriteLine($"\n對比 baseline — \x1b[31m新增 {cmp.Regressions.Count}\x1b[0m、" +
            $"\x1b[33m惡化 {cmp.Worsened.Count}\x1b[0m、\x1b[32m修好 {cmp.Fixed.Count}\x1b[0m、不變 {cmp.Unchanged}");

        // 分數走勢——PM 要的「方向」:相對基準是往上還是往下
        if (baseline.Score is { } baseScore)
        {
            var score = FidelityScore.Compute(reports);
            var trend = score > baseScore ? $"\x1b[32m↑ +{score - baseScore}\x1b[0m"
                : score < baseScore ? $"\x1b[31m↓ {score - baseScore}\x1b[0m" : "→ ±0";
            Console.WriteLine($"還原度走勢:基準 {baseScore}/100 → 現在 {score}/100({trend})");
        }
        foreach (var d in cmp.Regressions)
            Console.WriteLine($"  \x1b[31m+ 新增\x1b[0m {d.Route} ‹{d.DesignLayer}› {d.Prop} [{d.Severity.ToString().ToLowerInvariant()}]");
        foreach (var d in cmp.Worsened)
            Console.WriteLine($"  \x1b[33m↑ 惡化\x1b[0m {d.Route} ‹{d.DesignLayer}› {d.Prop} [{d.Severity.ToString().ToLowerInvariant()}]");
        foreach (var d in cmp.Fixed)
            Console.WriteLine($"  \x1b[32m- 修好\x1b[0m {d.Route} ‹{d.DesignLayer}› {d.Prop}");

        if (cmp.HasRegressions)
        {
            Console.WriteLine("\n\x1b[31m✘ GATE FAIL\x1b[0m(相對 baseline 有新增/惡化)");
            return 1;
        }
        Console.WriteLine("\n\x1b[32m✔ PASS\x1b[0m(相對 baseline 無回歸)");
        return 0;
    }

    /// <summary>reverse 模式:交換每條落差的期望/實際(現況變成「期望」)。分數/嚴重度不受影響。</summary>
    private static FidelityReport SwapExpectations(FidelityReport r) => r with
    {
        Nodes = r.Nodes.Select(n => n with
        {
            Diffs = n.Diffs.Select(d => d with { Expected = d.Actual, Actual = d.Expected }).ToList(),
        }).ToList(),
    };

    private static void PrintReport(FidelityReport report)
    {
        var s = report.Summary;
        Console.WriteLine($"  已配對 {s.Matched}/{s.DesignNodes} 個設計節點;{s.NodesWithDiffs} 個有落差");

        foreach (var node in Impact.Order(report.Nodes.Where(n => n.Diffs.Count > 0)))
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

internal static class ReportCommand
{
    /// <summary>
    /// parity report:從既有 report.json 重生 Markdown,免重掃(重掃要開瀏覽器,幾十秒;
    /// 這裡只是重排版,毫秒級)。CI 已上傳 report.json artifact 時,本機也能重現同一份報告。
    /// </summary>
    public static int Run(string[] args)
    {
        var opts = CliOptions.Parse(args, "--config=", "--in=", "--md=");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Report);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("找不到 parity.config.json(可用 `parity init` 產生範本)。");
        var config = ParityConfig.Load(configPath);

        var inPath = opts.GetValueOrDefault("--in")
            ?? Path.Combine(config.BaseDirectory, ".parity", "report.json");
        if (!File.Exists(inPath))
            throw new FileNotFoundException($"找不到報告:{inPath}(先跑 `parity check`,或用 --in 指定路徑)", inPath);

        var reports = JsonSerializer.Deserialize<List<FidelityReport>>(File.ReadAllText(inPath), ReportJson.Indented)
            ?? throw new InvalidOperationException($"報告解析失敗:{inPath}");

        var tokens = config.TokensFile is { } tf
            ? DesignTokens.LoadJson(Path.Combine(config.BaseDirectory, tf))
            : null;
        var md = MarkdownReport.Render(
            reports,
            gateFail: config.GateFailReasons(reports).Count > 0,
            tokens: tokens,
            gateNotes: config.MatchIntegrityFailures(reports));

        if (opts.GetValueOrDefault("--md") is { } mdPath)
        {
            var full = Path.GetFullPath(mdPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, md);
            Console.WriteLine($"Markdown 報告:{full}");
        }
        else
        {
            Console.Write(md); // 沒給 --md 就印到 stdout,方便管線接走
        }
        return 0;
    }
}

internal static class LintCommand
{
    /// <summary>
    /// parity lint:design lint——只看設計稿,驗值是否落在 design token 允許集合
    /// (顏色 / fontSize / padding / itemSpacing / cornerRadius)。
    /// 場景:設計師畫新頁面要跟設計系統一致。不開瀏覽器、不比對實作。
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = CliOptions.Parse(args, "--config=", "--target=", "--refresh");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Lint);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("找不到 parity.config.json(可用 `parity init` 產生範本)。");
        var config = ParityConfig.Load(configPath);

        var tokens = config.TokensFile is { } tf
            ? DesignTokens.LoadJson(Path.Combine(config.BaseDirectory, tf))
            : null;
        if (tokens is null)
            throw new InvalidOperationException(
                "lint 需要 design token:請在設定檔設 tokensFile(平面 JSON:{\"token 名\":\"值\"})。");

        var targets = opts.GetValueOrDefault("--target") is { } routeFilter
            ? config.Targets.Where(t => t.Route == routeFilter).ToList()
            : config.Targets;
        if (targets.Count == 0)
            throw new InvalidOperationException("設定檔裡沒有任何 target。");

        Console.WriteLine($"\x1b[1mParity lint\x1b[0m — 設計稿 token 規範檢查(只看設計,不比實作)\n");

        var source = ScanSession.CreateDesignSource(config, refresh: opts.ContainsKey("--refresh"));
        try
        {
            var total = 0;
            var allViolations = new List<(string Route, LintViolation V)>();
            foreach (var t in targets)
            {
                var designRef = new DesignRef(
                    Source: config.DesignFile is { } df
                        ? Path.GetFullPath(Path.Combine(config.BaseDirectory, df))
                        : config.FigmaFileKey ?? throw new InvalidOperationException(
                            "設定檔需要 figmaFileKey 或 designFile 其中之一。"),
                    NodeId: t.Frame);
                var tree = await source.GetFrameAsync(designRef);
                var nodes = tree.DescendantsAndSelf().Count();
                total += nodes;
                var violations = DesignLint.Run(tree, tokens, config.Tolerances.ColorDeltaE);
                allViolations.AddRange(violations.Select(v => (t.Route, v)));
                Console.WriteLine($"目標 \x1b[1m{t.Route}\x1b[0m:{nodes} 個節點,{violations.Count} 條違規");
            }

            foreach (var (route, v) in allViolations)
            {
                var near = v.NearestToken is null
                    ? ""
                    : $";最近:\x1b[36m{v.NearestToken}\x1b[0m = {v.NearestValue}" +
                      (v.Prop == "color" ? $"(ΔE {v.Distance})" : $"(差 {v.Distance})");
                Console.WriteLine($"  \x1b[33m✘ {v.Layer}\x1b[0m {v.Prop} = {v.Value} 不在 token 內{near}");
            }

            if (allViolations.Count > 0)
            {
                Console.WriteLine($"\n\x1b[31m✘ {allViolations.Count} 條違規\x1b[0m(共檢查 {total} 個節點)");
                return 1;
            }
            Console.WriteLine($"\n\x1b[32m✔ 全部符合 token 規範\x1b[0m(共檢查 {total} 個節點)");
            return 0;
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }
    }
}

internal static class SnapshotCommand
{
    /// <summary>
    /// parity snapshot:把「現在跑著的實作」凍結成設計基準(design JSON + 參考截圖)。
    /// 用途:重構/改版守門——現在的畫面是對的,之後 check 保證不跑版(visual regression 的數值版)。
    /// 不經 ScanSession(它會建設計來源;snapshot 只需要實作端,連 Figma 設定都不用)。
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = CliOptions.Parse(args,
            "--config=", "--target=", "--out=", "--width=", "--height=", "--headed");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Snapshot);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("找不到 parity.config.json(可用 `parity init` 產生範本)。");
        var config = ParityConfig.Load(configPath);
        if (config.Targets.Count == 0)
            throw new InvalidOperationException("設定檔裡沒有任何 target。");

        var targets = opts.GetValueOrDefault("--target") is { } route
            ? config.Targets.Where(t => t.Route == route).ToList()
            : config.Targets;
        if (targets.Count == 0)
            throw new InvalidOperationException("找不到指定 route 的 target。");

        // 快照的視窗大小 = 之後 check 的視窗大小(存進 frame box,check 會照它開視窗)
        var width = int.TryParse(opts.GetValueOrDefault("--width"), out var w) ? w : 1280;
        var height = int.TryParse(opts.GetValueOrDefault("--height"), out var h) ? h : 800;

        Console.WriteLine($"\x1b[1mParity snapshot\x1b[0m — 把現在的畫面凍結成設計基準({width}×{height})\n");

        await using var impl = new Parity.Engine.ImplementationSources.Web.WebImplementationSource(
            new Parity.Engine.ImplementationSources.Web.WebCaptureOptions(
                Headless: !opts.ContainsKey("--headed"), CaptureScreenshot: true));

        var outPath = Path.GetFullPath(opts.GetValueOrDefault("--out")
            ?? Path.Combine(config.BaseDirectory, "parity.snapshot.json"));
        var frames = new List<DesignNode>();
        var shotPaths = new List<string>();

        foreach (var (t, i) in targets.Select((t, i) => (t, i)))
        {
            var url = ScanSession.ResolveUrl(t.Url, config.BaseDirectory);
            var tree = await impl.CaptureAsync(new ImplRef(url, t.Width ?? width, t.Height ?? height)
            {
                IgnoreSelectors = config.Ignore,
            });
            frames.Add(SnapshotBuilder.ToFrame(tree, t.Route, t.Width ?? width, t.Height ?? height));

            if (impl.Screenshots.TryGetValue(url, out var png))
            {
                var shot = targets.Count == 1
                    ? Path.ChangeExtension(outPath, ".png")
                    : Path.ChangeExtension(outPath, $".{i}.png");
                await File.WriteAllBytesAsync(shot, png);
                shotPaths.Add(shot);
            }
            Console.WriteLine($"  ✓ {t.Route} → {tree.DescendantsAndSelf().Count()} 個節點");
        }

        // 單 target:frame 直接當根;多 target:包一層,frame id = route(對 config 的 target.frame)
        var root = frames.Count == 1
            ? frames[0]
            : new DesignNode("snapshot", "snapshot", DesignNodeType.Frame, default,
                null, null, null, null, null, frames);

        // 基準是「對的樣子」的唯一紀錄——覆寫前先備份到 .parity/(慣例上不進版控),
        // 在站台壞掉時誤拍也有無摩擦的後悔藥(不用 --force 是刻意的:重拍本來就是日常動作)。
        if (File.Exists(outPath))
        {
            var bakDir = Path.Combine(config.BaseDirectory, ".parity");
            Directory.CreateDirectory(bakDir);
            var bak = Path.Combine(bakDir, "snapshot.bak.json");
            File.Copy(outPath, bak, overwrite: true);
            Console.WriteLine($"既有基準已備份:{bak}(誤拍可用它救回)");
        }
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(root, ReportJson.Indented));

        Console.WriteLine($"\n已寫入:{outPath}");
        foreach (var s in shotPaths) Console.WriteLine($"參考截圖:{s}");
        Console.WriteLine($"""

            下一步(把快照當設計基準,重構不跑版):
              1. {Path.GetFileName(configPath)} 設 "designFile": "{Path.GetFileName(outPath)}"(figmaFileKey 可拿掉)
              2. 每個 target 的 "frame" 填自己的 route(如 "/")
              3. 之後 parity check = 檢查畫面是否仍與快照一致
            """);
        return 0;
    }
}

internal static class InitCommand
{
    public static int Run(string[] args)
    {
        var opts = CliOptions.Parse(args);
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Init);
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
        var opts = CliOptions.Parse(args, "--with-deps");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.InstallBrowser);
        // --with-deps:連同系統相依一起裝(CI 的 Linux runner 需要,否則 Chromium 起不來)
        var withDeps = opts.ContainsKey("--with-deps");
        string[] pwArgs = withDeps ? ["install", "--with-deps", "chromium"] : ["install", "chromium"];
        Console.WriteLine(withDeps
            ? "下載 Chromium 並安裝系統相依(CI 用,第一次需要幾分鐘)…"
            : "下載 Playwright Chromium(第一次需要幾分鐘)…");
        var exitCode = Microsoft.Playwright.Program.Main(pwArgs);
        Console.WriteLine(exitCode == 0 ? "完成。" : "安裝失敗。");
        return exitCode;
    }
}

/// <summary>
/// 各指令的用法文字——單一來源:主 help 由這些段落組成,各子指令的 --help 也印同一段,
/// 兩邊不會漂移。
/// </summary>
internal static class Usage
{
    public const string Check = """
          parity check [--config <path>] [--target <route>] [--out <path>] [--refresh] [--headed] [--baseline] [--reverse] [--md <path>]
              抓設計端與實作端真實數值比對,輸出報告 + exit code
              --refresh   忽略 Figma 本機快取重抓
              --headed    顯示瀏覽器視窗(除錯用)
              --baseline  回歸模式:只擋「相對基準新增/惡化」的落差(見 parity baseline)
              --reverse   反向檢視:「期望」= 現況(實作)、「實際」= 設計稿;不做把關
                          (設計師照現有頁面重畫/改版時,看自己的稿跟現況差在哪)
              --md <path> 另外輸出 Markdown 報告(含還原度分數 + 建議修法,可貼 PR 留言)
              target 的 url 可以是:
                http(s):// 或 file://   一般網頁 / 本機頁面
                cdp:http://host:port    連進已在跑的 Electron 桌面 app(抓活視窗)
                cdp:http://host:port#url片段  多視窗時指定 URL 含該片段的視窗
                (Electron 端啟動時加 --remote-debugging-port=<port>)
        """;

    public const string Report = """
          parity report [--config <path>] [--in <report.json>] [--md <path>]
              從既有 report.json 重生 Markdown 報告,免重掃(預設讀 .parity/report.json;
              沒給 --md 就印到 stdout)
        """;

    public const string Snapshot = """
          parity snapshot [--config <path>] [--target <route>] [--out <path>] [--width <n>] [--height <n>] [--headed]
              把「現在跑著的實作」凍結成設計基準(JSON + 參考截圖)——重構/改版守門:
              現在的畫面是對的,之後 check 保證不跑版。不需要 Figma。
              會覆寫既有基準(覆寫前自動備份到 .parity/snapshot.bak.json)
        """;

    public const string Serve = """
          parity serve [--config <path>] [--port <n>] [--watch] [--open]
              本機報告 UI(只綁 127.0.0.1):落差清單 + 截圖疊框視圖
              --watch     設定/設計/頁面檔變更時自動重掃
        """;

    public const string Map = """
          parity map [--config <path>] [--port <n>]
              互動配對:點選未配對的設計節點 → 點頁面元素 → 寫入 parity.map.json
        """;

    public const string Lint = """
          parity lint [--config <path>] [--target <route>] [--refresh]
              design lint:只看設計稿,驗值是否落在 design token 允許集合
              (顏色/字級/內距/間距/圓角;需 tokensFile)。設計師畫新頁面守設計系統用。
        """;

    public const string Baseline = """
          parity baseline save|list [--config <path>]
              存/看落差基準快照(SQLite);搭配 check --baseline 做回歸把關
        """;

    public const string Init = """
          parity init             產生 parity.config.json 範本
        """;

    public const string InstallBrowser = """
          parity install-browser [--with-deps]
              下載 Playwright Chromium(第一次必要);--with-deps 連系統相依一起裝(CI 用)
        """;

    /// <summary>子指令 --help:印該指令的用法,exit 0。</summary>
    public static int Print(string usage)
    {
        Console.WriteLine("用法:");
        Console.WriteLine(usage);
        return 0;
    }
}

internal static class HelpCommand
{
    public static int Run()
    {
        Console.WriteLine("Parity — 數值級設計還原度檢查工具\n");
        Console.WriteLine("用法:");
        foreach (var usage in new[]
        {
            Usage.Check, Usage.Report, Usage.Snapshot, Usage.Serve, Usage.Map,
            Usage.Lint, Usage.Baseline, Usage.Init, Usage.InstallBrowser,
        })
            Console.WriteLine(usage);
        Console.WriteLine("\nexit code:0 = 通過;1 = 落差超過 gate 門檻;2 = 執行錯誤");
        Console.WriteLine("每個子指令都可加 --help 看自己的用法。");
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
