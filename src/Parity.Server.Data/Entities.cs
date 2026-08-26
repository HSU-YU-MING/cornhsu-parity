namespace Parity.Server.Data;

/// <summary>
/// 網頁外殼的資料模型(網頁外殼規畫書 5.3)。M1 只有 Project → Run → PageResult → Diff;
/// Organization / Membership 是 M3 的事,到時候加 migration,不預先鋪空桌。
/// Diff 的欄位直接對齊 report.json 的形狀(1.0 凍結面 4)——報告格式就是伺服器與引擎的契約,
/// 不重新發明。severity / matchedBy 這些字彙存字串:reason/matchedBy 是開放集合,
/// 存 enum 會讓「引擎長出新值」變成伺服器的 migration。
/// </summary>
public class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    /// <summary>API token 的 SHA-256(hex)。token 本體只在建立當下印一次,不落庫。</summary>
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Run> Runs { get; set; } = [];
}

public class Run
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public string? TriggeredBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>整體還原度分數(FidelityScore.Compute,與 CLI 印的同一個數字)。</summary>
    public int Score { get; set; }
    public bool GateFailed { get; set; }

    /// <summary>
    /// 收到的 report.json 原文,gzip 壓縮(ReportBlob;解壓即逐位元原文)。
    /// 詳情頁(M2)直接從這裡重繪,不受關聯表映射的取捨影響。
    /// 壓縮的理由:規畫書 5.4 原推定「數值報告很小」,實測 21 頁站一次 10.3MB——
    /// gzip 約 10:1,先省回一個數量級;保留策略等真實使用量再定。
    /// </summary>
    public required byte[] RawReportGzip { get; set; }

    public List<PageResult> Pages { get; set; } = [];
}

public class PageResult
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Run? Run { get; set; }

    public required string Route { get; set; }
    public required string Url { get; set; }
    public int Score { get; set; }

    // summary 逐欄對齊 report.json 的 summary
    public int DesignNodes { get; set; }
    public int Matched { get; set; }
    public int Unmatched { get; set; }
    public int NodesWithDiffs { get; set; }
    public int Critical { get; set; }
    public int Serious { get; set; }
    public int Medium { get; set; }
    public int Minor { get; set; }
    public required string MaxSeverity { get; set; }
}

// 刻意沒有 Diff 表(M4 實測後拿掉):36,058 列 / 9.95MB——把報告原文 gzip 省下的
// 原樣吃回來,而且**零讀者**(總覽/趨勢讀 PageResult,詳情頁讀原文 blob)。
// 未來要做「跨 run 追一條 selector 的歷史」時再加表——原文都在,migration + 回填即可。
