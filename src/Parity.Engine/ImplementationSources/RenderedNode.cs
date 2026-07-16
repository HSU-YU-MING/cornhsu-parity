using Parity.Engine.Model;

namespace Parity.Engine.ImplementationSources;

/// <summary>從實作端讀出的節點(規畫書 4.5)。網頁來自 DOM,未來桌面來自 UI 樹。</summary>
public sealed record RenderedNode(
    string Selector,
    string Tag,
    string? Text,
    Box Box,
    Rgba? Color,
    Rgba? Background,
    Typography? Typography,
    Insets Padding,
    IReadOnlyList<RenderedNode> Children)
{
    /// <summary>手動錨點:data-parity 屬性值,或 map 檔選中的圖層名(規畫書 4.7)。</summary>
    public string? ExplicitMatch { get; init; }

    /// <summary>沿祖先鏈找到的第一個不透明背景(避免 transparent 誤報)。</summary>
    public Rgba? EffectiveBackground { get; init; }

    public string? DomId { get; init; }
    public string? Classes { get; init; }
    public string? AriaLabel { get; init; }
    public double? CornerRadius { get; init; }

    public IEnumerable<RenderedNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children ?? [])
            foreach (var d in child.DescendantsAndSelf())
                yield return d;
    }
}

/// <summary>指向實作端某個畫面的參照。網頁=URL;視窗大小由引擎依 frame 尺寸補上(規畫書 4.5)。</summary>
public sealed record ImplRef(
    string Url,
    int? ViewportWidth = null,
    int? ViewportHeight = null)
{
    /// <summary>map 檔的「圖層名 → CSS selector」對應,擷取時在頁面內解析並標記。</summary>
    public IReadOnlyDictionary<string, string>? MapSelectors { get; init; }

    /// <summary>要忽略的 selector 清單(如 "[data-parity-ignore]")。</summary>
    public IReadOnlyList<string>? IgnoreSelectors { get; init; }
}

/// <summary>實作來源抽象——留給 WPF/桌面的門(規畫書 4.5)。</summary>
public interface IImplementationSource
{
    Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default);
}
