using System.Text.Json.Serialization;
using Parity.Engine.Model;

namespace Parity.Engine.DesignSources;

public enum DesignNodeType { Frame, Group, Text, Shape, Component, Instance, Other }

/// <summary>從設計來源正規化出的節點(規畫書 4.3)。Box 相對 frame 原點。</summary>
public sealed record DesignNode(
    string Id,
    string Name,
    DesignNodeType Type,
    Box Box,
    Rgba? Fill,
    Typography? Text,
    Insets? Padding,
    double? ItemSpacing,
    double? CornerRadius,
    IReadOnlyList<DesignNode> Children)
{
    /// <summary>TEXT 節點的文字內容(自動配對的天然錨點,規畫書 4.7)。</summary>
    public string? Characters { get; init; }

    /// <summary>auto-layout 主軸方向:"HORIZONTAL" / "VERTICAL"(比 itemSpacing 用)。</summary>
    public string? LayoutMode { get; init; }

    /// <summary>
    /// auto-layout 水平尺寸模式:"FIXED" / "HUG" / "FILL"。
    /// HUG(隨內容)/FILL(隨父層)時,寬度由內容或版面決定,不是設計約束——比了會因
    /// Figma 文字寬 ≠ 瀏覽器渲染寬而狂誤報。只有 FIXED(或未知)才比寬度。
    /// </summary>
    public string? LayoutSizingHorizontal { get; init; }

    /// <summary>auto-layout 垂直尺寸模式;語意同水平,決定要不要比高度。</summary>
    public string? LayoutSizingVertical { get; init; }

    // 下面這些是 2.x 的選填加法:沒有值就不寫出來,既有的快照才會逐位元相同(凍結面 6;SnapshotWireTests)

    /// <summary>auto-layout 會不會換行:"WRAP" / "NO_WRAP"。會換行的容器,子元件順序不比(第一版)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutWrap { get; init; }

    /// <summary>"ABSOLUTE" = 這個子節點不參與父層的 auto-layout,順序與對齊都不算它。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutPositioning { get; init; }

    /// <summary>auto-layout 主軸對齊:"MIN" / "CENTER" / "MAX" / "SPACE_BETWEEN"。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryAxisAlignItems { get; init; }

    /// <summary>auto-layout 交叉軸對齊:"MIN" / "CENTER" / "MAX" / "BASELINE"。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CounterAxisAlignItems { get; init; }

    /// <summary>節點自己的透明度(0–1)。祖先的不累積:祖先差了,報在祖先身上一次就好。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Opacity { get; init; }

    /// <summary>邊框四邊的粗細。null = 不知道(不比);沒有邊框是四邊都 0。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Insets? BorderWidth { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Rgba? BorderColor { get; init; }

    /// <summary>陰影。null = 不知道(不比);空清單 = 設計上沒有陰影。文字節點是 text-shadow,其他是 box-shadow。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ShadowSpec>? Shadows { get; init; }

    /// <summary>漸層填色。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradientSpec? Gradient { get; init; }

    /// <summary>文字行數(快照量到的)。Figma 與手寫 JSON 沒有這欄,用「框高 ÷ 行高」算。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LineCount { get; init; }

    /// <summary>
    /// Figma 文字框的縮放方式:"NONE"(固定高度)/ "HEIGHT" / "WIDTH_AND_HEIGHT" / "TRUNCATE"。
    /// 固定高度時框高跟行數無關,不能拿來算行數。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextAutoResize { get; init; }

    /// <summary>走訪自己與所有子孫。</summary>
    public IEnumerable<DesignNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children ?? [])
            foreach (var d in child.DescendantsAndSelf())
                yield return d;
    }
}

/// <summary>指向某個設計 frame 的參照。Figma 時 Source=fileKey;本機 JSON 時 Source=檔案路徑。</summary>
public sealed record DesignRef(string Source, string NodeId);

/// <summary>設計來源抽象——留給 XD/Sketch/PNG 的門(規畫書 4.3)。</summary>
public interface IDesignSource
{
    Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default);
}
