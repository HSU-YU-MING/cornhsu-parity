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

    /// <summary>
    /// 連拍實測隨機 id(F2 殘留的根治):同一個 URL 載入兩次,id 出現在兩邊的 = 穩定,
    /// 只出現在單邊的 = 每次載入重新生成(隨機 id 第二次載入必是另一串,不可能重現)。
    /// 純字元啟發法(looksRandom)分不出 rkxvdnnzty 與 navigation,實測分得出——零誤判:
    /// 穩定 id 絕不會被誤標(它兩次都在)。代價的邊界誠實列:動態出現/消失的元素
    /// (隨機顯示的公告)其穩定 id 會被漏收 → 該元素走結構路徑,錨點次穩但不誤配。
    /// 回傳 (穩定 id 清單, 單邊 id 數);單邊數 0 = 此頁沒有隨機 id,不需要白名單。
    /// </summary>
    public static (IReadOnlyList<string> Stable, int UnstableCount) ProbeStableIds(
        RenderedNode first, RenderedNode second)
    {
        var ids1 = CollectIds(first);
        var ids2 = CollectIds(second);
        var stable = ids1.Intersect(ids2, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var unstable = ids1.Count + ids2.Count - stable.Count * 2;
        return (stable, unstable);
    }

    /// <summary>
    /// 擷取樹上所有節點的 DomId。這對「selector 錨點會用到的 id」是完備的:
    /// 走訪由上而下、每個經過的元素都輸出節點,任何出現在 selector 裡的祖先 id
    /// 必屬於某個被輸出的節點(display:none 子樹與 ignore 子樹整塊不走,其 id 也進不了 selector)。
    /// </summary>
    public static IReadOnlySet<string> CollectIds(RenderedNode tree)
        => tree.DescendantsAndSelf()
            .Select(n => n.DomId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

    private static string? FirstClass(string? classes)
        => classes?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static Typography? FirstFamily(Typography? t)
        => t is null ? null : t with
        {
            FontFamily = t.FontFamily?.Split(',')[0].Trim().Trim('"', '\''),
        };
}
