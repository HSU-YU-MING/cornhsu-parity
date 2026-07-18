using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Engine.DesignSources.Snapshot;

/// <summary>
/// 把實作端擷取的 RenderedNode 樹「凍結」成 DesignNode 樹(`parity snapshot` 的核心)。
/// 用途:重構/改版守門——「現在的畫面是對的」,存成設計基準,之後 check 保證不跑版。
///
/// 轉換原則:
///   - Id = CSS selector → 之後配對走 Matcher 的 selector 身分關,100% 確定性、不靠猜
///   - 有自有文字 → TEXT(字體 stack 只取第一個字型,與 CompareFontFamily「stack 含它即過」相容)
///   - fill:TEXT 用文字色;其他用**自身**背景(透明就 null → 不比;不把祖先色烙進每個節點)
///   - padding 全零 → null(該節點的子層才吃得到位置比對;非零照存 → padding 回歸抓得到)
/// </summary>
public static class SnapshotBuilder
{
    /// <summary>
    /// 把一個擷取樹包成可放進設計檔的 frame(id = route,配 config 的 target.frame)。
    /// frame 的 W/H 記「拍照當時的視窗尺寸」而不是 body 尺寸——check 用 frame 尺寸開視窗,
    /// 記 body 尺寸會自我參照(視窗縮成 body 寬 → 捲軸又吃掉 16px、100vh 變成整頁高)導致必然落差。
    /// </summary>
    public static DesignNode ToFrame(RenderedNode body, string frameId, int? viewportW = null, int? viewportH = null)
        => Convert(body) with
        {
            Id = frameId,
            Name = frameId,
            Type = DesignNodeType.Frame,
            Box = new Box(body.Box.X, body.Box.Y, viewportW ?? body.Box.W, viewportH ?? body.Box.H),
        };

    private static DesignNode Convert(RenderedNode r)
    {
        var isText = !string.IsNullOrWhiteSpace(r.Text);
        var pad = r.Padding;
        var hasPad = pad.Top != 0 || pad.Right != 0 || pad.Bottom != 0 || pad.Left != 0;

        return new DesignNode(
            Id: r.Selector,
            Name: r.DomId ?? FirstClass(r.Classes) ?? r.Tag,
            Type: isText ? DesignNodeType.Text : DesignNodeType.Frame,
            Box: r.Box,
            Fill: isText ? r.Color : (r.Background is { IsTransparent: false } bg ? bg : null),
            Text: isText ? FirstFamily(r.Typography) : null,
            Padding: hasPad ? pad : null,
            ItemSpacing: null, // 位置比對 + padding 已覆蓋;不重複記
            CornerRadius: r.CornerRadius,
            Children: r.Children.Select(Convert).ToList())
        {
            Characters = isText ? r.Text : null,
        };
    }

    private static string? FirstClass(string? classes)
        => classes?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static Typography? FirstFamily(Typography? t)
        => t is null ? null : t with
        {
            FontFamily = t.FontFamily?.Split(',')[0].Trim().Trim('"', '\''),
        };
}
