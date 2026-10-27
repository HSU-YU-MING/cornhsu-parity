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

/// <summary>
/// 一次執行是哪個工具的報告(合併伺服器:一台收兩種,統計功能規劃「合併伺服器」)。存字串不存 enum,
/// 理由同 severity:多一個工具不該變成既有資料的 migration。push 不帶 X-Parity-Tool = parity
/// (2.0.0 的 parity push 不送這個標頭,舊 CLI 照樣推得上來)。
/// </summary>
public static class RunTool
{
    public const string Parity = "parity";
    public const string XamlContrast = "xamlcontrast";

    public static bool IsKnown(string tool) => tool is Parity or XamlContrast;
}

public class Run
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary><see cref="RunTool"/> 的值。決定 RawReportGzip 是哪一種報告、明細掛在哪張表。</summary>
    public required string Tool { get; set; }

    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public string? TriggeredBy { get; set; }
    /// <summary>repo 首頁網址(如 https://github.com/owner/repo,CI 環境自動帶)——UI 把 commit 連回去。</summary>
    public string? RepoUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// 整體還原度分數(FidelityScore.Compute,與 CLI 印的同一個數字)。**只有 parity 的 run 有**——
    /// XamlContrast 沒有「分數」這個量,硬塞一個 0 或 100 會讓總覽與趨勢說謊,所以是 null。
    /// </summary>
    public int? Score { get; set; }
    public bool GateFailed { get; set; }

    /// <summary>
    /// 收到的報告原文,gzip 壓縮(ReportBlob;解壓即逐位元原文)。parity 是 report.json,
    /// xamlcontrast 是 `--json` 的輸出(schemaVersion 6)。
    /// 詳情頁(M2)直接從這裡重繪,不受關聯表映射的取捨影響。
    /// 壓縮的理由:規畫書 5.4 原推定「數值報告很小」,實測 21 頁站一次 10.3MB——
    /// gzip 約 10:1,先省回一個數量級;保留策略等真實使用量再定。
    /// </summary>
    public required byte[] RawReportGzip { get; set; }

    /// <summary>parity 的 run:每頁一列。xamlcontrast 的 run 沒有頁面,永遠是空的。</summary>
    public List<PageResult> Pages { get; set; } = [];

    /// <summary>xamlcontrast 的 run:摘要計數(一對一)。parity 的 run 是 null。</summary>
    public ContrastResult? Contrast { get; set; }
}

/// <summary>
/// XamlContrast 一次稽核的摘要,欄位對齊 `--json` 的 summary(XamlContrast 1.0 凍結的 schemaVersion 6)。
/// 總覽與趨勢讀這張表;逐筆 findings / unresolvedSites 跟 parity 的逐條落差一樣**不進關聯表**,
/// 詳情頁從原文 blob 重建(理由見檔尾:零讀者的列只會把壓縮省下的空間吃回來)。
/// </summary>
public class ContrastResult
{
    public Guid RunId { get; set; }
    public Run? Run { get; set; }

    /// <summary>報告上的 toolVersion(不含 build metadata);經函式庫產生的報告是 null。</summary>
    public string? ToolVersion { get; set; }
    /// <summary>CLI 的稽核判定:0 通過 / 1 低於 gate / 3 結果不可信;經函式庫產生的報告是 null。</summary>
    public int? ExitCode { get; set; }

    public int Files { get; set; }
    public int Pairs { get; set; }
    /// <summary>解得出顏色的配對百分比(一位小數),與 XamlContrast 的 --min-coverage 比的是同一個數。</summary>
    public double Coverage { get; set; }
    public int Unresolved { get; set; }

    // summary.counts
    public int Fail { get; set; }
    public int Warn { get; set; }
    public int Ok { get; set; }
    public int Decorative { get; set; }

    public int Suppressed { get; set; }
    public int ParseErrors { get; set; }

    // 開放集合(XamlContrast 1.x 可能長出新值)→ 存字串
    public required string PaletteMode { get; set; }
    public bool PaletteDegraded { get; set; }
    public required string FailOn { get; set; }
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
