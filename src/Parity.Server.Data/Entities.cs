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
    /// 收到的 report.json 原文(逐位元)。詳情頁(M2)直接從這裡重繪,
    /// 不受關聯表映射的取捨影響;數值報告很小,永久保留無妨(規畫書 5.4)。
    /// </summary>
    public required string RawReportJson { get; set; }

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

    public List<Diff> Diffs { get; set; } = [];
}

public class Diff
{
    public Guid Id { get; set; }
    public Guid PageResultId { get; set; }
    public PageResult? PageResult { get; set; }

    public required string DesignLayer { get; set; }
    public required string DesignId { get; set; }
    public required string Selector { get; set; }
    public required string MatchedBy { get; set; }
    public required string Prop { get; set; }
    public required string Expected { get; set; }
    public required string Actual { get; set; }
    public string? Unit { get; set; }
    public double? Delta { get; set; }
    public double Tolerance { get; set; }
    public required string Severity { get; set; }
    public required string Status { get; set; }
    public bool Soft { get; set; }
}
