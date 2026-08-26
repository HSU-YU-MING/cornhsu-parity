using System.Text.Json;
using Parity.Cli;
using Parity.Engine;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// Parity.Server 的報告收件(網頁外殼 M1):report.json 原文 → Run 實體圖。
/// 契約單一來源的驗證方式:測試資料不是手寫 JSON,而是拿引擎自己的 ReportDocument
/// 序列化出來——引擎寫的,伺服器就必須讀得回去。
/// </summary>
public class ServerIngestTests
{
    private static string SampleReportJson()
    {
        var diffs = new List<PropDiff>
        {
            new("paddingTop", "12", "8", "px", 4, 2, Severity.Serious),
            new("fontFamily", "Inter", "Arial", null, null, 0, Severity.Minor, Soft: true),
        };
        var nodes = new List<NodeResult>
        {
            new("CTA Button", "10:2", "#cta", "auto-name", Severity.Serious, diffs),
            new("Title", "10:3", "h1", "auto-text", Severity.None, []),
        };
        var report = new FidelityReport("/", "http://localhost/", "figma:abc",
            nodes,
            [new UnmatchedNode("Ghost", "10:9", "no-anchor")],
            new ReportSummary(3, 2, 1, 1, 0, 1, 0, 1, Severity.Serious));
        return JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Indented);
    }

    [Fact]
    public void Report_maps_to_run_pages_and_diffs()
    {
        var raw = SampleReportJson();
        var run = RunIngest.Parse(raw, new RunMetadata("abc123", "main", "cornhsu"), DateTimeOffset.UnixEpoch);

        Assert.Equal("abc123", run.CommitSha);
        Assert.Equal("main", run.Branch);
        Assert.True(run.GateFailed); // summary 有 serious → 紅
        Assert.Equal(raw, ReportBlob.Decompress(run.RawReportGzip)); // 原文壓縮入庫,解壓後逐位元不變

        var page = Assert.Single(run.Pages);
        Assert.Equal("/", page.Route);
        Assert.Equal(3, page.DesignNodes);
        Assert.Equal(2, page.Matched);
        Assert.Equal("serious", page.MaxSeverity); // 開放字彙存字串、小寫對齊 wire 格式

        // 逐條落差刻意不進關聯表(零讀者、36k 列/9.95MB 的實測教訓)——
        // 落差的真相源是原文 blob,解壓回來逐位元等於送進去的
        Assert.Contains("paddingTop", ReportBlob.Decompress(run.RawReportGzip));
    }

    [Fact]
    public void Newer_schema_is_a_distinct_rejection_not_bad_data()
    {
        var raw = SampleReportJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        var ex = Assert.Throws<SchemaTooNewException>(
            () => RunIngest.Parse(raw, new RunMetadata(null, null, null), DateTimeOffset.UnixEpoch));
        Assert.Equal(99, ex.Got); // 422 的素材:是版本落差,不是壞資料(400)
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "hello": "world" }""")] // 合法 JSON 但不是報告信封
    public void Garbage_is_rejected_with_a_clear_message(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RunIngest.Parse(raw, new RunMetadata(null, null, null), DateTimeOffset.UnixEpoch));
        Assert.Contains("report", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clean_report_gate_passes_and_score_is_computed()
    {
        var report = new FidelityReport("/ok", "http://localhost/ok", "snap",
            [new NodeResult("A", "a", "#a", "selector", Severity.None, [])],
            [],
            new ReportSummary(1, 1, 0, 0, 0, 0, 0, 0, Severity.None));
        var raw = JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact);

        var run = RunIngest.Parse(raw, new RunMetadata(null, null, null), DateTimeOffset.UnixEpoch);
        Assert.False(run.GateFailed);
        Assert.Equal(100, run.Score);
    }
}

/// <summary>專案 token:只存 hash、驗證走 hash 查找。</summary>
public class ProjectTokenTests
{
    [Fact]
    public void Tokens_are_unique_urlsafe_and_hash_deterministically()
    {
        var a = ProjectToken.Generate();
        var b = ProjectToken.Generate();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        Assert.InRange(a.Length, 40, 50); // 32 bytes base64url ≈ 43 字元
        Assert.Equal(ProjectToken.Hash(a), ProjectToken.Hash(a));
        Assert.NotEqual(ProjectToken.Hash(a), ProjectToken.Hash(b));
    }
}

/// <summary>push 的本機端信封檢查:壞檔案在送出前就講清楚。</summary>
public class PushEnvelopeTests
{
    [Fact]
    public void Valid_envelope_passes_and_garbage_fails_locally()
    {
        var report = new FidelityReport("/", "u", "d", [], [],
            new ReportSummary(0, 0, 0, 0, 0, 0, 0, 0, Severity.None));
        var ok = JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact);
        PushCommand.ValidateEnvelope(ok, "report.json"); // 不丟例外 = 通過

        Assert.Throws<InvalidOperationException>(
            () => PushCommand.ValidateEnvelope("""{ "nope": 1 }""", "report.json"));
        Assert.Throws<InvalidOperationException>(
            () => PushCommand.ValidateEnvelope("not json", "report.json"));
    }
}
