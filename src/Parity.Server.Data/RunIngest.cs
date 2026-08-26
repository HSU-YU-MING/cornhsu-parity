using System.Text.Json;
using Parity.Engine;

namespace Parity.Server.Data;

/// <summary>
/// push 上來的一次執行的中繼資料(由 CI 環境/push 端提供)。
/// GateFailed:**CLI 端算好的真實 gate 結果**(含使用者自訂 failOn)——伺服器不重判,
/// 免得出現「CI 綠燈、儀表板紅字」的口徑分裂;null(舊版 CLI / 裸 API)才退回
/// 伺服器端的預設口徑(critical/serious)。
/// </summary>
public sealed record RunMetadata(
    string? CommitSha, string? Branch, string? TriggeredBy,
    bool? GateFailed = null, string? RepoUrl = null);

/// <summary>
/// report.json 原文 → Run 實體圖。純函式、可單元測試;唯一的解析點,
/// 用引擎的 ReportWire 設定(契約單一來源,不自己抄一份序列化設定)。
/// </summary>
public static class RunIngest
{
    /// <summary>
    /// 解析並映射。丟 InvalidOperationException = 呼叫端該回 400(內容不是有效報告);
    /// schemaVersion 比伺服器新 = 該回 422(請升級伺服器,訊息講清楚,不靜默失敗)。
    /// </summary>
    public static Run Parse(string rawReportJson, RunMetadata meta, DateTimeOffset receivedAt)
    {
        ReportDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ReportDocument>(rawReportJson, ReportWire.Compact);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"body is not valid report JSON: {ex.Message}");
        }
        if (doc is null || doc.Reports is null)
            throw new InvalidOperationException(
                "body is not a Parity report (expected the { schemaVersion, reports } envelope that `parity check` writes).");
        if (doc.SchemaVersion > ReportDocument.CurrentSchemaVersion)
            throw new SchemaTooNewException(doc.SchemaVersion, ReportDocument.CurrentSchemaVersion);

        var run = new Run
        {
            Id = Guid.NewGuid(),
            CommitSha = meta.CommitSha,
            Branch = meta.Branch,
            TriggeredBy = meta.TriggeredBy,
            RepoUrl = meta.RepoUrl,
            CreatedAt = receivedAt,
            Score = FidelityScore.Compute(doc.Reports),
            // gate:優先用 push 端帶來的真實結果(見 RunMetadata);沒有才退回預設口徑
            GateFailed = meta.GateFailed
                ?? doc.Reports.Any(r => r.Summary.Critical > 0 || r.Summary.Serious > 0),
            RawReportGzip = ReportBlob.Compress(rawReportJson),
        };

        foreach (var report in doc.Reports)
        {
            var page = new PageResult
            {
                Id = Guid.NewGuid(),
                Route = report.Route,
                Url = report.Url,
                Score = FidelityScore.Compute([report]),
                DesignNodes = report.Summary.DesignNodes,
                Matched = report.Summary.Matched,
                Unmatched = report.Summary.Unmatched,
                NodesWithDiffs = report.Summary.NodesWithDiffs,
                Critical = report.Summary.Critical,
                Serious = report.Summary.Serious,
                Medium = report.Summary.Medium,
                Minor = report.Summary.Minor,
                MaxSeverity = report.Summary.MaxSeverity.ToString().ToLowerInvariant(),
            };

            // 逐條落差刻意不進關聯表(見 Entities.cs 的說明)——原文 blob 就是落差的真相源
            run.Pages.Add(page);
        }
        return run;
    }
}

/// <summary>報告比伺服器新——不是壞資料,是版本落差,要跟 400 分開講。</summary>
public sealed class SchemaTooNewException(int got, int supported) : Exception(
    $"report schemaVersion {got} is newer than this server supports ({supported}) — " +
    "the report was produced by a newer Parity CLI. Upgrade the server, or pin the CLI version in CI.")
{
    public int Got { get; } = got;
    public int Supported { get; } = supported;
}
