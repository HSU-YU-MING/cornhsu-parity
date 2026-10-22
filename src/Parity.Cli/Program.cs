using System.Reflection;
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
        "push" => await PushCommand.RunAsync(rest),
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
        var opts = CliOptions.Parse(args, Usage.Flags.Check);
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
            Console.WriteLine($"target \x1b[1m{ConsoleText.Safe(scan.Target.Route)}\x1b[0m → {ConsoleText.Safe(report.Url)}" +
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
            Console.WriteLine($"\n\x1b[31m✘ GATE FAIL\x1b[0m (fail on: {string.Join(", ", session.Config.Gate.FailOn)})");
            foreach (var r in gateReasons)
                Console.WriteLine($"  \x1b[31m·\x1b[0m {r}");
            return 1;
        }
        Console.WriteLine($"\n\x1b[32m✔ PASS\x1b[0m (fail on: {string.Join(", ", session.Config.Gate.FailOn)})");
        return 0;
    }

    /// <summary>--md &lt;path&gt;:把報告輸出成 Markdown(可分享 / 貼 PR 留言);有設定 tokensFile 就帶進 token 提示。</summary>
    private static void WriteMarkdown(
        Dictionary<string, string?> opts, ParityConfig config, IReadOnlyList<FidelityReport> reports,
        bool gateFail, BaselineComparison? baseline = null, IReadOnlyList<string>? gateNotes = null,
        int? baselineScore = null)
    {
        if (opts.GetValueOrDefault("--md") is not { } mdPath) return;
        var tokens = TokensFile.LoadForHints(config);
        var full = Path.GetFullPath(mdPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, MarkdownReport.Render(
            reports, gateFail, baseline, tokens, gateNotes,
            figmaFileKey: config.FigmaLinkFileKey, baselineScore: baselineScore));
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
        // db 不存在 = 還沒有 baseline。不開 BaselineStore(建構子會 migrate、建出空 db 留在 repo 裡)
        var dbPath = BaselineCommand.BaselineDbPath(session.Config);
        LatestBaseline? baseline = null;
        if (File.Exists(dbPath))
        {
            await using var store = new BaselineStore(dbPath);
            baseline = await store.GetLatestAsync();
        }

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
            Console.WriteLine($"  \x1b[31m+ new\x1b[0m      {ConsoleText.Safe(d.Route)} ‹{ConsoleText.Safe(d.DesignLayer)}› {d.Prop} [{d.Severity.ToString().ToLowerInvariant()}]");
        foreach (var d in cmp.Worsened)
            Console.WriteLine($"  \x1b[33m↑ worsened\x1b[0m {ConsoleText.Safe(d.Route)} ‹{ConsoleText.Safe(d.DesignLayer)}› {d.Prop} [{d.Severity.ToString().ToLowerInvariant()}]");
        foreach (var d in cmp.Fixed)
            Console.WriteLine($"  \x1b[32m- fixed\x1b[0m    {ConsoleText.Safe(d.Route)} ‹{ConsoleText.Safe(d.DesignLayer)}› {d.Prop}");

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

    internal static void PrintReport(FidelityReport report)
    {
        var s = report.Summary;
        Console.WriteLine($"  matched {s.Matched}/{s.DesignNodes} design nodes; {s.NodesWithDiffs} with diffs");

        foreach (var node in Impact.Order(report.Nodes.Where(n => n.Diffs.Count > 0)))
        {
            // 圖層名、selector、設計值都來自 repo 內容:帶換行時跳脫成字面,不讓它在 CI 裡變成 workflow command
            Console.WriteLine($"  \x1b[33m✘ {ConsoleText.Safe(node.DesignLayer)}\x1b[0m ‹{ConsoleText.Safe(node.Selector)}› " +
                $"[{node.Severity.ToString().ToLowerInvariant()}] ({ConsoleText.Safe(node.MatchedBy)})");
            foreach (var diff in node.Diffs)
            {
                var isColor = diff.Prop is "color" or "background";
                var expected = isColor ? diff.Expected : $"{diff.Expected}{diff.Unit}";
                var actual = isColor
                    ? $"{diff.Actual}{(diff.Delta is { } de ? $" (ΔE {de})" : "")}"
                    : $"{diff.Actual}{diff.Unit}";
                var soft = diff.Soft ? " [soft]" : "";
                Console.WriteLine($"      {diff.Prop,-14} expected {ConsoleText.Safe(expected)}  actual {ConsoleText.Safe(actual)}{soft}");
            }
        }

        if (report.Unmatched.Count > 0)
        {
            Console.WriteLine($"  \x1b[90munmatched: {string.Join(", ", report.Unmatched.Select(u => $"{ConsoleText.Safe(u.DesignLayer)} ({ConsoleText.Safe(u.Reason)})"))}\x1b[0m");
            if (report.Unmatched.Any(u => u.Reason == "randomized-id"))
                Console.WriteLine("  \x1b[90mhint: randomized-id = the baseline froze a selector containing a randomly generated id " +
                    "(React useId, CSS-in-JS, …). Newer baselines use structural paths that survive id regeneration — " +
                    "re-run `parity snapshot` once to migrate.\x1b[0m");
        }
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
        var opts = CliOptions.Parse(args, Usage.Flags.Report);
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
        // 比自己新的報告:照讀(1.x 一直如此;改成拒收是待決定的 semver 問題),但新版加的欄位會被丟掉,
        // 重生的 Markdown 可能不完整——講出來。stderr:stdout 是要接管線的 Markdown
        if (doc.SchemaVersion > ReportDocument.CurrentSchemaVersion)
            Console.Error.WriteLine($"\x1b[33mwarning: {inPath} has report schemaVersion {doc.SchemaVersion}, newer than " +
                $"this Parity understands ({ReportDocument.CurrentSchemaVersion}); fields added by the newer version are " +
                "ignored, so the Markdown may be incomplete. Upgrade Parity to render it faithfully.\x1b[0m");

        var tokens = TokensFile.LoadForHints(config);
        var md = MarkdownReport.Render(
            reports,
            gateFail: config.GateFailReasons(reports).Count > 0,
            tokens: tokens,
            gateNotes: config.MatchIntegrityFailures(reports),
            figmaFileKey: config.FigmaLinkFileKey); // 與 check --md 同一條:圖層名連回 Figma 節點

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

/// <summary>
/// config 的 tokensFile。「沒設定」「檔案不存在」「解析失敗」「沒有 token」各說各的——
/// 原本全被吞成「沒有 token」,lint 接著叫人去設一個已經設了的 tokensFile。
/// </summary>
internal static class TokensFile
{
    /// <summary>lint:token 是必需的,任何問題都是錯(exit 2)。</summary>
    public static DesignTokens LoadForLint(ParityConfig config)
    {
        if (config.TokensFile is not { } tf)
            throw new InvalidOperationException(
                "lint requires design tokens: set `tokensFile` in the config (a flat JSON object: {\"token name\": \"value\"}).");
        var path = Path.Combine(config.BaseDirectory, tf);
        var tokens = DesignTokens.Load(path);
        if (tokens.IsEmpty)
            throw new InvalidOperationException($"tokensFile {path} defines no tokens — lint needs at least one color or size token.");
        return tokens;
    }

    /// <summary>
    /// check --md / report:token 只是讓建議修法多一句「用哪個 token」,壞了不擋報告(exit code 不變),
    /// 但要在 stderr 講出來,不再靜靜少掉提示。
    /// </summary>
    public static DesignTokens? LoadForHints(ParityConfig config)
    {
        if (config.TokensFile is not { } tf) return null;
        try
        {
            var tokens = DesignTokens.Load(Path.Combine(config.BaseDirectory, tf));
            return tokens.IsEmpty ? null : tokens;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"\x1b[33mwarning: {ex.Message} — suggested fixes will not name design tokens.\x1b[0m");
            return null;
        }
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
        var opts = CliOptions.Parse(args, Usage.Flags.Lint);
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Lint);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("parity.config.json not found (run `parity init` to generate a template).");
        var config = ParityConfig.Load(configPath);

        var tokens = TokensFile.LoadForLint(config);

        if (config.Targets.Count == 0)
            throw new InvalidOperationException("the config file declares no targets.");
        var routeFilter = opts.GetValueOrDefault("--target");
        var targets = routeFilter is null
            ? config.Targets
            : config.Targets.Where(t => t.Route == routeFilter).ToList();
        if (targets.Count == 0) // 與 check 同一句(ScanSession.RunAsync)
            throw new InvalidOperationException($"no target with route {routeFilter}.");

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
                Console.WriteLine($"target \x1b[1m{ConsoleText.Safe(t.Route)}\x1b[0m: {nodes} nodes, {violations.Count} violation(s)");
            }

            foreach (var (route, v) in allViolations)
            {
                // 圖層名、設計值、token 名稱都來自 repo 內容(見 ConsoleText)
                var near = v.NearestToken is null
                    ? ""
                    : $"; nearest: \x1b[36m{ConsoleText.Safe(v.NearestToken)}\x1b[0m = {ConsoleText.Safe(v.NearestValue)}" +
                      (v.Prop == "color" ? $" (ΔE {v.Distance})" : $" (off by {v.Distance})");
                Console.WriteLine($"  \x1b[33m✘ {ConsoleText.Safe(v.Layer)}\x1b[0m {v.Prop} = {ConsoleText.Safe(v.Value)} is not a token value{near}");
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
        var opts = CliOptions.Parse(args, Usage.Flags.Snapshot);
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Snapshot);
        var stabilize = opts.ContainsKey("--stabilize");
        // 快照的視窗大小 = 之後 check 的視窗大小(存進 frame box,check 會照它開視窗)
        var width = CliOptions.PositiveIntOrDefault(opts, "--width", 1280);
        var height = CliOptions.PositiveIntOrDefault(opts, "--height", 800);
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

        Console.WriteLine($"\x1b[1mParity snapshot\x1b[0m — freezing the current rendering as the design baseline ({width}×{height})\n");

        await using var impl = new Parity.Engine.ImplementationSources.Web.WebImplementationSource(
            new Parity.Engine.ImplementationSources.Web.WebCaptureOptions(
                Headless: !opts.ContainsKey("--headed"), CaptureScreenshot: true));

        var outPath = Path.GetFullPath(opts.GetValueOrDefault("--out")
            ?? Path.Combine(config.BaseDirectory, "parity.snapshot.json"));
        var frames = new List<DesignNode>();
        var shotPaths = new List<string>();

        var unstableBySelector = new List<string>(); // 建議 ignore 用(跨 target 彙整,保序去重)
        var stableIdUnion = new HashSet<string>(StringComparer.Ordinal); // 全部 target 的穩定 id 聯集
        var anyRandomIds = false; // 任一 target 測到隨機 id → 白名單才需要存進快照
        foreach (var (t, i) in targets.Select((t, i) => (t, i)))
        {
            var url = ScanSession.ResolveUrl(t.Url, config.BaseDirectory);
            var implRef = new ImplRef(url, t.Width ?? width, t.Height ?? height)
            {
                IgnoreSelectors = config.Ignore,
            };
            var tree = await impl.CaptureAsync(implRef);

            // 連拍實測隨機 id(F2 根治):第二次載入,只出現在單邊的 id = 每次載入重新生成。
            // 字元啟發法(looksRandom)在擷取時已擋掉數字混雜型;純字母型(rkxvdnnzty)
            // 只有實測分得出——測到就帶「穩定 id 白名單」重拍,錨點只用兩次都在的 id,
            // 白名單存進快照,check 端用同一份名單擷取,兩邊 selector 生成規則才一致。
            // 探測擷取不拍截圖(CaptureScreenshot = false):截圖字典後拍覆寫先拍,
            // 探測照拍的話「凍結的樹」與「參考截圖」會來自不同次載入,疊框視圖對不齊。
            var reload = await impl.CaptureAsync(implRef with { CaptureScreenshot = false });
            var (stableIds, unstableIdCount) = SnapshotBuilder.ProbeStableIds(tree, reload);
            stableIdUnion.UnionWith(stableIds);
            if (unstableIdCount > 0)
            {
                anyRandomIds = true;
                implRef = implRef with { AllowedIdAnchors = stableIds };
                tree = await impl.CaptureAsync(implRef); // 這拍會被凍結,照拍截圖(覆寫第 1 拍的)
                Console.WriteLine($"  \x1b[33m⚠ {ConsoleText.Safe(t.Route)}: {unstableIdCount} id string(s) appeared in only " +
                    "one of two page loads (per-load random ids) — selectors anchor on the measured " +
                    "stable-id allowlist instead; the allowlist is stored in the snapshot so " +
                    "`parity check` anchors the same way.\x1b[0m");
            }

            // --stabilize:連拍三次,列出「會動」的區域(廣告輪播/動畫/lazy 媒體),
            // 給可貼進 config 的 ignore 建議——與其之後 check 踩到落差才回頭猜,不如現在就講。
            if (stabilize)
            {
                var captures = new List<Parity.Engine.ImplementationSources.RenderedNode> { tree };
                for (var probe = 1; probe < 3; probe++) // 連拍也是探測,不拍截圖(理由同上)
                    captures.Add(await impl.CaptureAsync(implRef with { CaptureScreenshot = false }));
                var unstable = SnapshotStabilizer.FindUnstable(captures);
                if (unstable.Count > 0)
                {
                    Console.WriteLine($"  \x1b[33m⚠ {ConsoleText.Safe(t.Route)}: {unstable.Count} region(s) changed across 3 captures — " +
                        "the baseline freezes whichever state capture #1 happened to catch:\x1b[0m");
                    foreach (var u in unstable)
                        Console.WriteLine($"      {ConsoleText.Safe(u.Selector)}  ({ConsoleText.Safe(u.Reason)})");
                    foreach (var sel in unstable.Select(u => SnapshotStabilizer.ToIgnoreSelector(u.Selector)))
                        if (!unstableBySelector.Contains(sel))
                            unstableBySelector.Add(sel);
                }
                else
                {
                    Console.WriteLine($"  \x1b[32m✓ {ConsoleText.Safe(t.Route)}: stable across 3 captures\x1b[0m");
                }
            }
            frames.Add(SnapshotBuilder.ToFrame(tree, t.Route, t.Width ?? width, t.Height ?? height));

            if (impl.Screenshots.TryGetValue(url, out var png))
            {
                var shot = targets.Count == 1
                    ? Path.ChangeExtension(outPath, ".png")
                    : Path.ChangeExtension(outPath, $".{i}.png");
                // --out 可以指到還不存在的目錄(與 check 的 --out 一致);擷取成功才建,失敗不留空目錄
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await File.WriteAllBytesAsync(shot, png);
                shotPaths.Add(shot);
            }
            Console.WriteLine($"  ✓ {ConsoleText.Safe(t.Route)} → {tree.DescendantsAndSelf().Count()} node(s)");
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
        // 白名單只在真的測到隨機 id 時才存(anyRandomIds):沒有隨機 id 的站,快照與 1.0 逐位元同形
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, ReportJson.SerializeSnapshotTree(
            root, anyRandomIds ? stableIdUnion.Order(StringComparer.Ordinal).ToList() : null));

        Console.WriteLine($"\nwritten: {outPath}");
        foreach (var s in shotPaths) Console.WriteLine($"reference screenshot: {s}");

        if (unstableBySelector.Count > 0)
        {
            // JsonSerializer 逐項序列化:selector 可能含 CSS.escape 產生的反斜線("#\\33 abc"),
            // 手拼字串會給出貼了就爆的 JSON
            Console.WriteLine("\n\x1b[33msuggested \"ignore\" for parity.config.json (unstable regions; " +
                "add them, then re-run parity snapshot):\x1b[0m");
            Console.WriteLine("  \"ignore\": [" + string.Join(", ",
                unstableBySelector.Select(s => JsonSerializer.Serialize(s))) + "]");
        }
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
    /// <summary>
    /// 範本刻意用註解並列兩條路(設定檔解析吃註解):snapshot 模式不需要 Figma,
    /// 是「五秒內第一次綠燈」的最快路徑——只給 Figma 欄位會在第一分鐘勸退沒有
    /// Figma 檔的人(DX 實查 D1)。
    /// </summary>
    internal const string Template = """
        {
          // Design source — pick ONE of the two:
          // (a) Figma (needs a file + a FIGMA_TOKEN with the file_content:read scope)
          "figmaFileKey": "your Figma file key",
          "designToken": "env:FIGMA_TOKEN",
          // (b) No Figma? Freeze today's rendering as the baseline instead (refactor guard):
          //     delete the two lines above, uncomment the next line, set each target's
          //     "frame" to its route (e.g. "/"), then run `parity snapshot` once.
          // "designFile": "parity.snapshot.json",

          "mapFile": "parity.map.json",
          "targets": [
            { "route": "/", "frame": "10:2", "url": "http://localhost:8080/" }
          ],
          "compare": { "position": "relative" },
          "tolerances": { "sizePx": 2, "spacingPx": 2, "colorDeltaE": 2.0 },
          "ignore": ["[data-parity-ignore]"],
          "gate": { "failOn": ["critical", "serious"] }
        }
        """;

    public static int Run(string[] args)
    {
        var opts = CliOptions.Parse(args, Usage.Flags.Init);
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Init);
        const string path = "parity.config.json";
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} already exists; not overwriting.");
            return 2;
        }
        File.WriteAllText(path, Template);
        Console.WriteLine($"created {path}. Next:");
        Console.WriteLine("  With a Figma file:");
        Console.WriteLine("    1. Fill in figmaFileKey and the targets (frame nodeId + URL)");
        Console.WriteLine("    2. Set the FIGMA_TOKEN environment variable");
        Console.WriteLine("  No Figma? Use snapshot mode (freeze today's rendering as the baseline):");
        Console.WriteLine("    1. In the config: switch to \"designFile\" (see the comments), point the");
        Console.WriteLine("       target's url at your page, set its \"frame\" to the route (e.g. \"/\")");
        Console.WriteLine("    2. parity snapshot");
        Console.WriteLine("  Then (either path):");
        Console.WriteLine("    parity install-browser   (first run only)");
        Console.WriteLine("    parity check");
        return 0;
    }
}

internal static class InstallBrowserCommand
{
    public static int Run(string[] args)
    {
        var opts = CliOptions.Parse(args, Usage.Flags.InstallBrowser);
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
    /// <summary>
    /// 各指令 CliOptions 的 spec(「--x=」吃值、「--x」布林)。與下面的用法文字放在一起:
    /// UsageMatchesParserTests 要求每段用法列出的旗標集合等於這裡——加旗標忘了寫用法、
    /// 或用法寫了解析器不收的旗標,測試都會紅。
    /// </summary>
    public static class Flags
    {
        public static readonly string[] Check =
            ["--config=", "--target=", "--out=", "--md=", "--refresh", "--headed", "--baseline", "--reverse"];
        public static readonly string[] Report = ["--config=", "--in=", "--md="];
        public static readonly string[] Push =
            ["--config=", "--in=", "--server=", "--token=", "--commit=", "--branch=", "--gate-exit="];
        public static readonly string[] Snapshot =
            ["--config=", "--target=", "--out=", "--width=", "--height=", "--headed", "--stabilize"];
        /// <summary>serve 與 map 共用(同一份實作)。</summary>
        public static readonly string[] Serve = ["--config=", "--port=", "--watch", "--open"];
        public static readonly string[] Lint = ["--config=", "--target=", "--refresh"];
        public static readonly string[] BaselineSave = ["--config=", "--target=", "--headed"];
        public static readonly string[] BaselineList = ["--config="];
        public static readonly string[] Init = [];
        public static readonly string[] InstallBrowser = ["--with-deps"];
    }

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

    public const string Push = """
          parity push --server <url> [--config <path>] [--in <report.json>] [--token <token>] [--commit <sha>] [--branch <name>] [--gate-exit <code>]
              Send an existing report.json to a Parity.Server dashboard (no re-scan; the server never
              runs a browser — scanning always happens where `parity check` ran).
              Token comes from the PARITY_TOKEN environment variable (a project token from the server);
              --token is for local trials only — flag values end up in shell history and CI logs.
              --server may also come from PARITY_SERVER. Commit/branch default to the GitHub Actions
              environment when present (GITHUB_SHA; GITHUB_HEAD_REF on pull requests, else GITHUB_REF_NAME).
              --gate-exit <code>  Pass `parity check`'s real exit code so the dashboard shows the same
                                  verdict as CI (covers custom failOn and --baseline semantics);
                                  omitted → recomputed from the config's gate settings.
              Reports over 1 MB are sent with Expect: 100-continue so an oversized report gets a clean 413:
              the upload waits up to 15 s for the server's go-ahead, and is resent without it on a 417.
              Each attempt times out after 60 s, so a push can take up to about 2 minutes when it is resent.
        """;

    public const string Snapshot = """
          parity snapshot [--config <path>] [--target <route>] [--out <path>] [--width <n>] [--height <n>] [--headed] [--stabilize]
              Freeze the currently running implementation into a design baseline (JSON + reference screenshot)
              — a refactor/redesign guard: today's rendering is correct, and later checks prove it has not drifted.
              No Figma required. Overwrites an existing baseline (backed up to .parity/snapshot.bak.json first).
              --stabilize   Capture 3 times and report regions that changed between captures (rotating ads,
                            animations, lazy media) with a ready-to-paste "ignore" suggestion — freeze only
                            what is actually stable.
        """;

    public const string Serve = """
          parity serve [--config <path>] [--port <n>] [--watch] [--open]
              Local report UI (binds 127.0.0.1 only): diff list plus a screenshot overlay view.
              --watch     Re-scan automatically when the config, design or page files change
        """;

    public const string Map = """
          parity map [--config <path>] [--port <n>] [--watch] [--open]
              Interactive matching: pick an unmatched design node → click the page element → writes parity.map.json
              Always opens the browser (--open is accepted for symmetry with serve); --watch as in serve.
        """;

    public const string Lint = """
          parity lint [--config <path>] [--target <route>] [--refresh]
              Design lint: looks only at the design file and checks its values against the allowed design-token set
              (color/font size/padding/spacing/corner radius; requires tokensFile).
              For designers keeping a new page inside the design system.
        """;

    public const string Baseline = """
          parity baseline save [--config <path>] [--target <route>] [--headed]
          parity baseline list [--config <path>]
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
            Usage.Check, Usage.Report, Usage.Push, Usage.Snapshot, Usage.Serve, Usage.Map,
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
        Console.WriteLine($"parity {Describe()}");
        return 0;
    }

    /// <summary>
    /// 版號取自 InformationalVersion,不是 AssemblyVersion。
    /// AssemblyVersion 只有三段數字,`0.0.0-dev` 這種本機建置會被截成 `0.0.0`,
    /// 看不出是哪個 commit;InformationalVersion 帶 SourceLink 補上的 `+<sha>`,
    /// 使用者回報 bug 時貼這一行就足以定位。發行版(tag 推導版號)則印乾淨的 `0.13.1+<sha>`。
    /// </summary>
    internal static string Describe()
    {
        var informational = typeof(VersionCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(VersionCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // SourceLink 接的是 40 字元完整 sha,對人來說太長,截成慣用的 7 碼
        var plus = informational.IndexOf('+');
        if (plus >= 0 && informational.Length - plus - 1 > 7)
        {
            return informational[..(plus + 8)];
        }

        return informational;
    }
}
