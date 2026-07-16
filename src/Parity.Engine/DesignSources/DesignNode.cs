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
