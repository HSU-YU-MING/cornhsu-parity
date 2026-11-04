using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Engine;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// 凍結面 4(report.json)的 wire 形狀。ReportRoundTripTests 只證明「寫得出去、讀得回來」——
/// 兩邊一起漂(例如改了命名策略)它照樣綠。這裡釘的是**位元組**:key 名稱與順序、enum 的
/// 小寫字串、unit/delta 的顯式 null、Box 的 width/height、頂層 schemaVersion。
/// 消費端有三家(parity report、serve、Parity.Server)外加使用者自己的腳本,任何一處走樣都是破壞。
/// </summary>
public class ReportWireContractTests
{
    /// <summary>
    /// 涵蓋各種欄位形狀的固定報告:數值落差、軟落差(null unit/delta)、缺失、零落差節點、未配對,
    /// 以及 extras 的兩種形狀(沒開的 null、開了且截斷過的清單:total 3、items 2)。
    /// </summary>
    internal static ReportDocument Sample() => ReportDocument.Of(
    [
        new FidelityReport(
            "/pricing", "http://localhost:8080/pricing", "abcd1234#10:1",
            [
                new NodeResult("CTA Button", "10:2", "#cta", "auto-name", Severity.Serious,
                [
                    new PropDiff("paddingTop", "12", "8", "px", 4, 2, Severity.Serious),
                    new PropDiff("fontFamily", "Inter", "Arial", null, null, 0, Severity.Minor, Soft: true),
                    new PropDiff("background", "#2563EB", "(transparent)", null, null, 2, Severity.Medium, DiffStatus.Missing),
                ],
                new Box(16, 24, 160, 48), new Box(16, 26, 158, 48)),
                new NodeResult("Title", "10:3", "body > h1:nth-of-type(1)", "selector", Severity.None, [],
                    new Box(0, 0, 320, 40.5), new Box(0, 0, 320, 40)),
            ],
            [new UnmatchedNode("Badge", "10:9", "no-anchor", new Box(0, 0, 40, 20))],
            new ReportSummary(3, 2, 1, 1, 0, 1, 1, 1, Severity.Serious)),
        new FidelityReport(
            "/landing", "http://localhost:8080/landing", "abcd1234#10:20", [], [],
            new ReportSummary(0, 0, 0, 0, 0, 0, 0, 0, Severity.None),
            new ExtraElements(3,
            [
                new ExtraElement("body > div:nth-of-type(1)", "div", new Box(0, 0, 800, 200)),
                new ExtraElement("#promo", "img", new Box(16, 240, 320, 180.5)),
            ])),
    ]);

    /// <summary>現行寫端的位元組。extras(compare.extras)加進來時另開這一份,2.2.0 寫的 report.v1.json 原樣留著當舊檔。</summary>
    private static string Golden() => Normalize(Repo.Read("tests", "Parity.Tests", "Golden", "report.v1-extras.json"));

    /// <summary>2.2.0(extras 出現之前)寫出的報告,一個位元組都不改:新版讀舊檔的對照組。</summary>
    private static string GoldenBeforeExtras() => Normalize(Repo.Read("tests", "Parity.Tests", "Golden", "report.v1.json"));

    // 行尾隨平台(Indented 用 Environment.NewLine;checkout 的 golden 檔行尾隨 core.autocrlf),只比內容
    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    [Fact]
    public void Indented_report_is_byte_for_byte_the_golden_file()
    {
        // golden 連 selector 裡 `>` 被預設 encoder 寫成 > 都照抄:對 JSON 解析端等價,
        // 但換 encoder 會讓每份落地的 report.json 改變位元組——要改就是有意識地重生 golden
        var json = JsonSerializer.Serialize(Sample(), ReportWire.Indented);

        Assert.Equal(Golden(), Normalize(json));
    }

    [Fact]
    public void The_envelope_says_which_direction_the_report_was_written_in()
    {
        // 決策 #27(2.0.0):check --reverse 寫出的報告期望/實際對調,1.x 的信封卻沒有任何標記,
        // parity report 照正向重算 gate、parity push 當一般 run 推上去。direction 一律寫出(不省略)
        var forward = JsonSerializer.Serialize(Sample(), ReportWire.Compact);
        var reverse = JsonSerializer.Serialize(Sample() with { Direction = ReportDirection.Reverse }, ReportWire.Compact);

        Assert.EndsWith(",\"direction\":\"forward\"}", forward);
        Assert.EndsWith(",\"direction\":\"reverse\"}", reverse);
    }

    [Fact]
    public void A_report_written_before_direction_existed_reads_as_forward()
    {
        var doc = JsonSerializer.Deserialize<ReportDocument>("""{ "schemaVersion": 1, "reports": [] }""", ReportWire.Compact)!;

        Assert.Equal(ReportDirection.Forward, doc.Direction);
    }

    [Theory]
    [InlineData("\"forward\"", ReportDirection.Forward)]
    [InlineData("\"reverse\"", ReportDirection.Reverse)]
    public void Direction_reads_exactly_the_two_lowercase_strings(string json, ReportDirection expected)
    {
        var doc = JsonSerializer.Deserialize<ReportDocument>($$"""{ "schemaVersion": 1, "reports": [], "direction": {{json}} }""", ReportWire.Compact)!;

        Assert.Equal(expected, doc.Direction);
    }

    [Theory]
    [InlineData("\"Reverse\"")]   // 大小寫不同:Parity 一律寫小寫,不放寬
    [InlineData("\"REVERSE\"")]
    [InlineData("\"sideways\"")]
    [InlineData("\"\"")]
    [InlineData("1")]             // 審查實證:字串 enum 轉換器原本也收數字(1 讀成 reverse、5 讀成未定義的值)
    [InlineData("5")]
    [InlineData("null")]          // 有寫但是 null ≠ 沒寫;沒寫才讀成 forward
    [InlineData("true")]
    public void Any_other_direction_is_refused_with_a_message_that_names_the_field(string json)
    {
        // 審查第 6 條:原本走字串 enum 轉換器,"Reverse"、"REVERSE" 照收、數字也收,"sideways" 則丟出泛用的
        // JsonException——parity report 再把它說成「likely an older format」。2.0.0 直接收緊:只收 forward/reverse
        var ex = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ReportDocument>(
            $$"""{ "schemaVersion": 1, "reports": [], "direction": {{json}} }""", ReportWire.Compact));

        Assert.IsType<ReportContractException>(ex);
        Assert.Contains("\"direction\" must be \"forward\" or \"reverse\"", ex.Message);
    }

    [Fact]
    public void Null_fields_are_written_explicitly_in_both_variants()
    {
        // CLAUDE.md 地雷:刻意不設 WhenWritingNull——消費端不必判斷 key 存不存在
        var indented = JsonSerializer.Serialize(Sample(), ReportWire.Indented);
        var compact = JsonSerializer.Serialize(Sample(), ReportWire.Compact);

        Assert.Contains("\"unit\": null", indented);
        Assert.Contains("\"delta\": null", indented);
        Assert.Contains("\"unit\":null,\"delta\":null", compact);
        Assert.Equal(JsonIgnoreCondition.Never, ReportWire.Compact.DefaultIgnoreCondition);
        Assert.Equal(JsonIgnoreCondition.Never, ReportWire.Indented.DefaultIgnoreCondition);
    }

    [Fact]
    public void Both_variants_allow_512_levels_and_only_indented_is_indented()
    {
        // CLAUDE.md 地雷:真實網站 DOM 30+ 層,每層 Children 佔 2 個 JSON 深度,預設 64 會炸
        Assert.Equal(512, ReportWire.Compact.MaxDepth);
        Assert.Equal(512, ReportWire.Indented.MaxDepth);
        Assert.True(ReportWire.Indented.WriteIndented);
        Assert.False(ReportWire.Compact.WriteIndented);
        Assert.DoesNotContain('\n', JsonSerializer.Serialize(Sample(), ReportWire.Compact));
    }

    [Fact]
    public void Golden_file_reads_back_and_rewrites_identically()
    {
        var back = JsonSerializer.Deserialize<ReportDocument>(Golden(), ReportWire.Indented)!;

        Assert.Equal(Golden(), Normalize(JsonSerializer.Serialize(back, ReportWire.Indented)));
    }

    [Fact]
    public void A_report_written_before_extras_existed_reads_as_null_and_rewrites_with_only_that_key_added()
    {
        // 新增欄位是允許的加法(README 字彙表:消費端忽略未知 key、2.x 只加不改):舊檔照讀、extras 讀成 null,
        // 重寫時除了多一行 "extras": null 之外逐位元相同——既有欄位一個都沒動
        var old = GoldenBeforeExtras();
        Assert.DoesNotContain("extras", old);

        var back = JsonSerializer.Deserialize<ReportDocument>(old, ReportWire.Indented)!;

        Assert.Null(Assert.Single(back.Reports).Extras);
        const string summaryEnd = "\"maxSeverity\": \"serious\"\n      }\n    }";
        Assert.Contains(summaryEnd, old);
        var expected = old.Replace(summaryEnd, "\"maxSeverity\": \"serious\"\n      },\n      \"extras\": null\n    }");
        Assert.Equal(expected, Normalize(JsonSerializer.Serialize(back, ReportWire.Indented)));
    }

    [Fact]
    public void Extras_comes_after_summary_null_when_off_and_an_object_when_on()
    {
        // 「沒開」(null,key 照樣寫出)與「開了、沒有」({ total: 0, items: [] })讀的人要分得出來
        var off = JsonSerializer.Serialize(Sample().Reports[0], ReportWire.Compact);
        var none = JsonSerializer.Serialize(Sample().Reports[0] with { Extras = new ExtraElements(0, []) }, ReportWire.Compact);
        var some = JsonSerializer.Serialize(Sample().Reports[1], ReportWire.Compact);

        Assert.EndsWith("\"maxSeverity\":\"serious\"},\"extras\":null}", off);
        Assert.EndsWith("},\"extras\":{\"total\":0,\"items\":[]}}", none);
        Assert.EndsWith("},\"extras\":{\"total\":3,\"items\":[" +
            "{\"selector\":\"body \\u003E div:nth-of-type(1)\",\"tag\":\"div\",\"box\":{\"x\":0,\"y\":0,\"width\":800,\"height\":200}}," +
            "{\"selector\":\"#promo\",\"tag\":\"img\",\"box\":{\"x\":16,\"y\":240,\"width\":320,\"height\":180.5}}]}}", some);
        Assert.Null(JsonSerializer.Deserialize<FidelityReport>(off, ReportWire.Compact)!.Extras);
        Assert.Equal(0, JsonSerializer.Deserialize<FidelityReport>(none, ReportWire.Compact)!.Extras!.Total);
    }

    [Fact]
    public void An_extra_element_carries_only_selector_tag_and_box()
    {
        // report.json 會被推到伺服器:多餘元件不帶元素文字,不多帶一種新的內容
        Assert.Equal(["Selector", "Tag", "Box"], typeof(ExtraElement).GetProperties().Select(p => p.Name));
        Assert.Equal(["Total", "Items"], typeof(ExtraElements).GetProperties().Select(p => p.Name));
    }

    // ---------- 封閉集(1.0 明文:新增成員或改拼寫 = major) ----------

    [Fact]
    public void Severity_is_a_closed_set_of_five_lowercase_strings()
    {
        var expected = new Dictionary<Severity, string>
        {
            [Severity.None] = "none",
            [Severity.Minor] = "minor",
            [Severity.Medium] = "medium",
            [Severity.Serious] = "serious",
            [Severity.Critical] = "critical",
        };

        Assert.Equal(5, Enum.GetValues<Severity>().Length);
        foreach (var s in Enum.GetValues<Severity>())
            Assert.Equal($"\"{expected[s]}\"", JsonSerializer.Serialize(s, ReportWire.Compact));
    }

    [Fact]
    public void Diff_status_is_a_closed_set_of_two_lowercase_strings()
    {
        Assert.Equal([DiffStatus.Mismatch, DiffStatus.Missing], Enum.GetValues<DiffStatus>());
        Assert.Equal("\"mismatch\"", JsonSerializer.Serialize(DiffStatus.Mismatch, ReportWire.Compact));
        Assert.Equal("\"missing\"", JsonSerializer.Serialize(DiffStatus.Missing, ReportWire.Compact));
    }

    // ---------- 讀端的寬嚴(盤點 5.3 實測) ----------

    private static PropDiff ReadDiff(string diffJson)
    {
        var json = $$"""
            { "schemaVersion": 1, "reports": [{ "route": "/", "url": "u", "designReference": "d",
              "nodes": [{ "designLayer": "a", "designId": "a", "selector": "s", "matchedBy": "selector",
                          "severity": "minor", "diffs": [{{diffJson}}] }],
              "unmatched": [], "summary": { "maxSeverity": "minor" } }] }
            """;
        var doc = JsonSerializer.Deserialize<ReportDocument>(json, ReportWire.Compact)!;
        return doc.Reports[0].Nodes[0].Diffs[0];
    }

    [Theory]
    [InlineData("""{ "prop": "width", "expected": "1", "actual": "2", "tolerance": 2, "severity": "blocker", "status": "mismatch" }""")]
    [InlineData("""{ "prop": "width", "expected": "1", "actual": "2", "tolerance": 2, "severity": "minor", "status": "gone" }""")]
    public void Unknown_closed_set_strings_are_rejected(string diffJson)
    {
        Assert.ThrowsAny<JsonException>(() => ReadDiff(diffJson));
    }

    [Fact]
    public void Pascal_case_enum_strings_are_accepted()
    {
        var d = ReadDiff("""{ "prop": "width", "expected": "1", "actual": "2", "tolerance": 2, "severity": "Serious", "status": "Missing" }""");

        Assert.Equal(Severity.Serious, d.Severity);
        Assert.Equal(DiffStatus.Missing, d.Status);
    }

    [Fact]
    public void Undefined_integer_severity_is_currently_accepted()
    {
        // 固定現況(盤點 5.3,待決):封閉集只對字串生效,整數 7 照收。寫端從不輸出整數;
        // 若決定讀端拒收整數(patch,但要在 Parity.Server 開始收件前定案),把這條改成預期 JsonException
        var d = ReadDiff("""{ "prop": "width", "expected": "1", "actual": "2", "tolerance": 2, "severity": 7, "status": "mismatch" }""");

        Assert.Equal((Severity)7, d.Severity);
    }

    [Fact]
    public void Missing_optional_keys_read_as_null_and_unknown_keys_are_ignored()
    {
        var d = ReadDiff("""{ "prop": "fontFamily", "expected": "Inter", "actual": "Arial", "tolerance": 0, "severity": "minor", "status": "mismatch", "soft": true, "futureField": 1 }""");

        Assert.Null(d.Unit);
        Assert.Null(d.Delta);
        Assert.True(d.Soft);
    }

    [Fact]
    public void Missing_schema_version_reads_as_zero()
    {
        var doc = JsonSerializer.Deserialize<ReportDocument>("""{ "reports": [] }""", ReportWire.Compact)!;

        Assert.Equal(0, doc.SchemaVersion);
        Assert.Empty(doc.Reports);
    }

    [Fact]
    public void Current_schema_version_is_1()
    {
        // 升到 2 = major(舊 CLI 的 report 照讀,但 Parity.Server 會回 422)
        Assert.Equal(1, ReportDocument.CurrentSchemaVersion);
        Assert.StartsWith("{\"schemaVersion\":1,\"reports\":[", JsonSerializer.Serialize(Sample(), ReportWire.Compact));
    }
}
