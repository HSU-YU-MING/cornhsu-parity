using System.Text.Json;
using Parity.Engine;

namespace Parity.Server.Data;

/// <summary>push 上來的一次執行的中繼資料(commit/branch/觸發者,由 CI 環境提供)。</summary>
public sealed record RunMetadata(string? CommitSha, string? Branch, string? TriggeredBy);

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
            CreatedAt = receivedAt,
            Score = FidelityScore.Compute(doc.Reports),
            // gate 判定屬於 config(failOn 可自訂),伺服器只看報告——
            // 有任何 critical/serious 即視為紅,與預設 gate 同口徑;M2 若要精確可隨 push 傳 gate 結果
            GateFailed = doc.Reports.Any(r => r.Summary.Critical > 0 || r.Summary.Serious > 0),
            RawReportJson = rawReportJson,
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

            foreach (var node in report.Nodes)
                foreach (var diff in node.Diffs)
                    page.Diffs.Add(new Diff
                    {
                        Id = Guid.NewGuid(),
                        DesignLayer = node.DesignLayer,
                        DesignId = node.DesignId,
                        Selector = node.Selector,
                        MatchedBy = node.MatchedBy,
                        Prop = diff.Prop,
                        Expected = diff.Expected,
                        Actual = diff.Actual,
                        Unit = diff.Unit,
                        Delta = diff.Delta,
                        Tolerance = diff.Tolerance,
                        Severity = diff.Severity.ToString().ToLowerInvariant(),
                        Status = diff.Status.ToString().ToLowerInvariant(),
                        Soft = diff.Soft,
                    });

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
