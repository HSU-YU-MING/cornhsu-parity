using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;

namespace Parity.Engine;

/// <summary>一次掃描的輸入:設計參照 + 實作參照(規畫書 4.1)。</summary>
public sealed record ScanRequest(DesignRef Design, ImplRef Impl, string Route = "/");

/// <summary>每維度容差,預設非零(規畫書 4.8:子像素與渲染差異必然存在)。</summary>
public sealed record Tolerances(
    double SizePx = 2.0,
    double SpacingPx = 2.0,
    double ColorDeltaE = 2.0,
    double FontSizePx = 0.5);

public sealed record EngineOptions(Tolerances Tolerances)
{
    public static EngineOptions Default { get; } = new(new Tolerances());
}

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
