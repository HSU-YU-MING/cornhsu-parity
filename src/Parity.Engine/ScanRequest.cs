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
    double FontSizePx = 0.5,
    // 位置容差比尺寸鬆:flow 版面裡文字行高的自然差異會讓後續元素有 2–4px 漂移
    double PositionPx = 4.0);

public sealed record EngineOptions(Tolerances Tolerances)
{
    public static EngineOptions Default { get; } = new(new Tolerances());

    /// <summary>相對位置比對(offsetX/offsetY)。compare.position = "none" 時關閉。</summary>
    public bool ComparePosition { get; init; } = true;

    /// <summary>
    /// 列出實作端多出來的元件(compare.extras = "list")。預設關閉:報告的 <c>extras</c> 是 null。
    /// 只是清單,開了也不影響 gate、分數與 summary(<see cref="Comparison.ExtrasFinder"/>)。
    /// </summary>
    public bool ListExtras { get; init; }

    /// <summary>文字內容比對(compare.text)。預設 <see cref="TextComparison.Soft"/>:會列出、不扣分、不擋。</summary>
    public TextComparison CompareText { get; init; } = TextComparison.Soft;
}

/// <summary>文字內容比對的三種模式。</summary>
public enum TextComparison
{
    /// <summary>不比。</summary>
    None,

    /// <summary>軟落差:會列出、不扣分、不擋、<c>--baseline</c> 不算新增。快照基準(selector 身分配對)不比:動態內容每次都不同。</summary>
    Soft,

    /// <summary>硬落差(serious):會擋 PR。快照基準也比。</summary>
    Gate,
}
