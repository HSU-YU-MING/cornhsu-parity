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
}
