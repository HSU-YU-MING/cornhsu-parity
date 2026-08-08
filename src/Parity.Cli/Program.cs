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
    Console.Error.WriteLine($"\x1b[31merror: {ex.Message}\x1b[0m");
    return 2;
}

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"unknown command: {cmd}");
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
            ?? throw new FileNotFoundException("parity.config.json not found (run `parity init` to generate a template).");

        await using var session = new ScanSession(
            configPath,
            refreshCache: opts.ContainsKey("--refresh"),
            headless: !opts.ContainsKey("--headed"));

        Console.WriteLine($"\x1b[1mParity\x1b[0m — property-level design fidelity check\nconfig: {configPath}\n");

        // --reverse:方向反過來——「現況(實作)是真相,設計稿是被檢視的草稿」。
        // 場景:設計師照著現有頁面重畫/改版,想看自己的稿跟現況差在哪。
        // 資料對稱,只需:交換期望/實際欄位(在所有輸出之前)、不做把關
        // (設計師要的是 diff 清單,不是被打紅)。
        var reverse = opts.ContainsKey("--reverse");
        if (reverse && opts.ContainsKey("--baseline"))
            throw new InvalidOperationException("--reverse and --baseline cannot be combined (reverse mode does not gate).");

        var scans = await session.RunAsync(opts.GetValueOrDefault("--target"));
        var reports = scans
            .Select(s => reverse ? SwapExpectations(s.Result.Report) : s.Result.Report)
            .ToList();

        foreach (var (scan, report) in scans.Zip(reports))
        {
            Console.WriteLine($"target \x1b[1m{scan.Target.Route}\x1b[0m → {report.Url}" +
                (reverse ? "\x1b[36m (reverse: expected = implementation, actual = design)\x1b[0m" : ""));
            PrintReport(report);
        }

        // JSON 輸出:機器與人都要(規畫書 0.1 決策總表)
        var outPath = opts.GetValueOrDefault("--out")
            ?? Path.Combine(session.Config.BaseDirectory, ".parity", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(ReportDocument.Of(reports), ReportJson.Indented));
        Console.WriteLine($"report written: {outPath}");

        Console.WriteLine($"fidelity score: \x1b[1m{FidelityScore.Compute(reports)}/100\x1b[0m");

        if (reverse)
        {
            WriteMarkdown(opts, session.Config, reports, gateFail: false,
                gateNotes: ["reverse mode: \"expected\" = the implementation, \"actual\" = the design file — the diff is for designers to read, no gating"]);
            Console.WriteLine("\n\x1b[36mreverse mode\x1b[0m: no gating, exit 0.");
            return 0;
        }

        // 配對可信度(0 配對 / 低於 minMatchRate):沒配到就沒落差可擋,不能沉默 PASS
        var integrity = session.MatchIntegrityFailures(scans);

        // --baseline:回歸模式——只擋「相對基準新增/惡化」的落差(適合已有一堆落差的專案漸進導入)
        if (opts.ContainsKey("--baseline"))
            return await GateAgainstBaselineAsync(session, scans, opts, integrity);

        // 配對可信度不足 ≠ 落差超門檻:前者是「結果不可信」(通常 url/frame 設定錯),
        // 後者是「東西真的做歪了」。給不同 exit code(3 vs 1),CI 才分得清該重設定還是修實作。
        if (integrity.Count > 0)
        {
            WriteMarkdown(opts, session.Config, reports, gateFail: true, gateNotes: integrity);
            Console.WriteLine("\n\x1b[31m✘ MATCH INTEGRITY TOO LOW\x1b[0m (results are not trustworthy; no gate verdict was made)");
            foreach (var r in integrity)
                Console.WriteLine($"  \x1b[31m·\x1b[0m {r}");
            return 3;
        }

        var gateReasons = session.GateFailReasons(scans);
        var gateFail = gateReasons.Count > 0;
        WriteMarkdown(opts, session.Config, reports, gateFail);
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
        Console.WriteLine($"markdown report: {full}");
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
            Console.WriteLine("\n\x1b[31m✘ MATCH INTEGRITY TOO LOW\x1b[0m (skipping baseline comparison)");
            foreach (var r in integrity)
                Console.WriteLine($"  \x1b[31m·\x1b[0m {r}");
            return 3;
        }

        var current = DiffRecord.FromReports(reports);
        await using var store = new BaselineStore(BaselineCommand.BaselineDbPath(session.Config));
        var baseline = await store.GetLatestAsync();

        if (baseline is null)
        {
            Console.WriteLine("\n\x1b[33m(no baseline yet)\x1b[0m run `parity baseline save` first; falling back to the normal gate this time.");
            Console.WriteLine($"\x1b[90m  hint: to use --baseline in CI, commit {Path.GetFileName(BaselineCommand.BaselineDbPath(session.Config))} into the repo.\x1b[0m");
            var fail = session.ShouldFail(scans);
            WriteMarkdown(opts, session.Config, reports, fail);
            Console.WriteLine(fail ? "\x1b[31m✘ GATE FAIL\x1b[0m" : "\x1b[32m✔ PASS\x1b[0m");
            return fail ? 1 : 0;
        }

        var cmp = BaselineComparer.Compare(current, baseline.Diffs);
        WriteMarkdown(opts, session.Config, reports, cmp.HasRegressions, cmp, baselineScore: baseline.Score);
        Console.WriteLine($"\nvs baseline — \x1b[31mnew {cmp.Regressions.Count}\x1b[0m, " +
            $"\x1b[33mworsened {cmp.Worsened.Count}\x1b[0m, \x1b[32mfixed {cmp.Fixed.Count}\x1b[0m, unchanged {cmp.Unchanged}");

        // 分數走勢——PM 要的「方向」:相對基準是往上還是往下
        if (baseline.Score is { } baseScore)
        {
            var score = FidelityScore.Compute(reports);
            var trend = score > baseScore ? $"\x1b[32m↑ +{score - baseScore}\x1b[0m"
                : score < baseScore ? $"\x1b[31m↓ {score - baseScore}\x1b[0m" : "→ ±0";
            Console.WriteLine($"fidelity trend: baseline {baseScore}/100 → now {score}/100 ({trend})");
        }
        foreach (var d in cmp.Regressions)
            Console.WriteLine($"  \x1b[31m+ new\x1b[0m      {d.Route} ‹{d.DesignLayer}› {d.Prop} [{d.Severity.ToString().ToLowerInvariant()}]");
        foreach (var d in cmp.Worsened)
            Console.WriteLine($"  \x1b[33m↑ worsened\x1b[0m {d.Route} ‹{d.DesignLayer}› {d.Prop} [{d.Severity.ToString().ToLowerInvariant()}]");
        foreach (var d in cmp.Fixed)
            Console.WriteLine($"  \x1b[32m- fixed\x1b[0m    {d.Route} ‹{d.DesignLayer}› {d.Prop}");

        if (cmp.HasRegressions)
        {
            Console.WriteLine("\n\x1b[31m✘ GATE FAIL\x1b[0m (new or worsened diffs vs baseline)");
            return 1;
        }
        Console.WriteLine("\n\x1b[32m✔ PASS\x1b[0m (no regressions vs baseline)");
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
        Console.WriteLine($"  matched {s.Matched}/{s.DesignNodes} design nodes; {s.NodesWithDiffs} with diffs");

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
                Console.WriteLine($"      {diff.Prop,-14} expected {expected}  actual {actual}{soft}");
            }
        }

        if (report.Unmatched.Count > 0)
            Console.WriteLine($"  \x1b[90munmatched: {string.Join(", ", report.Unmatched.Select(u => $"{u.DesignLayer} ({u.Reason})"))}\x1b[0m");
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
            ?? throw new FileNotFoundException("parity.config.json not found (run `parity init` to generate a template).");
        var config = ParityConfig.Load(configPath);

        var inPath = opts.GetValueOrDefault("--in")
            ?? Path.Combine(config.BaseDirectory, ".parity", "report.json");
        if (!File.Exists(inPath))
            throw new FileNotFoundException($"report not found: {inPath} (run `parity check` first, or pass --in)", inPath);

        ReportDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(inPath), ReportJson.Indented)
                ?? throw new InvalidOperationException($"could not parse report: {inPath}");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"unrecognized report format: {inPath} (likely an older format; re-run `parity check` to regenerate)");
        }
        var reports = doc.Reports
            ?? throw new InvalidOperationException($"report has no `reports` field: {inPath} (re-run `parity check`)");

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
            Console.WriteLine($"markdown report: {full}");
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
            ?? throw new FileNotFoundException("parity.config.json not found (run `parity init` to generate a template).");
        var config = ParityConfig.Load(configPath);

        var tokens = config.TokensFile is { } tf
            ? DesignTokens.LoadJson(Path.Combine(config.BaseDirectory, tf))
            : null;
        if (tokens is null)
            throw new InvalidOperationException(
                "lint requires design tokens: set `tokensFile` in the config (a flat JSON object: {\"token name\": \"value\"}).");

        var targets = opts.GetValueOrDefault("--target") is { } routeFilter
            ? config.Targets.Where(t => t.Route == routeFilter).ToList()
            : config.Targets;
        if (targets.Count == 0)
            throw new InvalidOperationException("the config file declares no targets.");

        Console.WriteLine($"\x1b[1mParity lint\x1b[0m — design-token conformance of the design file (design only, no implementation)\n");

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
                            "the config needs either figmaFileKey or designFile."),
                    NodeId: t.Frame);
                var tree = await source.GetFrameAsync(designRef);
                var nodes = tree.DescendantsAndSelf().Count();
                total += nodes;
                var violations = DesignLint.Run(tree, tokens, config.Tolerances.ColorDeltaE);
                allViolations.AddRange(violations.Select(v => (t.Route, v)));
                Console.WriteLine($"target \x1b[1m{t.Route}\x1b[0m: {nodes} nodes, {violations.Count} violation(s)");
            }

            foreach (var (route, v) in allViolations)
            {
                var near = v.NearestToken is null
                    ? ""
                    : $"; nearest: \x1b[36m{v.NearestToken}\x1b[0m = {v.NearestValue}" +
                      (v.Prop == "color" ? $" (ΔE {v.Distance})" : $" (off by {v.Distance})");
                Console.WriteLine($"  \x1b[33m✘ {v.Layer}\x1b[0m {v.Prop} = {v.Value} is not a token value{near}");
            }

            if (allViolations.Count > 0)
            {
                Console.WriteLine($"\n\x1b[31m✘ {allViolations.Count} violation(s)\x1b[0m ({total} nodes checked)");
                return 1;
            }
            Console.WriteLine($"\n\x1b[32m✔ everything conforms to the token set\x1b[0m ({total} nodes checked)");
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
            ?? throw new FileNotFoundException("parity.config.json not found (run `parity init` to generate a template).");
        var config = ParityConfig.Load(configPath);
        if (config.Targets.Count == 0)
            throw new InvalidOperationException("the config file declares no targets.");

        var targets = opts.GetValueOrDefault("--target") is { } route
            ? config.Targets.Where(t => t.Route == route).ToList()
            : config.Targets;
        if (targets.Count == 0)
            throw new InvalidOperationException("no target matches the requested route.");

        // 快照的視窗大小 = 之後 check 的視窗大小(存進 frame box,check 會照它開視窗)
        var width = int.TryParse(opts.GetValueOrDefault("--width"), out var w) ? w : 1280;
        var height = int.TryParse(opts.GetValueOrDefault("--height"), out var h) ? h : 800;

        Console.WriteLine($"\x1b[1mParity snapshot\x1b[0m — freezing the current rendering as the design baseline ({width}×{height})\n");

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
            Console.WriteLine($"  ✓ {t.Route} → {tree.DescendantsAndSelf().Count()} node(s)");
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
            Console.WriteLine($"previous baseline backed up: {bak} (use it to recover from a bad snapshot)");
        }
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(root, ReportJson.Indented));

        Console.WriteLine($"\nwritten: {outPath}");
        foreach (var s in shotPaths) Console.WriteLine($"reference screenshot: {s}");
        Console.WriteLine($"""

            Next steps (use the snapshot as the design baseline so refactors cannot drift):
              1. In {Path.GetFileName(configPath)} set "designFile": "{Path.GetFileName(outPath)}" (figmaFileKey can be dropped)
              2. Set each target's "frame" to its own route (e.g. "/")
              3. From then on `parity check` verifies the UI still matches the snapshot
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
            Console.Error.WriteLine($"{path} already exists; not overwriting.");
            return 2;
        }
        File.WriteAllText(path, """
            {
              "figmaFileKey": "your Figma file key",
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
        Console.WriteLine($"created {path}. Next:");
        Console.WriteLine("  1. Fill in figmaFileKey and the targets (frame nodeId + URL)");
        Console.WriteLine("  2. Set the FIGMA_TOKEN environment variable");
        Console.WriteLine("  3. parity install-browser (first run only)");
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
            ? "Downloading Chromium and installing system dependencies (for CI; takes a few minutes the first time)…"
            : "Downloading Playwright Chromium (takes a few minutes the first time)…");
        var exitCode = Microsoft.Playwright.Program.Main(pwArgs);
        Console.WriteLine(exitCode == 0 ? "Done." : "Install failed.");
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
              Compare the real numeric values of the design and the implementation; writes a report and sets the exit code.
              --refresh   Ignore the local Figma cache and re-fetch
              --headed    Show the browser window (for debugging)
              --baseline  Regression mode: only gate on diffs that are new or worse than the baseline (see `parity baseline`)
              --reverse   Reverse view: "expected" = the implementation, "actual" = the design file; never gates.
                          (For designers redrawing an existing page: see how the draft differs from what ships.)
              --md <path> Also write a Markdown report (fidelity score + suggested fixes; suitable for a PR comment)
              A target's url may be:
                http(s):// or file://          a normal web page / local file
                cdp:http://host:port           attach to a running Electron desktop app (live window)
                cdp:http://host:port#fragment  with several windows, pick the one whose URL contains the fragment
                (start Electron with --remote-debugging-port=<port>)
        """;

    public const string Report = """
          parity report [--config <path>] [--in <report.json>] [--md <path>]
              Re-render a Markdown report from an existing report.json without re-scanning
              (defaults to .parity/report.json; prints to stdout when --md is omitted).
        """;

    public const string Snapshot = """
          parity snapshot [--config <path>] [--target <route>] [--out <path>] [--width <n>] [--height <n>] [--headed]
              Freeze the currently running implementation into a design baseline (JSON + reference screenshot)
              — a refactor/redesign guard: today's rendering is correct, and later checks prove it has not drifted.
              No Figma required. Overwrites an existing baseline (backed up to .parity/snapshot.bak.json first).
        """;

    public const string Serve = """
          parity serve [--config <path>] [--port <n>] [--watch] [--open]
              Local report UI (binds 127.0.0.1 only): diff list plus a screenshot overlay view.
              --watch     Re-scan automatically when the config, design or page files change
        """;

    public const string Map = """
          parity map [--config <path>] [--port <n>]
              Interactive matching: pick an unmatched design node → click the page element → writes parity.map.json
        """;

    public const string Lint = """
          parity lint [--config <path>] [--target <route>] [--refresh]
              Design lint: looks only at the design file and checks its values against the allowed design-token set
              (color/font size/padding/spacing/corner radius; requires tokensFile).
              For designers keeping a new page inside the design system.
        """;

    public const string Baseline = """
          parity baseline save|list [--config <path>]
              Save or list diff baseline snapshots (SQLite); pair with `check --baseline` for regression gating.
        """;

    public const string Init = """
          parity init             Generate a parity.config.json template
        """;

    public const string InstallBrowser = """
          parity install-browser [--with-deps]
              Download Playwright Chromium (required on first use); --with-deps also installs system
              dependencies (needed on CI Linux runners).
        """;

    /// <summary>子指令 --help:印該指令的用法,exit 0。</summary>
    public static int Print(string usage)
    {
        Console.WriteLine("Usage:");
        Console.WriteLine(usage);
        return 0;
    }
}

internal static class HelpCommand
{
    public static int Run()
    {
        Console.WriteLine("Parity — property-level design fidelity checking\n");
        Console.WriteLine("Usage:");
        foreach (var usage in new[]
        {
            Usage.Check, Usage.Report, Usage.Snapshot, Usage.Serve, Usage.Map,
            Usage.Lint, Usage.Baseline, Usage.Init, Usage.InstallBrowser,
        })
            Console.WriteLine(usage);
        Console.WriteLine("\nExit codes: 0 = pass; 1 = diffs exceed the gate threshold; 2 = execution error; " +
            "3 = match integrity too low (results not trustworthy)");
        Console.WriteLine("Every subcommand accepts --help for its own usage.");
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
