using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Engine.Comparison;

/// <summary>
/// 實作端多出來的元件(<c>compare.extras: "list"</c>)。配對以設計端為錨(<see cref="Matcher"/>),
/// 頁面上「設計稿根本沒有的東西」原本完全看不見——頁首多一條 800×200 的大紅橫幅,報告照樣 exit 0、沒有任何提示。
/// 這裡在配對之後反向掃一次實作樹,**只列出**同時符合下列條件的元素:
/// <list type="number">
///   <item>沒被配對;</item>
///   <item>不是任何已配對節點的祖先(那些是版面容器,body、wrapper 之類);</item>
///   <item>不在「已配對的葉節點」底下(見 <see cref="CoversSubtree"/>);</item>
///   <item>看得見:自己有文字、是 <c>img</c>/<c>svg</c>、或背景不是全透明;</item>
///   <item>落在視窗裡的面積至少 <see cref="MinArea"/>。</item>
/// </list>
/// 巢狀時只報最外層一個:一個元素入選後不再往下找,同一塊不會報成一長串。
/// 不判斷的:擷取端沒有回傳邊框、陰影、opacity、被祖先 overflow 裁掉或被別的元素蓋住——
/// 只有邊框的元素不會被列出,opacity: 0 的元素可能被列出(README 寫明)。
/// 結果只是清單:不進 gate、分數、summary、baseline 與匿名統計。
/// </summary>
public static class ExtrasFinder
{
    /// <summary>
    /// 面積門檻(px²;算的是落在視窗內的那一塊),至少 64×64。保守取值的理由:
    /// 圖示(16–24px)、1px 分隔線、sr-only(1×1)、追蹤像素全都在門檻下;一行短字(「Login」約 50×20 = 1,000)也在門檻下——
    /// 第一版寧可漏報小東西,也不要每頁冒出幾十條雜訊讓人學會不看。一顆一般的按鈕(120×40 = 4,800)、
    /// 一段文字、一張卡片、一條橫幅都在門檻上。第一版不開放設定:先量過真實網站的雜訊再決定要不要調、要不要開放。
    /// </summary>
    public const double MinArea = 64 * 64;

    /// <summary>報告最多帶幾筆(依面積由大到小);<see cref="ExtraElements.Total"/> 是截斷前的總數。</summary>
    public const int MaxItems = 20;

    /// <param name="root">正規化後的實作樹(body;根視為隱含配對,本身永遠不列)。</param>
    /// <param name="pairs">Matcher 的配對結果。</param>
    /// <param name="viewport">這次渲染的視窗,與 <paramref name="root"/> 同一套座標(相對 body 原點)。</param>
    public static ExtraElements Find(RenderedNode root, IReadOnlyList<NodePair> pairs, Box viewport)
    {
        var designOf = new Dictionary<RenderedNode, DesignNode>(ReferenceEqualityComparer.Instance);
        foreach (var p in pairs) designOf.TryAdd(p.Rendered, p.Design);

        // 子樹裡(含自己)有沒有已配對節點——後序走一次記下來,「是不是已配對節點的祖先」查表就好
        var holdsMatch = new HashSet<RenderedNode>(ReferenceEqualityComparer.Instance);
        bool Mark(RenderedNode n)
        {
            var any = designOf.ContainsKey(n);
            foreach (var c in n.Children ?? []) any |= Mark(c);
            if (any) holdsMatch.Add(n);
            return any;
        }
        Mark(root);

        var found = new List<(RenderedNode Node, double Area)>();
        void Walk(RenderedNode parent)
        {
            foreach (var n in parent.Children ?? [])
            {
                if (designOf.TryGetValue(n, out var design))
                {
                    if (!CoversSubtree(n, design, holdsMatch)) Walk(n);
                    continue;
                }
                if (holdsMatch.Contains(n)) { Walk(n); continue; } // 已配對節點的祖先:版面容器,本身不列
                var area = VisibleArea(n.Box, viewport);
                if (area >= MinArea && IsVisible(n))
                {
                    found.Add((n, area));
                    continue; // 最外層一個就好
                }
                Walk(n); // 透明的 wrapper 本身不算,裡面的東西可能算
            }
        }
        Walk(root);

        var items = found
            .OrderByDescending(f => f.Area) // 穩定排序:同面積照文件順序
            .Take(MaxItems)
            .Select(f => new ExtraElement(f.Node.Selector, f.Node.Tag, f.Node.Box))
            .ToList();
        return new ExtraElements(found.Count, items);
    }

    /// <summary>
    /// 已配對節點的子樹算不算「設計稿已經涵蓋了」。兩種情況都算,底下的東西一律不列:
    /// <list type="bullet">
    ///   <item>設計端是葉節點:設計師說「這是一個東西」(一顆按鈕、一張圖),實作裡面怎麼拆是實作細節;</item>
    ///   <item>子孫一個都沒配到:設計端的子節點都已經在 unmatched 清單裡,再把實作端的內容報成多出來,
    ///   是同一個落差報兩次。</item>
    /// </list>
    /// 子孫有配到的容器不算:配對過的部分之外多出一塊,正是要找的東西。
    /// </summary>
    private static bool CoversSubtree(RenderedNode rendered, DesignNode design, HashSet<RenderedNode> holdsMatch)
        => design.Children is null || design.Children.Count == 0
            || !(rendered.Children ?? []).Any(holdsMatch.Contains);

    /// <summary>看得見:自己有文字、是圖片或 SVG、或背景不是全透明(擷取端回傳的是元素自己的背景,不是沿祖先找的那個)。</summary>
    private static bool IsVisible(RenderedNode n)
        => !string.IsNullOrWhiteSpace(n.Text)
            || n.Tag is "img" or "svg"
            || n.Background is { IsTransparent: false };

    /// <summary>框落在視窗內那一塊的面積;完全在視窗外是 0。</summary>
    internal static double VisibleArea(Box b, Box viewport)
    {
        var w = Math.Min(b.X + b.W, viewport.X + viewport.W) - Math.Max(b.X, viewport.X);
        var h = Math.Min(b.Y + b.H, viewport.Y + viewport.H) - Math.Max(b.Y, viewport.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}
