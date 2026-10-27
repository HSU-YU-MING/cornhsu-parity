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

    [Theory]
    [InlineData("""{ "gate": null }""", "\"gate\" is null")]
    [InlineData("""{ "gate": { "failOn": null } }""", "\"gate.failOn\" is null")]
    [InlineData("""{ "compare": null }""", "\"compare\" is null")]
    [InlineData("""{ "stats": null }""", "\"stats\" is null")] // 隱私開關:寫成 null 不猜成「不送」或「照送」
    public void A_null_section_that_load_reads_is_an_error_naming_the_field(string json, string expected)
    {
        // 原本載入時直接 NullReferenceException,使用者只看到 "Object reference not set to an instance of an object."
        using var dir = new TempDir();
        var path = dir.Write("parity.config.json", json);

        var ex = Assert.Throws<InvalidOperationException>(() => ParityConfig.Load(path));
        Assert.Contains($"config {path}: {expected}", ex.Message);
    }

    [Fact]
    public void Null_targets_and_tolerances_load_but_name_the_field_when_used()
    {
        // 這兩個區段載入時用不到:report/push/baseline list 這種不碰它們的指令照樣能跑(行為不變),
        // 用到的指令(check/serve/map/snapshot/lint)才報錯——原本是執行期的 NullReferenceException
        using var dir = new TempDir();
        var path = dir.Write("parity.config.json", """{ "targets": null, "tolerances": null }""");
        var config = ParityConfig.Load(path);

        Assert.Contains($"config {path}: \"targets\" is null",
            Assert.Throws<InvalidOperationException>(() => config.Targets).Message);
        Assert.Contains($"config {path}: \"tolerances\" is null",
            Assert.Throws<InvalidOperationException>(() => config.ToEngineTolerances()).Message);
    }

    [Fact]
    public void A_null_entry_in_targets_names_its_index()
    {
        var config = LoadJson("""{ "targets": [{ "route": "/" }, null] }""");

        Assert.Contains("\"targets[1]\" is null", Assert.Throws<InvalidOperationException>(() => config.Targets).Message);
    }

    [Theory]
    [InlineData("""{ "figmaFileKey": "qRzJ3fxF1g09NczsrugiXD" }""", "qRzJ3fxF1g09NczsrugiXD")]
    [InlineData("""{ "figmaFileKey": "qRzJ3fxF1g09NczsrugiXD", "designFile": "parity.snapshot.json" }""", null)]
    [InlineData("""{ "designFile": "design.json" }""", null)]
    public void Figma_links_use_the_file_key_only_when_figma_is_the_design_source(string json, string? expected)
    {
        // designFile 優先於 figmaFileKey(ScanSession);那時 designId 是 selector,不是 Figma 節點
        Assert.Equal(expected, LoadJson(json).FigmaLinkFileKey);
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
    [InlineData("none")]              // 對不到任何落差:gate 永遠不因嚴重度失敗
    [InlineData("9")]                 // 未定義的數字:同上
    [InlineData("serious, critical")] // Enum 旗標語法:3|4 = 7,不是任何嚴重度
    [InlineData("serious, minor")]    // 3|1 = 3:看起來是兩個值,實際只擋 serious
    [InlineData("4")]                 // 數字寫法:1.x 讀成 critical,從沒文件化
    [InlineData(" critical")]         // 前後空白:1.x 的 Enum 解析會吃掉
    public void Fail_on_accepts_only_the_four_severity_names(string value)
    {
        // 2.0.0:白名單。1.x 用 Enum.TryParse,上面這些值都能載入,不是讓 gate 靜默失效就是被讀成別的等級
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson($$"""{ "gate": { "failOn": ["{{value}}"] } }"""));
        Assert.Contains($"\"{value}\"", ex.Message);
        Assert.Contains("minor, medium, serious, critical", ex.Message);
    }

    [Fact]
    public void The_four_severity_names_load_in_any_case()
    {
        var config = LoadJson("""{ "gate": { "failOn": ["Minor", "medium", "SERIOUS", "critical"] } }""");

        Assert.Equal(["Minor", "medium", "SERIOUS", "critical"], config.Gate.FailOn);
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

    // ---------- stats(2.1.0,匿名使用統計;面 1 的加法)----------

    [Fact]
    public void Without_a_stats_section_the_config_has_no_opinion_and_no_project_id()
    {
        var config = LoadJson("{}");

        Assert.Null(config.ShareStats);
        Assert.Null(config.Stats.ProjectId);
        Assert.Null(new ParityConfig().ShareStats);
        Assert.Null(LoadJson("""{ "stats": {} }""").ShareStats);
    }

    [Theory]
    [InlineData("""{ "stats": { "share": false } }""", false)]
    [InlineData("""{ "stats": { "share": true } }""", true)]
    [InlineData("""{ "Stats": { "Share": false } }""", false)] // 欄位名不分大小寫,與整份設定檔一致
    public void Stats_share_takes_true_or_false(string json, bool share)
        => Assert.Equal(share, LoadJson(json).ShareStats);

    [Theory]
    [InlineData("\"false\"")] // 最可能的手誤:寫成字串
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Stats_share_of_any_other_type_is_an_error_naming_the_field(string value)
    {
        using var dir = new TempDir();
        var path = dir.Write("parity.config.json", $$"""{ "stats": { "share": {{value}} } }""");

        var ex = Assert.Throws<InvalidOperationException>(() => ParityConfig.Load(path));
        Assert.Equal($"config {path}: stats.share must be true or false (got: {value}) — " +
            "write \"stats\": { \"share\": false } to turn anonymous usage statistics off.", ex.Message);
    }

    [Theory]
    [InlineData("abcdefghijklmnop")]                                                   // 16 字元(下限)
    [InlineData("A_b-C_d-0123456789")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]  // 64 字元(上限)
    public void A_project_id_in_the_protocols_format_loads(string id)
        => Assert.Equal(id, LoadJson($$"""{ "stats": { "projectId": "{{id}}" } }""").Stats.ProjectId);

    [Theory]
    [InlineData("abcdefghijklmno")]                                                    // 15 字元
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65 字元
    [InlineData("has spaces in it ok")]
    [InlineData("dots.are.not.allowed")]
    [InlineData("ümlaut-ümlaut-ümlaut")]
    [InlineData("")]
    public void A_project_id_in_any_other_format_is_a_config_error(string id)
    {
        // 協定 §7.1:格式不對是設定檔載入錯誤(exit 2),不是安靜丟掉
        using var dir = new TempDir();
        var path = dir.Write("parity.config.json", $$"""{ "stats": { "projectId": "{{id}}" } }""");

        var ex = Assert.Throws<InvalidOperationException>(() => ParityConfig.Load(path));
        Assert.StartsWith($"config {path}: stats.projectId must be 16–64 characters of A-Z, a-z, 0-9, '_' or '-' " +
            $"(got: \"{id}\", {id.Length} characters).", ex.Message);
        Assert.Contains("never derive it from the repository name", ex.Message);
    }

    [Fact]
    public void A_null_project_id_means_none()
        => Assert.Null(LoadJson("""{ "stats": { "projectId": null } }""").Stats.ProjectId);

    [Fact]
    public void Unknown_keys_inside_stats_are_ignored_like_everywhere_else()
    {
        // 與整份設定檔的規則一致(拼錯的欄位靜默忽略;改成報錯是 major)。代價寫在 README:拼錯的 "share" 不會關掉統計,
        // 要確認用 check --stats-preview
        var config = LoadJson("""{ "stats": { "sahre": false, "projectID": "abcdefghijklmnop", "extra": 1 } }""");

        Assert.Null(config.ShareStats);
        Assert.Equal("abcdefghijklmnop", config.Stats.ProjectId); // projectID 只是大小寫不同,照收
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
