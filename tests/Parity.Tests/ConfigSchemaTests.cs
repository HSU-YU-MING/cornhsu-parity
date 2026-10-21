using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// 凍結面 1(parity.config.json schema)。1.0.0 的凍結宣告**包含容差預設值**;演進條款 ③:
/// 預設值調整 = minor + 顯著標示——所以預設值一變,這裡一定要紅,逼人去寫 CHANGELOG。
/// </summary>
public class ConfigSchemaTests
{
    private static ParityConfig LoadJson(string json)
    {
        using var dir = new TempDir();
        return ParityConfig.Load(dir.Write("parity.config.json", json));
    }

    // ---------- 凍結的預設值 ----------

    [Fact]
    public void Tolerance_defaults_are_the_frozen_1_0_values()
    {
        var t = new ToleranceConfig();

        Assert.Equal(2, t.SizePx);
        Assert.Equal(2, t.SpacingPx);
        Assert.Equal(2.0, t.ColorDeltaE);
        Assert.Equal(0.5, t.FontSizePx);
        Assert.Equal(4, t.PositionPx);
    }

    [Fact]
    public void Engine_tolerance_defaults_are_the_same_values_as_the_config_defaults()
    {
        // 引擎(ScanRequest.cs)與 config(ParityConfig.cs)各寫一份預設——兩份漂開時,
        // 「沒寫 tolerances 的 config」與「直接用引擎的外殼」會量出不同結果
        Assert.Equal(new Tolerances(2, 2, 2.0, 0.5, 4), new Tolerances());
        Assert.Equal(new Tolerances(), new ParityConfig().ToEngineTolerances());
    }

    [Fact]
    public void ToEngineTolerances_maps_every_field_to_its_own_slot()
    {
        var config = new ParityConfig
        {
            Tolerances = new ToleranceConfig { SizePx = 1.1, SpacingPx = 2.2, ColorDeltaE = 3.3, FontSizePx = 4.4, PositionPx = 5.5 },
        };

        Assert.Equal(new Tolerances(SizePx: 1.1, SpacingPx: 2.2, ColorDeltaE: 3.3, FontSizePx: 4.4, PositionPx: 5.5),
            config.ToEngineTolerances());
    }

    [Fact]
    public void Gate_compare_and_target_defaults_are_frozen()
    {
        var config = new ParityConfig();

        Assert.Equal(["critical", "serious"], config.Gate.FailOn);
        Assert.Equal(0, config.Gate.MinMatchRate);
        Assert.Equal("relative", config.Compare.Position);
        Assert.True(config.ComparePosition);

        var target = new TargetConfig();
        Assert.Equal("/", target.Route);
        Assert.Equal("", target.Frame);
        Assert.Equal("", target.Url);
        Assert.Null(target.Width);
        Assert.Null(target.Height);
    }

    [Fact]
    public void Empty_config_file_loads_with_the_same_defaults()
    {
        // 屬性初始值之外,另一條路:JSON 什麼都沒寫時反序列化也要落到同一組預設
        var config = LoadJson("{}");

        Assert.Equal(new Tolerances(), config.ToEngineTolerances());
        Assert.Equal(["critical", "serious"], config.Gate.FailOn);
        Assert.Equal("relative", config.Compare.Position);
        Assert.Empty(config.Targets);
        Assert.Empty(config.Ignore);
        Assert.Null(config.DesignFile);
        Assert.Null(config.MapFile);
        Assert.Null(config.BaselineFile);
    }

    [Fact]
    public void Partial_tolerances_keep_the_other_defaults()
    {
        var config = LoadJson("""{ "tolerances": { "sizePx": 7 } }""");

        Assert.Equal(new Tolerances(SizePx: 7), config.ToEngineTolerances());
    }

    [Fact]
    public void Target_without_route_or_frame_gets_the_defaults()
    {
        var t = Assert.Single(LoadJson("""{ "targets": [{ "url": "./a.html", "width": 375 }] }""").Targets);

        Assert.Equal("/", t.Route);
        Assert.Equal("", t.Frame);
        Assert.Equal(375, t.Width);
        Assert.Null(t.Height);
    }

    // ---------- 解析規則 ----------

    [Fact]
    public void Field_names_are_case_insensitive()
    {
        var config = LoadJson("""{ "Tolerances": { "SizePx": 7 }, "GATE": { "FailOn": ["Critical"] }, "DesignFile": "d.json" }""");

        Assert.Equal(7, config.Tolerances.SizePx);
        Assert.Equal(["Critical"], config.Gate.FailOn);
        Assert.Equal("d.json", config.DesignFile);
    }

    [Fact]
    public void Unknown_and_snake_case_fields_are_silently_ignored()
    {
        // 固定現況:拼錯的欄位不報錯、值維持預設(System.Text.Json 預設行為)。
        // 改成「未知欄位報錯」會讓原本載入得了的設定檔變 exit 2 = major;只印警告則是 patch
        var config = LoadJson("""{ "tolerance": { "sizePx": 9 }, "tolerances": { "size_px": 9 }, "bogus": true }""");

        Assert.Equal(2, config.Tolerances.SizePx);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var config = LoadJson("""
            {
              // 註解
              "designFile": "d.json", /* 區塊註解 */
              "ignore": ["#ad",],
            }
            """);

        Assert.Equal("d.json", config.DesignFile);
        Assert.Equal(["#ad"], config.Ignore);
    }

    [Fact]
    public void Base_directory_is_the_config_files_own_directory()
    {
        using var dir = new TempDir();
        var path = dir.Write(Path.Combine("sub", "parity.config.json"), "{}");

        Assert.Equal(dir.File("sub"), ParityConfig.Load(path).BaseDirectory);
    }

    [Fact]
    public void A_config_that_is_just_null_is_a_parse_error_naming_the_file()
    {
        // `null` 是合法 JSON,反序列化不丟例外而是回 null——要接住,不能變成後面的 NullReferenceException
        using var dir = new TempDir();
        var path = dir.Write("parity.config.json", "null");

        var ex = Assert.Throws<InvalidOperationException>(() => ParityConfig.Load(path));
        Assert.Equal($"could not parse config file: {path}", ex.Message);
    }

    [Fact]
    public void Loading_a_missing_file_points_at_init()
    {
        using var dir = new TempDir();

        var ex = Assert.Throws<FileNotFoundException>(() => ParityConfig.Load(dir.File("nope.json")));
        Assert.Contains("config file not found", ex.Message);
        Assert.Contains("parity init", ex.Message);
    }

    // ---------- 載入時驗證 ----------

    [Fact]
    public void Design_image_without_a_design_file_is_rejected_at_load()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson("""{ "designImage": "shot.png" }"""));
        Assert.Contains("designImage requires a companion designFile", ex.Message);
    }

    [Fact]
    public void Design_image_with_a_design_file_loads()
    {
        var config = LoadJson("""{ "designImage": "shot.png", "designFile": "annotations.json" }""");

        Assert.Equal("shot.png", config.DesignImage);
    }

    [Theory]
    [InlineData("relative", true)]
    [InlineData("RELATIVE", true)]
    [InlineData("none", false)]
    [InlineData("None", false)]
    public void Compare_position_accepts_relative_and_none_in_any_case(string value, bool compares)
    {
        var config = LoadJson($$"""{ "compare": { "position": "{{value}}" } }""");

        Assert.Equal(compares, config.ComparePosition);
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("")]
    public void Compare_position_rejects_anything_else(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson($$"""{ "compare": { "position": "{{value}}" } }"""));
        Assert.Contains("compare.position", ex.Message);
        Assert.Contains("relative, none", ex.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Min_match_rate_bounds_are_inclusive(double rate)
    {
        Assert.Equal(rate, LoadJson($$"""{ "gate": { "minMatchRate": {{rate}} } }""").Gate.MinMatchRate);
    }

    [Fact]
    public void Negative_min_match_rate_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson("""{ "gate": { "minMatchRate": -0.1 } }"""));
        Assert.Contains("between 0 and 1", ex.Message);
    }

    [Fact]
    public void Fail_on_lists_the_valid_severities_when_misspelled()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson("""{ "gate": { "failOn": ["moderate"] } }"""));
        Assert.Contains("\"moderate\"", ex.Message);
        Assert.Contains("minor, medium, serious, critical", ex.Message);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("9")]
    [InlineData("serious, critical")] // Enum 旗標語法:3|4 = 7,不是任何嚴重度
    public void Fail_on_values_that_can_never_match_a_diff_load_with_a_warning(string value)
    {
        // 這些值目前照樣載入(收緊成報錯是待決定的 semver 問題),但 gate 會因此靜默失效——
        // 至少要大聲講出來,而不是讓 CI 永遠綠
        var config = LoadJson($$"""{ "gate": { "failOn": ["{{value}}"] } }""");

        var warning = Assert.Single(config.Warnings);
        Assert.Contains($"\"{value}\"", warning);
        Assert.Contains("never matches", warning);
        Assert.Contains("minor, medium, serious, critical", warning);
    }

    [Theory]
    [InlineData("serious, minor", "serious")] // 3|1 = 3:看起來是兩個值,實際只擋 serious
    [InlineData("4", "critical")]
    public void Fail_on_values_that_are_not_severity_names_warn_how_they_are_read(string value, string readAs)
    {
        var config = LoadJson($$"""{ "gate": { "failOn": ["{{value}}"] } }""");

        var warning = Assert.Single(config.Warnings);
        Assert.Contains($"\"{value}\"", warning);
        Assert.Contains($"read as \"{readAs}\"", warning);
    }

    [Fact]
    public void Documented_fail_on_values_load_without_warnings()
    {
        var config = LoadJson("""{ "gate": { "failOn": ["Minor", "medium", "SERIOUS", "critical"] } }""");

        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Fail_on_is_case_insensitive_and_gates_accordingly()
    {
        var config = LoadJson("""{ "gate": { "failOn": ["Critical"] } }""");
        var report = new FidelityReport("/", "u", "d",
            [new NodeResult("a", "a", "s", "selector", Severity.Critical,
                [new PropDiff("width", "10", "90", "px", 80, 2, Severity.Critical)])],
            [], new ReportSummary(1, 1, 0, 1, 1, 0, 0, 0, Severity.Critical));

        Assert.Equal("has Critical severity diffs", config.GateFailReason(report));
    }

    // ---------- 找設定檔 / baseline 路徑 ----------

    [Fact]
    public void Find_config_file_walks_up_and_the_nearest_one_wins()
    {
        using var dir = new TempDir();
        dir.Write("parity.config.json", "{}");
        var nearer = dir.Write(Path.Combine("a", "parity.config.json"), "{}");
        var deep = Directory.CreateDirectory(dir.File("a", "b", "c")).FullName;

        Assert.Equal(nearer, ParityConfig.FindConfigFile(deep));
        Assert.Equal(dir.File("parity.config.json"), ParityConfig.FindConfigFile(dir.File()));
    }

    [Theory]
    [InlineData(null, "parity.baseline.db")]
    [InlineData("ci/base.db", "ci/base.db")]
    public void Baseline_db_lives_next_to_the_config_unless_configured(string? baselineFile, string expectedRelative)
    {
        // 刻意預設在 repo 根而不是 .parity/(那裡通常被 gitignore,CI 會靜默吃不到 baseline)
        var config = new ParityConfig { BaseDirectory = Path.Combine("x", "repo"), BaselineFile = baselineFile };

        Assert.Equal(Path.Combine("x", "repo", expectedRelative), BaselineCommand.BaselineDbPath(config));
    }
}

/// <summary>designToken 的解析會讀環境變數(行程共用狀態),所以單獨放進不平行的集合。</summary>
[Collection(ProcessStateCollection.Name)]
public class ConfigTokenResolutionTests
{
    [Fact]
    public void Env_prefix_reads_the_named_variable_in_any_case()
    {
        var name = $"PARITY_TEST_TOKEN_{Guid.NewGuid():N}";
        using var env = new EnvScope().Set(name, "from-env").Set("FIGMA_TOKEN", "not-this-one");

        Assert.Equal("from-env", new ParityConfig { DesignToken = $"env:{name}" }.ResolveToken());
        Assert.Equal("from-env", new ParityConfig { DesignToken = $"ENV:{name}" }.ResolveToken());
    }

    [Fact]
    public void Env_prefix_with_an_unset_variable_is_null_not_the_literal()
    {
        var name = $"PARITY_TEST_UNSET_{Guid.NewGuid():N}";
        using var env = new EnvScope().Set(name, null).Set("FIGMA_TOKEN", "not-this-one");

        Assert.Null(new ParityConfig { DesignToken = $"env:{name}" }.ResolveToken());
    }

    [Fact]
    public void Anything_else_is_a_literal_token()
    {
        using var env = new EnvScope().Set("FIGMA_TOKEN", "not-this-one");

        Assert.Equal("figd_literal", new ParityConfig { DesignToken = "figd_literal" }.ResolveToken());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_design_token_falls_back_to_FIGMA_TOKEN(string? designToken)
    {
        using var env = new EnvScope().Set("FIGMA_TOKEN", "fallback");

        Assert.Equal("fallback", new ParityConfig { DesignToken = designToken }.ResolveToken());
    }
}
