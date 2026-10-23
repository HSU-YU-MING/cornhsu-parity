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
    /// <summary>涵蓋各種欄位形狀的固定報告:數值落差、軟落差(null unit/delta)、缺失、零落差節點、未配對。</summary>
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
    ]);

    private static string Golden() => Normalize(Repo.Read("tests", "Parity.Tests", "Golden", "report.v1.json"));

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
