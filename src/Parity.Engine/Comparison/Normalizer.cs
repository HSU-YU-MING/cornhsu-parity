using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Engine.Comparison;

/// <summary>
/// 座標正規化(規畫書 4.6):兩邊座標都換算到「相對各自 root 原點」。
/// 目標是後續算出「相對度量」(自身尺寸、內距、gap),不是拿絕對 x/y 直接比。
/// 這層算錯整份報告全錯,所以抽成獨立、可單元測試的純函式。
/// </summary>
public static class Normalizer
{
    /// <summary>設計樹:所有 Box 減掉 root 原點(Figma absoluteBoundingBox 是檔案絕對座標)。</summary>
    public static DesignNode NormalizeDesign(DesignNode root)
        => Shift(root, root.Box.X, root.Box.Y);

    private static DesignNode Shift(DesignNode node, double dx, double dy)
        => node with
        {
            Box = new Box(node.Box.X - dx, node.Box.Y - dy, node.Box.W, node.Box.H),
            Children = (node.Children ?? []).Select(c => Shift(c, dx, dy)).ToList(),
        };

    /// <summary>實作樹:所有 Box 減掉 root(body)原點。擷取端已加回 scroll 位移。</summary>
    public static RenderedNode NormalizeRendered(RenderedNode root)
        => Shift(root, root.Box.X, root.Box.Y);

    private static RenderedNode Shift(RenderedNode node, double dx, double dy)
        => node with
        {
            Box = new Box(node.Box.X - dx, node.Box.Y - dy, node.Box.W, node.Box.H),
            Children = (node.Children ?? []).Select(c => Shift(c, dx, dy)).ToList(),
        };

    /// <summary>把 "16px" / "16.5px" / "normal" 這類 CSS 長度字串轉成數字;無法解析回傳 null。</summary>
    public static double? ParseCssPx(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) value = value[..^2];
        return double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
