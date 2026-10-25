using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// 匿名使用統計(2.1.0)的純函式部分:開關的順序(協定 §3)、分桶(§7.2,左閉右開)、屬性家族的封閉集、
/// 設計來源的判斷、JSON 的形狀。協定的唯一真相源是 stats-collector 的 PROTOCOL.md;
/// <see cref="StatsProtocol"/> 是那份協定 §7 在測試這邊的鏡像(照收集站的驗證規則寫),送出去的東西都要過它。
/// </summary>
public class UsageStatsTests
{
    private const string Release = "2.1.0";

    private static StatsDecision Decide(Dictionary<string, string?> vars, bool? configShare = null,
        string version = Release, bool defaultOn = true)
        => UsageStats.Decide(name => vars.GetValueOrDefault(name), configShare, version, defaultOn);

    // ---------- 開關(協定 §3)----------

    [Fact]
    public void Default_is_on_and_sends_to_the_collector()
    {
        var d = Decide([]);

        Assert.True(d.Send);
        Assert.Equal("https://stats.cornhsu.com/v1/parity", d.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void The_default_is_one_constant_and_it_is_on()
    {
        // 倫理審查若要求主動同意,只改 UsageStats.SendByDefault 這一個值(這條測試會跟著紅,提醒一起改文件)
        Assert.True(UsageStats.SendByDefault);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("anything")]
    [InlineData(" 0")]   // 不是剛好 0:往「不送」那邊錯
    public void Do_not_track_with_any_value_but_0_or_false_turns_it_off(string value)
        => Assert.Equal(StatsOff.DoNotTrack, Decide(new() { ["DO_NOT_TRACK"] = value }).Off);

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("")]
    public void Do_not_track_set_to_0_false_or_empty_does_not_count(string value)
        => Assert.True(Decide(new() { ["DO_NOT_TRACK"] = value }).Send);

    [Fact]
    public void Do_not_track_wins_over_everything()
    {
        var d = Decide(new() { ["DO_NOT_TRACK"] = "1", ["PARITY_SHARE_STATS"] = "true", ["CORNHSU_SHARE_STATS"] = "on" },
            configShare: true);

        Assert.Equal(StatsOff.DoNotTrack, d.Off);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("On")]
    [InlineData("")]     // 空字串 = 沒設
    public void Share_variables_set_to_yes_values_are_neutral(string value)
    {
        Assert.True(Decide(new() { ["CORNHSU_SHARE_STATS"] = value }).Send);
        Assert.True(Decide(new() { ["PARITY_SHARE_STATS"] = value }).Send);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("flase")] // 打錯字:往「不送」那邊錯
    [InlineData("2")]
    [InlineData(" true")]
    public void Any_other_share_variable_value_turns_it_off(string value)
    {
        Assert.Equal(StatsOff.SharedVariable, Decide(new() { ["CORNHSU_SHARE_STATS"] = value }).Off);
        Assert.Equal(StatsOff.ToolVariable, Decide(new() { ["PARITY_SHARE_STATS"] = value }).Off);
    }

    [Fact]
    public void The_shared_variable_is_checked_before_the_tools_own()
        => Assert.Equal(StatsOff.SharedVariable,
            Decide(new() { ["CORNHSU_SHARE_STATS"] = "false", ["PARITY_SHARE_STATS"] = "false" }).Off);

    [Fact]
    public void Config_share_false_turns_it_off_and_an_env_off_beats_config_true()
    {
        Assert.Equal(StatsOff.Config, Decide([], configShare: false).Off);
        Assert.True(Decide([], configShare: true).Send);
        Assert.Equal(StatsOff.ToolVariable, Decide(new() { ["PARITY_SHARE_STATS"] = "false" }, configShare: true).Off);
    }

    [Fact]
    public void A_development_build_never_sends_unless_a_stats_url_is_set()
    {
        Assert.Equal(StatsOff.DevelopmentBuild, Decide([], version: "0.0.0-dev").Off);

        var custom = Decide(new() { ["CORNHSU_STATS_URL"] = "http://127.0.0.1:9/" }, version: "0.0.0-dev");
        Assert.True(custom.Send);
        Assert.Equal("http://127.0.0.1:9/v1/parity", custom.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("not a url")]
    [InlineData("/an/absolute/path")] // Unix 上這是 file: 的絕對 URI
    [InlineData("localhost:8080")]
    [InlineData("")]
    public void An_invalid_stats_url_counts_as_unset(string url)
    {
        Assert.Equal(StatsOff.DevelopmentBuild, Decide(new() { ["CORNHSU_STATS_URL"] = url }, version: "0.0.0-dev").Off);
        Assert.Equal("https://stats.cornhsu.com/v1/parity", Decide(new() { ["CORNHSU_STATS_URL"] = url }).Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000/v1/parity")]
    [InlineData("http://127.0.0.1:5000/", "http://127.0.0.1:5000/v1/parity")]
    [InlineData("https://collector.test/base/", "https://collector.test/base/v1/parity")]
    [InlineData("https://collector.test/base?x=1#f", "https://collector.test/base/v1/parity")]
    public void The_stats_url_replaces_only_the_root(string url, string endpoint)
        => Assert.Equal(endpoint, Decide(new() { ["CORNHSU_STATS_URL"] = url }).Endpoint.AbsoluteUri);

    [Fact]
    public void Switches_still_win_over_a_stats_url()
    {
        var vars = new Dictionary<string, string?> { ["CORNHSU_STATS_URL"] = "http://127.0.0.1:9/" };

        Assert.Equal(StatsOff.DoNotTrack, Decide(new(vars) { ["DO_NOT_TRACK"] = "1" }, version: "0.0.0-dev").Off);
        Assert.Equal(StatsOff.Config, Decide(vars, configShare: false, version: "0.0.0-dev").Off);
    }

    [Fact]
    public void With_an_opt_in_default_someone_has_to_say_yes()
    {
        // 倫理審查若要求主動同意(SendByDefault = false):沒人表態 → 不送;設定檔或環境變數明說要 → 送
        Assert.Equal(StatsOff.NotOptedIn, Decide([], defaultOn: false).Off);
        Assert.True(Decide([], configShare: true, defaultOn: false).Send);
        Assert.True(Decide(new() { ["PARITY_SHARE_STATS"] = "yes" }, defaultOn: false).Send);
        Assert.Equal(StatsOff.DoNotTrack, Decide(new() { ["DO_NOT_TRACK"] = "1" }, configShare: true, defaultOn: false).Off);
    }

    [Theory]
    [InlineData("true", null, true)]
    [InlineData("TRUE", null, true)]
    [InlineData(null, "true", true)]
    [InlineData("1", null, false)]
    [InlineData("false", "false", false)]
    [InlineData(null, null, false)]
    public void Ci_is_CI_or_GITHUB_ACTIONS_set_to_true(string? ci, string? actions, bool expected)
    {
        var vars = new Dictionary<string, string?> { ["CI"] = ci, ["GITHUB_ACTIONS"] = actions };

        Assert.Equal(expected, UsageStats.IsCi(name => vars.GetValueOrDefault(name)));
    }

    // ---------- 分桶(協定 §7.2,左閉右開)----------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.99, 0)]
    [InlineData(1, 1)]      // 剛好等於邊界:進右邊那一桶
    [InlineData(1.99, 1)]
    [InlineData(2, 2)]
    [InlineData(3.99, 2)]
    [InlineData(4, 3)]
    [InlineData(7.99, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 5)]
    [InlineData(31.99, 5)]
    [InlineData(32, 6)]
    [InlineData(1000, 6)]
    public void Length_buckets_are_left_closed(double delta, int bucket)
        => AssertSingleBucket("paddingTop", delta, bucket, length: 7);

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4.99, 2)]
    [InlineData(5, 3)]
    [InlineData(10, 4)]
    [InlineData(19.99, 4)]
    [InlineData(20, 5)]
    [InlineData(100, 5)]
    public void Color_buckets_are_delta_e_and_left_closed(double delta, int bucket)
    {
        AssertSingleBucket("color", delta, bucket, length: 6);
        AssertSingleBucket("background", delta, bucket, length: 6);
    }

    [Theory]
    [InlineData(99, 0)]
    [InlineData(100, 1)]
    [InlineData(199, 1)]
    [InlineData(200, 2)]
    [InlineData(299, 2)]
    [InlineData(300, 3)]
    [InlineData(800, 3)]
    public void Font_weight_buckets_are_left_closed(double delta, int bucket)
        => AssertSingleBucket("fontWeight", delta, bucket, length: 4);

    [Fact]
    public void A_negative_delta_is_bucketed_by_its_absolute_value()
        => AssertSingleBucket("width", -9, 4, length: 7);

    private static void AssertSingleBucket(string prop, double delta, int bucket, int length)
    {
        var stats = UsageStats.Compute([Report([Diff(prop, delta)])], 1, "figma");

        var p = Assert.Single(stats.Props);
        Assert.Equal(prop, p.Prop);
        Assert.Equal(1, p.Count);
        Assert.Equal(0, p.Missing);
        var expected = new int[length];
        expected[bucket] = 1;
        Assert.Equal(expected, p.Buckets);
    }

    public static TheoryData<string, int> Families => new()
    {
        { "offsetX", 7 }, { "offsetY", 7 }, { "width", 7 }, { "height", 7 },
        { "paddingTop", 7 }, { "paddingRight", 7 }, { "paddingBottom", 7 }, { "paddingLeft", 7 },
        { "itemSpacing", 7 }, { "cornerRadius", 7 }, { "fontSize", 7 }, { "lineHeight", 7 }, { "letterSpacing", 7 },
        { "color", 6 }, { "background", 6 },
        { "fontWeight", 4 },
        { "fontFamily", 0 },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_protocol_property_has_its_familys_bucket_count(string prop, int length)
    {
        var p = Assert.Single(UsageStats.Compute([Report([Diff(prop, 3)])], 0, "figma").Props);

        Assert.Equal(length, p.Buckets.Count);
        Assert.Equal(length == 0 ? 0 : 1, p.Buckets.Sum()); // 無量值的家族:有 delta 也只計入 count
    }

    [Fact]
    public void The_property_list_is_exactly_the_protocols_closed_set_in_its_order()
        => Assert.Equal(Families.Select(row => (string)row[0]), UsageStats.PropFamilies.Select(f => f.Prop));

    [Fact]
    public void Every_property_the_engine_can_emit_is_in_the_protocols_closed_set()
    {
        // 屬性名來自 DiffEngine 的字面值:CompareNum/CompareColor 的呼叫點與直接 new PropDiff("…") 的地方。
        // 引擎多一種屬性而協定沒有,這裡紅——先改協定與收集站,不要在 UsageStats 替它發明家族
        var source = Repo.Read("src", "Parity.Engine", "Comparison", "DiffEngine.cs");
        var emitted = Regex.Matches(source, @"(?:Compare(?:Num|Color)\(diffs, |new PropDiff\()""(?<p>[A-Za-z]+)""")
            .Select(m => m.Groups["p"].Value)
            .ToHashSet();
        var closedSet = UsageStats.PropFamilies.Select(f => f.Prop).ToHashSet();

        Assert.True(emitted.Count >= 17, $"只從 DiffEngine 抓到 {emitted.Count} 個屬性名,解析可能壞了");
        Assert.Empty(emitted.Except(closedSet));
        // 其餘產生 PropDiff 的地方只能是那兩個接 prop 參數的 helper——多一個動態的,上面的掃描就看不到它
        Assert.Equal(2, Regex.Matches(source, @"new PropDiff\(prop,").Count);
    }

    [Fact]
    public void Each_diff_lands_in_exactly_one_place()
    {
        // 協定:status: missing → missing;有 delta → buckets;都不是(fontFamily 的不一致)→ 只計入 count
        var stats = UsageStats.Compute([Report([
            Diff("background", null, DiffStatus.Missing, Severity.Medium),
            Diff("background", 3),
            Diff("fontFamily", null, soft: true, severity: Severity.Minor),
            Diff("fontFamily", null, soft: true, severity: Severity.Minor),
        ])], 1, "figma");

        var background = stats.Props.Single(p => p.Prop == "background");
        Assert.Equal((2, 1), (background.Count, background.Missing));
        Assert.Equal([0, 0, 1, 0, 0, 0], background.Buckets);
        var family = stats.Props.Single(p => p.Prop == "fontFamily");
        Assert.Equal((2, 0), (family.Count, family.Missing));
        Assert.Empty(family.Buckets);
        Assert.All(stats.Props, p => Assert.True(p.Missing + p.Buckets.Sum() <= p.Count));
    }

    [Fact]
    public void Only_properties_with_diffs_are_listed_in_protocol_order_and_unknown_ones_are_never_sent()
    {
        var stats = UsageStats.Compute([Report([Diff("fontWeight", 100), Diff("opacity", 0.5), Diff("width", 2)])], 1, "figma");

        Assert.Equal(["width", "fontWeight"], stats.Props.Select(p => p.Prop));
        Assert.Equal(3, stats.Severity.Serious); // 嚴重度取自報告的摘要,封閉集以外的屬性照樣算在裡面
    }

    [Fact]
    public void Totals_add_up_across_pages_and_the_score_is_the_one_check_prints()
    {
        var a = Report([Diff("width", 5, severity: Severity.Critical)], designNodes: 10, matched: 9, route: "/a");
        var b = Report([Diff("color", 12, severity: Severity.Medium), Diff("fontFamily", null, soft: true, severity: Severity.Minor)],
            designNodes: 4, matched: 4, route: "/b");

        var stats = UsageStats.Compute([a, b], 3, "snapshot");

        Assert.Equal(2, stats.Pages);
        Assert.Equal(14, stats.DesignNodes);
        Assert.Equal(13, stats.Matched);
        Assert.Equal(new SeverityTotals(Critical: 1, Serious: 0, Medium: 1, Minor: 1), stats.Severity);
        Assert.Equal(FidelityScore.Compute([a, b]), stats.Score);
        Assert.Equal("snapshot", stats.DesignSource);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Gate_failed_is_the_checks_exit_code_not_being_zero(int exitCode, bool failed)
        => Assert.Equal(failed, UsageStats.Compute([Report([])], exitCode, "figma").GateFailed);

    // ---------- 設計來源(協定 §7.2 designSource)----------

    [Fact]
    public void Design_source_follows_the_same_precedence_as_the_scan()
    {
        using var dir = new TempDir();
        dir.Write("bare.json", """{ "id": "1:1", "name": "home", "type": "Frame", "children": [] }""");

        Assert.Equal("figma", UsageStats.DesignSourceOf(new ParityConfig { FigmaFileKey = "k", BaseDirectory = dir.Path }));
        Assert.Equal("figma", UsageStats.DesignSourceOf(new ParityConfig { BaseDirectory = dir.Path }));
        Assert.Equal("file", UsageStats.DesignSourceOf(new ParityConfig { FigmaFileKey = "k", DesignFile = "bare.json", BaseDirectory = dir.Path }));
        Assert.Equal("image", UsageStats.DesignSourceOf(new ParityConfig { DesignImage = "shot.png", DesignFile = "bare.json", BaseDirectory = dir.Path }));
    }

    [Fact]
    public void A_design_file_written_by_parity_snapshot_is_a_snapshot()
    {
        using var dir = new TempDir();
        dir.Write("parity.snapshot.json", ReportJson.SerializeSnapshotTree(SnapshotWireTests.SampleTree()));

        Assert.Equal("snapshot", UsageStats.DesignSourceOf(new ParityConfig { DesignFile = "parity.snapshot.json", BaseDirectory = dir.Path }));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "root": { "id": "/" } }""", true)]
    [InlineData("""{ "SchemaVersion": 1, "root": { "id": "/" } }""", true)]                 // 讀端不分大小寫
    [InlineData("""// 註解\n{ /* x */ "root": { "id": "/", "children": [{ "schemaVersion": 1 }] }, "schemaVersion": 1, }""", true)]
    [InlineData("""{ "id": "1:1", "children": [{ "schemaVersion": 1 }] }""", false)]       // 只看頂層
    [InlineData("""{ "schemaVersion": null, "id": "1:1" }""", false)]                       // 引擎也當裸樹
    [InlineData("""[1, 2]""", false)]
    [InlineData("""not json""", false)]
    public void The_snapshot_envelope_is_detected_like_the_design_reader_does(string json, bool envelope)
    {
        using var dir = new TempDir();

        Assert.Equal(envelope, UsageStats.HasSnapshotEnvelope(dir.Write("d.json", json.Replace("\\n", "\n"))));
    }

    [Fact]
    public void A_snapshot_saved_with_a_bom_or_a_missing_file_is_handled()
    {
        using var dir = new TempDir();
        var bom = dir.File("bom.json");
        File.WriteAllText(bom, """{ "schemaVersion": 1, "root": { "id": "/" } }""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.True(UsageStats.HasSnapshotEnvelope(bom));
        Assert.False(UsageStats.HasSnapshotEnvelope(dir.File("missing.json")));
    }

    [Fact]
    public void The_offline_demos_hand_written_design_file_is_a_file()
        => Assert.Equal("file", UsageStats.DesignSourceOf(ParityConfig.Load(Repo.PathOf("samples", "demo", "parity.config.json"))));

    // ---------- JSON(協定 §7)----------

    private static StatsPayload Payload(string? projectId = null) => new(
        Release, "linux", Ci: true, projectId,
        UsageStats.Compute([Report([Diff("paddingTop", 3), Diff("color", 2.5), Diff("fontFamily", null, soft: true)])], 1, "figma"));

    [Fact]
    public void The_payload_has_exactly_the_protocols_fields()
    {
        using var doc = JsonDocument.Parse(UsageStats.ToJson(Payload(), indented: false));

        StatsProtocol.AssertMatches(doc.RootElement);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("projectId").ValueKind); // 沒有 ID 寫 null,不省略
        Assert.True(doc.RootElement.GetProperty("ci").GetBoolean());
    }

    [Fact]
    public void A_project_id_is_sent_only_as_given()
    {
        using var doc = JsonDocument.Parse(UsageStats.ToJson(Payload("my-project-id-0123"), indented: false));

        Assert.Equal("my-project-id-0123", doc.RootElement.GetProperty("projectId").GetString());
        StatsProtocol.AssertMatches(doc.RootElement);
    }

    [Fact]
    public void The_sent_json_is_compact_and_the_preview_is_the_same_document_indented()
    {
        var compact = UsageStats.ToJson(Payload(), indented: false);
        var indented = UsageStats.ToJson(Payload(), indented: true);

        Assert.DoesNotContain('\n', compact);
        Assert.DoesNotContain(' ', compact);
        Assert.Contains('\n', indented);
        Assert.Equal(compact, JsonSerializer.Serialize(JsonDocument.Parse(indented).RootElement));
    }

    [Fact]
    public void The_payload_carries_only_counts_none_of_the_reports_strings()
    {
        // 報告裡的網址、route、圖層名、selector、設計值都不能出現在送出的內容裡
        var report = new FidelityReport("/secret-route", "https://intranet.example/page", "design.json#9:9",
            [new NodeResult("Secret Layer", "9:9", "#secret-selector", "auto-name", Severity.Serious,
                [new PropDiff("color", "#123456", "#654321", null, 12.5, 2, Severity.Serious)])],
            [new UnmatchedNode("Hidden Layer", "9:10", "no-anchor")],
            new ReportSummary(2, 1, 1, 1, 0, 1, 0, 0, Severity.Serious));
        var json = UsageStats.ToJson(new StatsPayload(Release, "windows", false, null,
            UsageStats.Compute([report], 1, "figma")), indented: true);

        foreach (var secret in new[] { "secret", "intranet", "Layer", "design.json", "#12", "#65", "9:9", "no-anchor", "auto-name" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_endpoint_filter_of_the_test_process_only_lets_loopback_through()
    {
        // TestProcessDefaults:任何測試都送不到真的收集站
        var allow = UsageStats.EndpointFilterForTests;

        Assert.NotNull(allow);
        Assert.False(allow(new Uri("https://stats.cornhsu.com/v1/parity")));
        Assert.True(allow(new Uri("http://127.0.0.1:5000/v1/parity")));
    }

    // ---------- 測試資料 ----------

    internal static PropDiff Diff(string prop, double? delta, DiffStatus status = DiffStatus.Mismatch,
        Severity severity = Severity.Serious, bool soft = false)
        => new(prop, "e", "a", "px", delta, 1, severity, status, soft);

    internal static FidelityReport Report(IReadOnlyList<PropDiff> diffs, int designNodes = 3, int matched = 2, string route = "/")
    {
        List<NodeResult> nodes = diffs.Count == 0 ? [] :
            [new NodeResult("layer", "1:1", "#a", "selector", diffs.Max(d => d.Severity), diffs)];
        int Count(Severity s) => diffs.Count(d => d.Severity == s);
        return new FidelityReport(route, "http://x/", "d", nodes, [],
            new ReportSummary(designNodes, matched, designNodes - matched, nodes.Count,
                Count(Severity.Critical), Count(Severity.Serious), Count(Severity.Medium), Count(Severity.Minor),
                nodes.Count == 0 ? Severity.None : nodes.Max(n => n.Severity)));
    }
}

/// <summary>
/// 協定 §7(schema 1)在測試這邊的鏡像,照收集站(stats-collector 的 src/validate.ts)的規則寫:
/// 多一個欄位、少一個欄位、型別或範圍不對,收集站一律 400——這裡就一律紅。
/// </summary>
internal static class StatsProtocol
{
    private static readonly Dictionary<string, int> BucketLengths = new(StringComparer.Ordinal)
    {
        ["offsetX"] = 7,
        ["offsetY"] = 7,
        ["width"] = 7,
        ["height"] = 7,
        ["paddingTop"] = 7,
        ["paddingRight"] = 7,
        ["paddingBottom"] = 7,
        ["paddingLeft"] = 7,
        ["itemSpacing"] = 7,
        ["cornerRadius"] = 7,
        ["fontSize"] = 7,
        ["lineHeight"] = 7,
        ["letterSpacing"] = 7,
        ["color"] = 6,
        ["background"] = 6,
        ["fontWeight"] = 4,
        ["fontFamily"] = 0,
    };

    private static void ExactKeys(JsonElement e, params string[] keys)
    {
        Assert.Equal(JsonValueKind.Object, e.ValueKind);
        Assert.Equal(keys.Order(StringComparer.Ordinal), e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    private static int Count(JsonElement e, int max = 10_000_000)
    {
        Assert.Equal(JsonValueKind.Number, e.ValueKind);
        Assert.True(e.TryGetInt32(out var n), $"{e} 不是整數");
        Assert.InRange(n, 0, max);
        return n;
    }

    public static void AssertMatches(JsonElement root)
    {
        ExactKeys(root, "schema", "tool", "toolVersion", "os", "ci", "projectId", "data");
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal("parity", root.GetProperty("tool").GetString());
        Assert.Matches("^[0-9A-Za-z.-]{1,32}$", root.GetProperty("toolVersion").GetString());
        Assert.Contains(root.GetProperty("os").GetString(), new[] { "windows", "linux", "macos", "other" });
        Assert.True(root.GetProperty("ci").ValueKind is JsonValueKind.True or JsonValueKind.False);
        var id = root.GetProperty("projectId");
        if (id.ValueKind != JsonValueKind.Null) Assert.Matches("^[A-Za-z0-9_-]{16,64}$", id.GetString());

        var d = root.GetProperty("data");
        ExactKeys(d, "score", "gateFailed", "pages", "designSource", "designNodes", "matched", "severity", "props");
        Count(d.GetProperty("score"), 100);
        Assert.True(d.GetProperty("gateFailed").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.InRange(Count(d.GetProperty("pages"), 10_000), 1, 10_000);
        Assert.Contains(d.GetProperty("designSource").GetString(), new[] { "figma", "snapshot", "file", "image" });
        Assert.True(Count(d.GetProperty("matched")) <= Count(d.GetProperty("designNodes")));
        var severity = d.GetProperty("severity");
        ExactKeys(severity, "critical", "serious", "medium", "minor");
        foreach (var s in severity.EnumerateObject()) Count(s.Value);

        foreach (var prop in d.GetProperty("props").EnumerateObject())
        {
            Assert.True(BucketLengths.TryGetValue(prop.Name, out var length), $"props 裡有協定沒有的屬性 {prop.Name}");
            ExactKeys(prop.Value, "count", "missing", "buckets");
            var count = Count(prop.Value.GetProperty("count"));
            Assert.True(count > 0, $"{prop.Name}:沒有落差的屬性不送");
            var buckets = prop.Value.GetProperty("buckets");
            Assert.Equal(length, buckets.GetArrayLength());
            Assert.True(Count(prop.Value.GetProperty("missing")) + buckets.EnumerateArray().Sum(b => Count(b)) <= count);
        }
    }
}

/// <summary>
/// 本機的假收集站(綁 127.0.0.1:0,不連外網):記下每個請求,照模式回應——固定狀態碼、永遠不回(逾時)、
/// 或 302 轉址到自己的另一條路徑(跟了轉址的話,第二個請求會被記下來)。
/// </summary>
internal sealed class StatsCollectorStub : IAsyncDisposable
{
    public enum Mode { Status, Hang, Redirect }

    public sealed record Captured(string Method, string Path, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Captured> _requests = new();

    private StatsCollectorStub(WebApplication app) => _app = app;

    public string Url => _app.Urls.First().TrimEnd('/');

    public IReadOnlyList<Captured> Requests => [.. _requests];

    public static async Task<StatsCollectorStub> StartAsync(Mode mode = Mode.Status, int status = 204)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StatsCollectorStub(app);
        var stopping = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            stub._requests.Enqueue(new Captured(ctx.Request.Method, ctx.Request.Path,
                ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            switch (mode)
            {
                case Mode.Hang:
                    using (var both = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping))
                    {
                        try { await Task.Delay(Timeout.Infinite, both.Token); }
                        catch (OperationCanceledException) { }
                    }
                    return;
                case Mode.Redirect:
                    ctx.Response.StatusCode = StatusCodes.Status302Found;
                    ctx.Response.Headers.Location = "/redirected";
                    return;
                default:
                    ctx.Response.StatusCode = status;
                    return;
            }
        });
        await app.StartAsync();
        return stub;
    }

    /// <summary>一個沒有人在聽的 loopback 位址(拒絕連線)。</summary>
    public static string RefusingUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// 送出那一段:對本機假收集站實送,看送出的請求、告知、job summary,以及「收集站壞掉時什麼都不變」。
/// 攔 Console.Out(證明統計一個字都不寫到 stdout),所以放進不平行的集合。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class UsageStatsSendTests
{
    private static readonly ParityConfig Config = new();

    private static IReadOnlyList<FidelityReport> Reports() =>
        [UsageStatsTests.Report([UsageStatsTests.Diff("paddingTop", 3), UsageStatsTests.Diff("color", 12)])];

    private sealed class Run(Dictionary<string, string?> vars, string? noticeDirectory, string version = "2.1.0")
    {
        public StringWriter Err { get; } = new();

        public StatsEnvironment Env => new(name => vars.GetValueOrDefault(name), version, "linux", noticeDirectory, Err);
    }

    /// <summary>跑一次 AfterCheckAsync,同時攔下 stdout(必須是空的)。</summary>
    private static async Task<(string Err, TimeSpan Elapsed)> AfterCheckAsync(
        Dictionary<string, string?> vars, string? noticeDirectory, bool preview = false, ParityConfig? config = null,
        string version = "2.1.0")
    {
        var run = new Run(vars, noticeDirectory, version);
        var previous = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var clock = Stopwatch.StartNew();
        try
        {
            await UsageStats.AfterCheckAsync(run.Env, config ?? Config, Reports(), 1, preview);
        }
        finally
        {
            Console.SetOut(previous);
        }
        Assert.Equal("", stdout.ToString()); // 說明只印在 stderr
        return (run.Err.ToString().Replace("\r\n", "\n"), clock.Elapsed);
    }

    private static string Normalized(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public async Task One_post_with_the_protocols_body_and_no_identifying_headers()
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();

        await AfterCheckAsync(new() { ["CORNHSU_STATS_URL"] = stub.Url }, dir.Path);

        var req = Assert.Single(stub.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1/parity", req.Path);
        Assert.Equal("application/json", req.Header("Content-Type")); // 不帶 charset
        Assert.Null(req.Header("User-Agent"));
        Assert.Null(req.Header("Cookie"));
        Assert.Null(req.Header("traceparent"));
        // 協定 §6:不帶任何自訂標頭——只剩 HTTP 本身需要的
        Assert.Equal(["Content-Length", "Content-Type", "Host"], req.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase));
        using var body = JsonDocument.Parse(req.Body);
        StatsProtocol.AssertMatches(body.RootElement);
        Assert.Equal(UsageStats.ToJson(UsageStats.Build(new Run([], null).Env, Config, Reports(), 1), indented: false), req.Body);
        Assert.Equal(Encoding.UTF8.GetByteCount(req.Body), int.Parse(req.Header("Content-Length")!)); // 沒有 BOM
    }

    [Fact]
    public async Task The_full_notice_shows_once_per_machine_and_leaves_a_marker()
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();
        var vars = new Dictionary<string, string?> { ["CORNHSU_STATS_URL"] = stub.Url };

        var first = await AfterCheckAsync(vars, dir.Path);
        var second = await AfterCheckAsync(vars, dir.Path);

        Assert.Equal(Normalized(UsageStats.FirstRunNotice) + "\n", first.Err);
        Assert.True(File.Exists(dir.File("cornhsu", "parity-stats-notice")), "標記檔沒有寫在 <ApplicationData>/cornhsu/ 底下");
        Assert.Equal("", second.Err);
        Assert.Equal(2, stub.Requests.Count); // 說明只印一次,統計每次都送
    }

    [Fact]
    public async Task The_notice_matches_the_protocols_template()
    {
        // 協定 §4 的範本換成 Parity 的名稱、設定檔、環境變數與預覽旗標
        Assert.Equal("""
            Parity sends anonymous usage statistics: counts and score ranges only - no URLs, names,
            selectors, file paths, colours or screenshots, and no IP address is stored. They support a
            master's thesis on design fidelity.
            What is sent and why: https://stats.cornhsu.com/
            Preview it:  parity check --stats-preview   (sends nothing)
            Turn it off: "stats": { "share": false } in parity.config.json, or PARITY_SHARE_STATS=false,
                         CORNHSU_SHARE_STATS=false or DO_NOT_TRACK=1
            This notice is shown once on this machine.
            """.Replace("\r\n", "\n"), Normalized(UsageStats.FirstRunNotice));
        Assert.Equal("Parity: anonymous usage statistics sent (counts only) - https://stats.cornhsu.com/ - turn off with DO_NOT_TRACK=1",
            UsageStats.CiLine);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("CI")]
    [InlineData("GITHUB_ACTIONS")]
    public async Task On_ci_one_line_every_time_and_no_marker(string variable)
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();
        var vars = new Dictionary<string, string?> { ["CORNHSU_STATS_URL"] = stub.Url, [variable] = "true" };

        var first = await AfterCheckAsync(vars, dir.Path);
        var second = await AfterCheckAsync(vars, dir.Path);

        Assert.Equal(UsageStats.CiLine + "\n", first.Err);
        Assert.Equal(UsageStats.CiLine + "\n", second.Err);
        Assert.False(Directory.Exists(dir.File("cornhsu")), "CI 上不寫標記檔");
        Assert.Equal(2, stub.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/dir")]
    public async Task Without_a_usable_settings_directory_the_notice_shows_every_time(string? noticeDirectory)
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        var vars = new Dictionary<string, string?> { ["CORNHSU_STATS_URL"] = stub.Url };

        Assert.Contains("This notice is shown once", (await AfterCheckAsync(vars, noticeDirectory)).Err);
        Assert.Contains("This notice is shown once", (await AfterCheckAsync(vars, noticeDirectory)).Err);
        Assert.False(Directory.Exists(Path.Combine("relative", "dir")));
        Assert.Equal(2, stub.Requests.Count);
    }

    public static TheoryData<string, string> OffSwitches => new()
    {
        { "DO_NOT_TRACK", "1" },
        { "CORNHSU_SHARE_STATS", "false" },
        { "PARITY_SHARE_STATS", "0" },
        { "PARITY_SHARE_STATS", "nope" },
    };

    [Theory]
    [MemberData(nameof(OffSwitches))]
    public async Task Every_switch_turns_sending_off_silently(string variable, string value)
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();

        var r = await AfterCheckAsync(new() { ["CORNHSU_STATS_URL"] = stub.Url, [variable] = value }, dir.Path);

        Assert.Empty(stub.Requests);
        Assert.Equal("", r.Err); // 不送的時候什麼都不印
        Assert.False(Directory.Exists(dir.File("cornhsu")));
    }

    [Fact]
    public async Task Config_share_false_turns_sending_off()
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();
        var config = ParityConfig.Load(dir.Write("parity.config.json", """{ "stats": { "share": false } }"""));

        var r = await AfterCheckAsync(new() { ["CORNHSU_STATS_URL"] = stub.Url }, dir.Path, config: config);

        Assert.Empty(stub.Requests);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public async Task A_development_build_without_a_stats_url_sends_nothing_and_says_nothing()
    {
        using var dir = new TempDir();

        var r = await AfterCheckAsync([], dir.Path, version: "0.0.0-dev");

        Assert.Equal("", r.Err);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(1), "開發版不該去連任何地方");
    }

    [Fact]
    public async Task Preview_sends_nothing_and_prints_the_json_and_the_verdict()
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();

        var r = await AfterCheckAsync(new() { ["CORNHSU_STATS_URL"] = stub.Url }, dir.Path, preview: true);

        Assert.Empty(stub.Requests);
        Assert.False(Directory.Exists(dir.File("cornhsu")), "預覽不寫標記");
        Assert.DoesNotContain("This notice", r.Err); // 預覽不印告知
        var lines = r.Err.TrimEnd('\n').Split('\n');
        Assert.Equal("--stats-preview: nothing is sent. The anonymous usage statistics of this run:", lines[0]);
        Assert.Equal($"would be sent: POST {stub.Url}/v1/parity", lines[^1]);
        using var json = JsonDocument.Parse(string.Join("\n", lines[1..^1]));
        StatsProtocol.AssertMatches(json.RootElement);
    }

    [Theory]
    [InlineData("DO_NOT_TRACK", "1", "would not be sent: DO_NOT_TRACK is set")]
    [InlineData("CORNHSU_SHARE_STATS", "s3cr3t-typo", "would not be sent: CORNHSU_SHARE_STATS is set to something other than 1, true, yes or on")]
    [InlineData("PARITY_SHARE_STATS", "s3cr3t-typo", "would not be sent: PARITY_SHARE_STATS is set to something other than 1, true, yes or on")]
    public async Task Preview_says_why_it_would_not_be_sent(string variable, string value, string verdict)
    {
        using var dir = new TempDir();

        var r = await AfterCheckAsync(new() { [variable] = value }, dir.Path, preview: true);

        Assert.EndsWith(verdict + "\n", r.Err);
        Assert.DoesNotContain("s3cr3t", r.Err); // 原因只有常數字串:環境變數的值一律不印
    }

    [Fact]
    public async Task Preview_of_a_development_build_names_the_reason()
    {
        using var dir = new TempDir();

        var r = await AfterCheckAsync([], dir.Path, preview: true, version: "0.0.0-dev");

        Assert.EndsWith("would not be sent: this is a development build (0.0.0-dev) and CORNHSU_STATS_URL is not set\n", r.Err);
    }

    public static TheoryData<string> BrokenCollectors => new() { "hang", "refused", "500", "redirect" };

    [Theory]
    [MemberData(nameof(BrokenCollectors))]
    public async Task A_broken_collector_costs_at_most_about_three_seconds_and_prints_nothing(string kind)
    {
        await using var stub = await StatsCollectorStub.StartAsync(
            kind switch { "hang" => StatsCollectorStub.Mode.Hang, "redirect" => StatsCollectorStub.Mode.Redirect, _ => StatsCollectorStub.Mode.Status },
            status: 500);
        using var dir = new TempDir();
        var summary = dir.Write("summary.md", "");
        var url = kind == "refused" ? StatsCollectorStub.RefusingUrl() : stub.Url;
        var vars = new Dictionary<string, string?>
        {
            ["CORNHSU_STATS_URL"] = url,
            ["CI"] = "true",
            ["PARITY_STATS_JOB_SUMMARY"] = "true",
            ["GITHUB_STEP_SUMMARY"] = summary,
        };

        var r = await AfterCheckAsync(vars, dir.Path);

        Assert.Equal("", r.Err);                        // 送不出去就算了:CI 那一行也不印(沒送成)
        Assert.Equal("", File.ReadAllText(summary));    // job summary 也不寫
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(4.5), $"花了 {r.Elapsed.TotalSeconds:0.0} 秒(上限:連線 1.5 秒、整體 3 秒)");
        if (kind == "hang") Assert.True(r.Elapsed > TimeSpan.FromSeconds(2.5), "整體 3 秒的上限沒有作用?");
        if (kind == "redirect") Assert.Equal(["/v1/parity"], stub.Requests.Select(q => q.Path)); // 不跟隨轉址
    }

    [Fact]
    public async Task Send_reports_success_only_for_2xx_and_never_throws()
    {
        await using var ok = await StatsCollectorStub.StartAsync(status: 204);
        await using var bad = await StatsCollectorStub.StartAsync(status: 400);

        Assert.True(await UsageStats.SendAsync(new Uri(ok.Url + "/v1/parity"), "{}"));
        Assert.False(await UsageStats.SendAsync(new Uri(bad.Url + "/v1/parity"), "{}"));
        Assert.False(await UsageStats.SendAsync(new Uri(StatsCollectorStub.RefusingUrl() + "/v1/parity"), "{}"));
    }

    [Fact]
    public void The_transport_has_the_protocols_limits()
    {
        using var handler = UsageStats.CreateHandler();

        Assert.Equal(TimeSpan.FromSeconds(1.5), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), UsageStats.TotalTimeout);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.ActivityHeadersPropagator);
    }

    [Fact]
    public async Task The_action_gets_one_job_summary_line_saying_sent()
    {
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();
        var summary = dir.Write("summary.md", "existing\n");

        await AfterCheckAsync(new()
        {
            ["CORNHSU_STATS_URL"] = stub.Url,
            ["GITHUB_ACTIONS"] = "true",
            ["PARITY_STATS_JOB_SUMMARY"] = "true",
            ["GITHUB_STEP_SUMMARY"] = summary,
        }, dir.Path);

        Assert.Equal("existing\n" + UsageStats.SentSummary + "\n", File.ReadAllText(summary));
    }

    [Fact]
    public async Task The_action_gets_one_job_summary_line_saying_why_not()
    {
        using var dir = new TempDir();
        var summary = dir.Write("summary.md", "");

        var r = await AfterCheckAsync(new()
        {
            ["PARITY_SHARE_STATS"] = "false",
            ["GITHUB_ACTIONS"] = "true",
            ["PARITY_STATS_JOB_SUMMARY"] = "true",
            ["GITHUB_STEP_SUMMARY"] = summary,
        }, dir.Path);

        Assert.Equal("", r.Err);
        Assert.Equal("**Parity** — anonymous usage statistics not sent: turned off by PARITY_SHARE_STATS (`share-stats: false` sets it).\n",
            File.ReadAllText(summary));
    }

    [Fact]
    public async Task Outside_the_action_the_job_summary_is_left_alone()
    {
        // 自己在 workflow 裡跑 parity check 的人:沒有 PARITY_STATS_JOB_SUMMARY,job summary 不多一行
        await using var stub = await StatsCollectorStub.StartAsync();
        using var dir = new TempDir();
        var summary = dir.Write("summary.md", "");

        await AfterCheckAsync(new() { ["CORNHSU_STATS_URL"] = stub.Url, ["GITHUB_ACTIONS"] = "true", ["GITHUB_STEP_SUMMARY"] = summary }, dir.Path);

        Assert.Single(stub.Requests);
        Assert.Equal("", File.ReadAllText(summary));
    }
}
