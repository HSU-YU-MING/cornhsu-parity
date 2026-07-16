using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;

namespace Parity.Engine;

/// <summary>
/// 引擎對外唯一進入點(規畫書 4.1)。
/// 兩邊都是插進來的 adapter:設計來源 + 實作來源;引擎只比對「兩棵正規化的樹」,不管樹從哪來。
/// 外殼(CLI / 本機 UI / CI / 雲端)只做「準備 ScanRequest → RunAsync → 處理 FidelityReport」。
/// </summary>
public sealed class FidelityEngine(IDesignSource design, IImplementationSource impl, EngineOptions opts)
{
    private readonly IDesignSource _design = design ?? throw new ArgumentNullException(nameof(design));
    private readonly IImplementationSource _impl = impl ?? throw new ArgumentNullException(nameof(impl));
    private readonly EngineOptions _opts = opts ?? EngineOptions.Default;

    public async Task<FidelityReport> RunAsync(ScanRequest req, CancellationToken ct = default)
        => (await RunDetailedAsync(req, ct)).Report;

    /// <summary>報告 + 兩棵正規化的樹 + 實作端原始原點——本機 UI 的疊圖與 hit-test 用(M3)。</summary>
    public async Task<ScanResult> RunDetailedAsync(ScanRequest req, CancellationToken ct = default)
    {
        // 1. 設計來源 → DesignNode 樹 → 正規化(相對 frame 原點)
        var designTree = await _design.GetFrameAsync(req.Design, ct);
        designTree = Normalizer.NormalizeDesign(designTree);

        // 2. 實作來源 → RenderedNode 樹;視窗大小 = frame 尺寸(規畫書:在 frame 設計寬度下渲染)
        var implRef = req.Impl;
        if (implRef.ViewportWidth is null)
            implRef = implRef with
            {
                ViewportWidth = (int)Math.Ceiling(designTree.Box.W),
                ViewportHeight = (int)Math.Ceiling(designTree.Box.H),
            };
        var renderedTree = await _impl.CaptureAsync(implRef, ct);
        var renderedOrigin = renderedTree.Box; // body 在頁面上的原始位置(截圖疊框要加回這個位移)
        renderedTree = Normalizer.NormalizeRendered(renderedTree);

        // 3. 配對(設計端為錨)
        var match = Matcher.Match(designTree, renderedTree);

        // 4. 數值 diff + 容差
        var diffEngine = new DiffEngine(_opts.Tolerances);
        var nodes = match.Pairs.Select(diffEngine.Diff).ToList();

        // 5. 彙總
        var withDiffs = nodes.Where(n => n.Diffs.Count > 0).ToList();
        var summary = new ReportSummary(
            DesignNodes: match.Pairs.Count + match.Unmatched.Count,
            Matched: match.Pairs.Count,
            Unmatched: match.Unmatched.Count,
            NodesWithDiffs: withDiffs.Count,
            Critical: Count(Severity.Critical),
            Serious: Count(Severity.Serious),
            Medium: Count(Severity.Medium),
            Minor: Count(Severity.Minor),
            MaxSeverity: nodes.Count == 0 ? Severity.None : nodes.Max(n => n.Severity));

        var report = new FidelityReport(
            req.Route, req.Impl.Url, $"{req.Design.Source}#{req.Design.NodeId}",
            nodes, match.Unmatched, summary);
        return new ScanResult(report, designTree, renderedTree, renderedOrigin);

        int Count(Severity s) => nodes.SelectMany(n => n.Diffs).Count(x => x.Severity == s);
    }
}

/// <summary>RunDetailedAsync 的完整輸出:報告之外,再給 UI 疊圖 / 配對 hit-test 所需的原料。</summary>
public sealed record ScanResult(
    FidelityReport Report,
    DesignNode DesignTree,
    RenderedNode RenderedTree,
    Model.Box RenderedOrigin);
