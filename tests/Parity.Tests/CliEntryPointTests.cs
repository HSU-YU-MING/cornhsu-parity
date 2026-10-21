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
