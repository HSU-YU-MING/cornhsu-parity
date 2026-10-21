namespace Parity.Engine;

// report.json 的型別(1.0 凍結面 4)。住在 Parity.Report 這個不含擷取端的組件,
// 命名空間沿用 Parity.Engine——見 Parity.Report.csproj 的說明。

/// <summary>落差嚴重度。</summary>
public enum Severity { None = 0, Minor = 1, Medium = 2, Serious = 3, Critical = 4 }

public enum DiffStatus { Mismatch, Missing }

/// <summary>單一屬性的落差:「內距 8px,設計 12px」精確到數字(規畫書 1)。</summary>
public sealed record PropDiff(
    string Prop,
    string Expected,
    string Actual,
    string? Unit,
    double? Delta,
    double Tolerance,
    Severity Severity,
    DiffStatus Status = DiffStatus.Mismatch,
    bool Soft = false);

/// <summary>一個配對成功的設計節點的比對結果。兩個框給疊圖視圖用(M3)。</summary>
public sealed record NodeResult(
    string DesignLayer,
    string DesignId,
    string Selector,
    string MatchedBy,
    Severity Severity,
    IReadOnlyList<PropDiff> Diffs,
    Model.Box DesignBox = default,
    Model.Box RenderedBox = default);

/// <summary>只在設計端找得到、配不到實作的節點——誠實列出(規畫書 4.7)。</summary>
public sealed record UnmatchedNode(string DesignLayer, string DesignId, string Reason, Model.Box DesignBox = default);

public sealed record ReportSummary(
    int DesignNodes,
    int Matched,
    int Unmatched,
    int NodesWithDiffs,
    int Critical,
    int Serious,
    int Medium,
    int Minor,
    Severity MaxSeverity);

/// <summary>引擎輸出:可序列化成 JSON / 餵給 UI / 決定 exit code(規畫書 4.1)。</summary>
public sealed record FidelityReport(
    string Route,
    string Url,
    string DesignReference,
    IReadOnlyList<NodeResult> Nodes,
    IReadOnlyList<UnmatchedNode> Unmatched,
    ReportSummary Summary);

/// <summary>
/// report.json 的頂層信封(0.10.0 起;1.0 凍結面 4)。裸陣列無從辨識版本;包一層
/// schemaVersion,報告格式演進時消費端(parity report / serve / **Parity.Server**)有據可判。
/// 原住在 Parity.Cli;網頁外殼動工(2026-08-26)時移入引擎,2026-10-22 再搬進 Parity.Report
/// (伺服器不准引用含擷取端的引擎)——報告契約的每一個消費者都該對同一個型別,而不是各抄一份。
/// 兩次搬家 wire 格式都逐位元不變。
/// </summary>
public sealed record ReportDocument(int SchemaVersion, IReadOnlyList<FidelityReport> Reports)
{
    public const int CurrentSchemaVersion = 1;

    public static ReportDocument Of(IReadOnlyList<FidelityReport> reports) => new(CurrentSchemaVersion, reports);
}
