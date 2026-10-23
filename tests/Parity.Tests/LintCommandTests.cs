using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// parity lint 指令層(DesignLint 的規則本身見 DesignLintTests):不開瀏覽器,
/// 只看設計稿——exit code、tokensFile 的必要性、--target 篩選。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class LintCommandTests
{
    /// <summary>samples/demo 的設計稿與 token 複製進暫存目錄——demo 目錄本身不能被測試寫到。</summary>
    private static string DemoCopy(TempDir dir, string? tokensFile = "tokens.json", string targets = """[{ "route": "/", "frame": "" }]""")
    {
        File.Copy(Repo.PathOf("samples", "demo", "design.json"), dir.File("design.json"));
        File.Copy(Repo.PathOf("samples", "demo", "tokens.json"), dir.File("tokens.json"));
        var tokens = tokensFile is null ? "" : $"\"tokensFile\": \"{tokensFile}\",";
        return dir.Write("parity.config.json", $$"""{ "designFile": "design.json", {{tokens}} "targets": {{targets}} }""");
    }

    [Fact]
    public async Task Demo_design_has_violations_and_exits_1()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);

        var r = await Cli.RunAsync("lint", "--config", config);

        // demo 的設計稿刻意有不在 token 集合裡的值(盤點實跑:21 violations / 13 nodes)
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("target \x1b[1m/\x1b[0m: 13 nodes, 21 violation(s)", r.Out);
        Assert.Contains("21 violation(s)\x1b[0m (13 nodes checked)", r.Out);
        Assert.Contains("is not a token value; nearest:", r.Out);
    }

    [Fact]
    public async Task Fully_conforming_design_exits_0()
    {
        using var dir = new TempDir();
        dir.Write("tokens.json", """{ "primary": "#2563EB", "space-4": "16px", "radius": "8px" }""");
        dir.Write("design.json", """
            { "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 400, "height": 300 },
              "children": [
                { "id": "1", "name": "card", "type": "frame", "box": { "x": 0, "y": 0, "width": 100, "height": 50 },
                  "fill": { "r": 37, "g": 99, "b": 235, "a": 1 },
                  "padding": { "top": 16, "right": 16, "bottom": 16, "left": 16 }, "cornerRadius": 8 }
              ] }
            """);
        var config = dir.Write("parity.config.json",
            """{ "designFile": "design.json", "tokensFile": "tokens.json", "targets": [{ "route": "/" }] }""");

        var r = await Cli.RunAsync("lint", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("everything conforms to the token set", r.Out);
        Assert.Contains("(2 nodes checked)", r.Out);
    }

    [Fact]
    public async Task Target_flag_lints_only_that_route()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir, targets: """[{ "route": "/", "frame": "" }, { "route": "/other", "frame": "" }]""");

        var r = await Cli.RunAsync("lint", "--config", config, "--target", "/other");

        Assert.Contains("target \x1b[1m/other\x1b[0m:", r.Out);
        Assert.DoesNotContain("target \x1b[1m/\x1b[0m:", r.Out);
    }

    [Fact]
    public async Task Missing_tokens_file_setting_is_an_error()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir, tokensFile: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LintCommand.RunAsync(["--config", config]));
        Assert.Contains("lint requires design tokens", ex.Message);
    }

    // 下面三條原本都報同一句「lint requires design tokens: set `tokensFile` in the config」——
    // 叫人去設一個已經設了的欄位。現在「沒設定」「檔案不存在」「解析失敗」「沒有 token」各說各的(exit 2 不變)

    [Fact]
    public async Task Tokens_file_that_does_not_exist_says_so()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir, tokensFile: "nope.json");

        var r = await Cli.RunAsync("lint", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains($"tokensFile not found: {dir.File("nope.json")}", r.Err);
        Assert.DoesNotContain("set `tokensFile`", r.Err);
    }

    [Fact]
    public async Task Tokens_file_that_does_not_parse_says_where()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        File.WriteAllText(dir.File("tokens.json"), """{ "space-2": 8 }""");

        var r = await Cli.RunAsync("lint", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("\"space-2\" is a number", r.Err);
        Assert.DoesNotContain("set `tokensFile`", r.Err);
    }

    [Fact]
    public async Task Tokens_file_without_any_token_says_so()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        File.WriteAllText(dir.File("tokens.json"), "{}");

        var r = await Cli.RunAsync("lint", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("defines no tokens", r.Err);
    }

    [Fact]
    public async Task Tokens_file_with_comments_is_read()
    {
        // 與 parity.config.json / map 檔同一套寬鬆規則(註解、尾逗號)
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        File.WriteAllText(dir.File("tokens.json"), "{\n  // brand\n  \"color-primary\": \"#2563EB\",\n}");

        var r = await Cli.RunAsync("lint", "--config", config);

        Assert.Equal(1, r.ExitCode); // 讀得到 token,demo 設計稿照樣有違規
        Assert.Contains("violation(s)", r.Out);
    }

    [Fact]
    public async Task Unknown_target_route_is_exit_2()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);

        var r = await Cli.RunAsync("lint", "--config", config, "--target", "/nope");

        Assert.Equal(2, r.ExitCode);
        Assert.DoesNotContain("violation", r.Out); // 沒有任何一頁被 lint
        // 與 check 同一句;原本說的是「config 沒有任何 target」,但 config 明明有
        Assert.Contains("no target with route /nope.", r.Err);
        Assert.DoesNotContain("declares no targets", r.Err);
    }

    [Fact]
    public async Task A_config_without_targets_still_says_so()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir, targets: "[]");

        var r = await Cli.RunAsync("lint", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("the config file declares no targets.", r.Err);
    }
}
