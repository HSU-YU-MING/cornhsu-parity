using System.Text.RegularExpressions;
using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// 凍結面 2(CLI 與 exit code)的守門:直接跑 parity 的進入點,斷言指令分派、用法文字與 exit code。
/// 不需要瀏覽器的路徑全在這裡——「在開瀏覽器之前就該擋下」的錯,刻意把
/// PLAYWRIGHT_BROWSERS_PATH 指到空目錄來證明順序:真的去開瀏覽器的話,錯誤訊息會變成
/// 「Chromium is not installed」,斷言就會紅。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class CliEntryPointTests
{
    // 1.0 凍結的 exit code 契約,原句。改一個字都是對外文件與 CI 腳本的破壞
    private const string ExitCodeContract =
        "Exit codes: 0 = pass; 1 = diffs exceed the gate threshold; 2 = execution error; " +
        "3 = match integrity too low (results not trustworthy)";

    // ---------- 全域 help / 分派 ----------

    [Fact]
    public async Task No_arguments_prints_the_full_help_and_exits_0()
    {
        var r = await Cli.RunAsync();

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(ExitCodeContract, r.Out);
        Assert.Contains("Every subcommand accepts --help for its own usage.", r.Out);
        foreach (var cmd in new[] { "check", "report", "push", "snapshot", "serve", "map", "lint", "baseline", "init", "install-browser" })
            Assert.Contains($"  parity {cmd} ", r.Out);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Help_aliases_print_the_exit_code_contract(string flag)
    {
        var r = await Cli.RunAsync(flag);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(ExitCodeContract, r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public async Task Unknown_command_is_exit_2_and_still_shows_help()
    {
        var r = await Cli.RunAsync("bogus");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unknown command: bogus", r.Err);
        Assert.Contains(ExitCodeContract, r.Out);
    }

    [Theory]
    [InlineData("check", "parity check [--config <path>]")]
    [InlineData("report", "parity report [--config <path>]")]
    [InlineData("push", "parity push --server <url>")]
    [InlineData("snapshot", "parity snapshot [--config <path>]")]
    [InlineData("serve", "parity serve [--config <path>]")]
    [InlineData("map", "parity map [--config <path>]")]
    [InlineData("lint", "parity lint [--config <path>]")]
    [InlineData("baseline", "parity baseline save [--config <path>]")]
    [InlineData("init", "parity init")]
    [InlineData("install-browser", "parity install-browser [--with-deps]")]
    public async Task Every_subcommand_answers_help_without_doing_anything(string command, string usage)
    {
        // `snapshot --help` 曾直接覆寫基準:查用法絕不能觸發執行。空目錄當 cwd,
        // 跑完仍是空的(init 沒寫設定檔、baseline 沒建 db、snapshot 沒落檔)
        using var dir = new TempDir();
        using var cwd = new CwdScope(dir.Path);

        var r = await Cli.RunAsync(command, "--help");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(usage, r.Out);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("push", "--help")]
    public async Task Push_is_marked_experimental_in_the_main_help_and_its_own_help(params string[] args)
    {
        // 2.0.0 的使用者決定:push 子指令與伺服器協定在伺服器公開部署(網頁外殼 M5)之前屬實驗性,
        // 不列入六面凍結——只在說明文字標示,執行時不印警告(CI log 不要變吵)
        var r = await Cli.RunAsync(args);

        Assert.Equal(0, r.ExitCode);
        var lines = r.Out.Split('\n');
        var usage = Array.FindIndex(lines, l => l.Contains("parity push --server <url>"));
        Assert.True(usage >= 0, "找不到 push 的用法行");
        Assert.Contains("(experimental)", lines[usage + 1]);
        Assert.Contains("not covered by the interface freeze", r.Out);
    }

    [Fact]
    public async Task Command_names_are_case_insensitive()
    {
        var r = await Cli.RunAsync("CHECK", "--help");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("parity check [--config <path>]", r.Out);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    public async Task Version_prints_semver_with_an_optional_short_sha(string flag)
    {
        var r = await Cli.RunAsync(flag);

        Assert.Equal(0, r.ExitCode);
        // CHANGELOG 0.14.0 承諾的格式:parity <版號>[+<7 碼 sha>](解析者取 + 之前)
        Assert.Matches(@"^parity \d+\.\d+\.\d+(-[0-9A-Za-z.]+)?(\+[0-9a-f]{7})?\r?\n$", r.Out);
    }

    // ---------- 旗標格式(凍結審查定案:只接受空格式) ----------

    [Fact]
    public async Task Inline_equals_flag_is_an_unknown_argument()
    {
        var r = await Cli.RunAsync("check", "--config=x.json");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unknown argument: \"--config=x.json\"", r.Err);
    }

    [Fact]
    public async Task Install_browser_rejects_unknown_flags_before_downloading_anything()
    {
        var r = await Cli.RunAsync("install-browser", "--bogus");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unknown argument", r.Err);
        Assert.DoesNotContain("Downloading", r.Out);
    }

    // ---------- check:開瀏覽器之前就該擋下的執行錯誤(exit 2) ----------

    private static string WriteConfig(TempDir dir, string json)
    {
        dir.Write("design.json", """{ "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 800, "height": 600 } }""");
        return dir.Write("parity.config.json", json);
    }

    private const string MinimalConfig =
        """{ "designFile": "design.json", "targets": [{ "route": "/", "frame": "", "url": "./page.html" }] }""";

    [Fact]
    public async Task Reverse_and_baseline_together_are_rejected_before_any_browser_starts()
    {
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, MinimalConfig);

        var r = await Cli.RunAsync("check", "--reverse", "--baseline", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("--reverse and --baseline cannot be combined", r.Err);
        Assert.False(Directory.Exists(dir.File(".parity"))); // 連 report.json 都還沒寫
    }

    [Fact]
    public async Task Unknown_target_route_is_exit_2_before_any_browser_starts()
    {
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, MinimalConfig);

        var r = await Cli.RunAsync("check", "--config", config, "--target", "/nope");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("no target with route /nope", r.Err);
        Assert.False(File.Exists(dir.File(".parity", "report.json")));
    }

    [Fact]
    public async Task Missing_design_file_is_exit_2_before_any_browser_starts()
    {
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = dir.Write("parity.config.json",
            """{ "designFile": "missing.json", "targets": [{ "route": "/", "url": "./page.html" }] }""");

        var r = await Cli.RunAsync("check", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("design JSON file not found", r.Err);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("snapshot")]
    [InlineData("lint")]
    [InlineData("serve")]
    [InlineData("baseline", "list")]
    public async Task Missing_config_file_is_exit_2(params string[] command)
    {
        using var dir = new TempDir();
        var r = await Cli.RunAsync([.. command, "--config", dir.File("nope.json")]);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("config file not found", r.Err);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("snapshot")]
    [InlineData("baseline", "save")]
    public async Task Config_without_targets_is_exit_2(params string[] command)
    {
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, """{ "designFile": "design.json", "targets": [] }""");

        var r = await Cli.RunAsync([.. command, "--config", config]);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("the config file declares no targets.", r.Err);
    }

    [Theory]
    [InlineData("check", """{ "designFile": "design.json", "targets": null }""", "\"targets\" is null")]
    [InlineData("check", """{ "designFile": "design.json", "targets": [{ "route": "/" }], "tolerances": null }""", "\"tolerances\" is null")]
    [InlineData("check", """{ "designFile": "design.json", "gate": null, "targets": [] }""", "\"gate\" is null")]
    [InlineData("snapshot", """{ "targets": null }""", "\"targets\" is null")]
    [InlineData("lint", """{ "designFile": "design.json", "tokensFile": "t.json", "targets": [{ "route": "/" }], "tolerances": null }""", "\"tolerances\" is null")]
    public async Task A_null_config_section_is_exit_2_naming_the_field(string command, string json, string expected)
    {
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        dir.Write("t.json", """{ "brand": "#2563EB" }""");
        dir.Write("design.json", """{ "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 10, "height": 10 } }""");
        var config = WriteConfig(dir, json);

        var r = await Cli.RunAsync(command, "--config", config);

        Assert.Equal(2, r.ExitCode); // exit code 不變,只是訊息從 NullReferenceException 變成指名欄位
        Assert.Contains(expected, r.Err);
        Assert.DoesNotContain("Object reference", r.Err);
    }

    [Fact]
    public async Task A_target_with_height_but_no_width_is_no_longer_warned_about()
    {
        // 2.0.0 起只給 height 時照用它(引擎只補缺的寬,見 FidelityEngineTests),所以上一版加的
        // 「height 被忽略」警告也拿掉了
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, """
            { "designFile": "design.json", "targets": [{ "route": "/tall", "url": "./page.html", "height": 2000 }] }
            """);

        var r = await Cli.RunAsync("check", "--config", config);

        Assert.DoesNotContain("height", r.Err);
    }

    [Theory]
    [InlineData("snapshot", "--width", "375px")]
    [InlineData("snapshot", "--height", "tall")]
    [InlineData("snapshot", "--width", "0")]
    [InlineData("snapshot", "--height", "-800")]
    [InlineData("serve", "--port", "http")]
    [InlineData("serve", "--port", "0")]
    [InlineData("map", "--port", "4321x")]
    // 只收十進位數字:正負號與前後空白也是用法錯誤(審查的 M05b:改成 NumberStyles.Integer 時這幾條會放行)
    [InlineData("snapshot", "--width", "+375")]
    [InlineData("snapshot", "--width", " 375")]
    [InlineData("snapshot", "--width", "375 ")]
    [InlineData("snapshot", "--height", "-0")]
    [InlineData("serve", "--port", "+4321")]
    public async Task A_numeric_flag_that_is_not_a_positive_integer_is_exit_2_before_anything_runs(
        string command, string flag, string value)
    {
        // 決策 #5(2.0.0):1.x 非整數時靜默退回預設值(snapshot --width 375px 會以 1280 寬凍結手機基準),
        // 0 與負數則原樣交給瀏覽器。現在都是用法錯誤,而且在讀設定檔、開瀏覽器之前就擋
        // (這裡用沒有 target 的設定檔,證明順序:錯誤訊息不會是「沒有 target」)
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, """{ "designFile": "design.json", "targets": [] }""");

        var r = await Cli.RunAsync(command, "--config", config, flag, value);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains($"{flag} expects a positive integer (got \"{value}\")", r.Err);
        Assert.DoesNotContain("declares no targets", r.Err);
    }

    [Theory]
    [InlineData("serve", "65536")]
    [InlineData("map", "99999")]
    public async Task A_port_above_65535_is_exit_2_before_anything_runs(string command, string value)
    {
        // 1.x 與 2.0.0 第一版:通過驗證,等到 Kestrel bind 時才失敗(ArgumentOutOfRangeException)
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, """{ "designFile": "design.json", "targets": [] }""");

        var r = await Cli.RunAsync(command, "--config", config, "--port", value);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains($"--port expects an integer from 1 to 65535 (got \"{value}\")", r.Err);
        Assert.DoesNotContain("declares no targets", r.Err);
    }

    [Fact]
    public async Task Snapshot_with_an_unknown_target_is_exit_2_and_writes_nothing()
    {
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, MinimalConfig);

        var r = await Cli.RunAsync("snapshot", "--config", config, "--target", "/nope");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("no target matches the requested route.", r.Err);
        Assert.False(File.Exists(dir.File("parity.snapshot.json")));
    }

    [Fact]
    public async Task Figma_source_without_a_token_is_exit_2_with_the_scope_hint()
    {
        using var dir = new TempDir();
        using var env = new EnvScope().Set("FIGMA_TOKEN", null);
        var config = dir.Write("parity.config.json",
            """{ "figmaFileKey": "abc", "targets": [{ "route": "/", "frame": "1:2", "url": "http://localhost/" }] }""");

        var r = await Cli.RunAsync("check", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("missing Figma token", r.Err);
        Assert.Contains("file_content:read", r.Err);
    }

    [Fact]
    public async Task Without_an_installed_browser_check_is_exit_2_and_points_at_install_browser()
    {
        // 新使用者第一次跑 check 最常撞到的錯。這句話也是整個測試套件「沒瀏覽器就略過」的判準
        // (Cli.BrowserMissing 認的就是 install-browser)——訊息一改,真瀏覽器測試會從「略過」變「紅」
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        var config = WriteConfig(dir, MinimalConfig);

        var r = await Cli.RunAsync("check", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("Chromium is not installed yet. Run `parity install-browser` first.", r.Err);
        Assert.True(Cli.BrowserMissing(r));
        Assert.False(File.Exists(dir.File(".parity", "report.json")));
    }

    [Fact]
    public async Task Without_config_flag_or_file_above_cwd_check_explains_init()
    {
        using var dir = new TempDir();
        using var cwd = new CwdScope(dir.Path);
        Assert.Null(ParityConfig.FindConfigFile(dir.Path)); // 前提:暫存目錄往上沒有任何 parity.config.json

        var r = await Cli.RunAsync("check");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("parity.config.json not found", r.Err);
        Assert.Contains("parity init", r.Err);
    }

    // ---------- baseline 子指令分派 ----------

    [Theory]
    [InlineData]
    [InlineData("help")]
    [InlineData("-h")]
    public async Task Baseline_without_a_subcommand_prints_its_usage(params string[] rest)
    {
        var r = await Cli.RunAsync(["baseline", .. rest]);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("parity baseline save [--config <path>] [--target <route>]", r.Out);
        Assert.Contains("parity baseline list [--config <path>]", r.Out);
    }

    [Theory]
    [InlineData("save", "--help")]
    [InlineData("save", "-h")]
    [InlineData("list", "--help")]
    public async Task Baseline_subcommand_help_never_scans_or_writes_the_store(string sub, string flag)
    {
        // 與 `snapshot --help` 曾直接覆寫基準同一類事故:cwd 有一份能跑的設定檔,
        // `baseline save --help` 若沒先看 --help,就會開掃描、存一筆基準。瀏覽器指到空目錄,
        // 真的去掃的話會變成 exit 2 而不是 0
        using var dir = new TempDir();
        using var noBrowsers = new TempDir();
        using var env = new EnvScope().Set("PLAYWRIGHT_BROWSERS_PATH", noBrowsers.Path);
        WriteConfig(dir, MinimalConfig);
        using var cwd = new CwdScope(dir.Path);

        var r = await Cli.RunAsync("baseline", sub, flag);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("parity baseline save [--config <path>] [--target <route>] [--headed]", r.Out);
        Assert.False(File.Exists(dir.File("parity.baseline.db")));
    }

    [Fact]
    public async Task Unknown_baseline_subcommand_is_exit_2()
    {
        var r = await Cli.RunAsync("baseline", "foo");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unknown `baseline` subcommand: \"foo\"", r.Err);
        Assert.Contains("parity baseline save", r.Out); // 順手給用法
    }

    [Fact]
    public async Task Baseline_list_rejects_unknown_flags()
    {
        var r = await Cli.RunAsync("baseline", "list", "--bogus");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unknown argument", r.Err);
    }

    [Fact]
    public async Task Baseline_list_on_an_empty_store_is_exit_0()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir, MinimalConfig);

        var r = await Cli.RunAsync("baseline", "list", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("No baseline yet. Run `parity baseline save` first.", r.Out);
        // 唯讀的查詢不留檔:原本會建出一個 32 KB 的空 db,順手 commit 之後 CI 的 --baseline
        // 就一直走「no baseline yet」(退回一般 gate),而且沒有任何提示說它是空的
        Assert.False(File.Exists(dir.File("parity.baseline.db")));
    }

    [Fact]
    public async Task Baseline_list_shows_newest_first_with_score_and_short_commit()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir,
            """{ "designFile": "design.json", "baselineFile": "ci/custom.db", "targets": [{ "route": "/", "url": "./p.html" }] }""");
        var dbPath = dir.File("ci", "custom.db"); // baselineFile 相對於設定檔目錄
        await using (var store = new Parity.Storage.BaselineStore(dbPath))
        {
            await store.SaveAsync([], new DateTime(2026, 7, 1), commit: "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", score: 70);
            await store.SaveAsync(
                [new Parity.Engine.DiffRecord("/", "btn", "#b", "color", Parity.Engine.Severity.Serious, "e", "a")],
                new DateTime(2026, 7, 2), commit: "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", score: 85);
        }

        var r = await Cli.RunAsync("baseline", "list", "--config", config);

        Assert.Equal(0, r.ExitCode);
        var rows = r.Out.Split('\n').Where(l => l.TrimStart().StartsWith('#')).ToList();
        Assert.Equal(2, rows.Count);
        // 時間的分隔符隨 culture(HH:mm 的 ':' 是 culture 的時間分隔字元),只認數字
        Assert.Matches(@"#2\s+2026-07-02 \d\d\D\d\d\s+1 diffs\s+85/100\s+@ 2222222\s*$", rows[0]); // 新到舊
        Assert.Matches(@"#1\s+2026-07-01 \d\d\D\d\d\s+0 diffs\s+70/100\s+@ 1111111\s*$", rows[1]);
        Assert.DoesNotContain("2222222b", r.Out); // commit 截成 7 碼
    }

    // ---------- init ----------

    [Fact]
    public async Task Init_writes_the_template_once_and_never_overwrites()
    {
        using var dir = new TempDir();
        using var cwd = new CwdScope(dir.Path);
        var path = dir.File("parity.config.json");

        var first = await Cli.RunAsync("init");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(InitCommand.Template, File.ReadAllText(path));
        Assert.Contains("created parity.config.json", first.Out);

        File.WriteAllText(path, "{ /* user edits */ }");
        var second = await Cli.RunAsync("init");
        Assert.Equal(2, second.ExitCode);
        Assert.Contains("already exists; not overwriting", second.Err);
        Assert.Equal("{ /* user edits */ }", File.ReadAllText(path));
    }

    [Fact]
    public async Task Init_rejects_positional_arguments_and_writes_nothing()
    {
        using var dir = new TempDir();
        using var cwd = new CwdScope(dir.Path);

        var r = await Cli.RunAsync("init", "foo");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("unexpected argument: \"foo\"", r.Err);
        Assert.False(File.Exists(dir.File("parity.config.json")));
        Assert.Throws<CliUsageException>(() => InitCommand.Run(["foo"]));
    }
}

/// <summary>
/// 用法文字與解析器的同步:每個指令 --help(與主 help)印出的旗標集合,必須等於該指令 CliOptions 的 spec
/// ——值旗標寫成 `--x <…>`、布林旗標寫成 `--x`。2026-10-22 稽核時三處漂開:push 沒列 `--token`、
/// `baseline save --help` 沒列 `--target`/`--headed`、`map --help` 沒列 `--watch`/`--open`。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class UsageMatchesParserTests
{
    private static string[] Spec(string name) => name switch
    {
        "check" => Usage.Flags.Check,
        "report" => Usage.Flags.Report,
        "push" => Usage.Flags.Push,
        "snapshot" => Usage.Flags.Snapshot,
        "serve" => Usage.Flags.Serve,
        "lint" => Usage.Flags.Lint,
        "baseline save" => Usage.Flags.BaselineSave,
        "baseline list" => Usage.Flags.BaselineList,
        "init" => Usage.Flags.Init,
        "install-browser" => Usage.Flags.InstallBrowser,
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("check --help", "parity check", "check")]
    [InlineData("report --help", "parity report", "report")]
    [InlineData("push --help", "parity push", "push")]
    [InlineData("snapshot --help", "parity snapshot", "snapshot")]
    [InlineData("serve --help", "parity serve", "serve")]
    [InlineData("map --help", "parity map", "serve")] // map 與 serve 同一份實作、同一個 spec
    [InlineData("lint --help", "parity lint", "lint")]
    [InlineData("baseline save --help", "parity baseline save", "baseline save")]
    [InlineData("baseline list --help", "parity baseline list", "baseline list")]
    [InlineData("baseline", "parity baseline save", "baseline save")]
    [InlineData("baseline", "parity baseline list", "baseline list")]
    [InlineData("init --help", "parity init", "init")]
    [InlineData("install-browser --help", "parity install-browser", "install-browser")]
    [InlineData("--help", "parity check", "check")]
    [InlineData("--help", "parity push", "push")]
    [InlineData("--help", "parity map", "serve")]
    [InlineData("--help", "parity baseline save", "baseline save")]
    [InlineData("--help", "parity baseline list", "baseline list")]
    public async Task Help_lists_exactly_the_flags_the_parser_accepts(string args, string synopsis, string spec)
    {
        var r = await Cli.RunAsync(args.Split(' '));
        Assert.Equal(0, r.ExitCode);

        var line = r.Out.Split('\n').Select(l => l.Trim())
            .SingleOrDefault(l => l == synopsis || l.StartsWith(synopsis + " ", StringComparison.Ordinal));
        Assert.True(line is not null, $"`{args}` 的輸出裡找不到以 `{synopsis}` 開頭的用法行");

        var listed = Regex.Matches(line!, @"--(?<name>[a-z-]+)(?<value> <[^>]+>)?")
            .Select(m => "--" + m.Groups["name"].Value + (m.Groups["value"].Success ? "=" : ""))
            .Order(StringComparer.Ordinal);
        Assert.Equal(Spec(spec).Order(StringComparer.Ordinal), listed);
    }
}
