using Parity.Engine.ImplementationSources;

namespace Parity.Engine.DesignSources.Snapshot;

/// <summary>一個在多次擷取間不穩定的區域。Reason:presence / box / text / color / typography / padding。</summary>
public sealed record UnstableRegion(string Selector, string Reason);

/// <summary>
/// snapshot 的穩定性探測(野生實查 F4):動態內容(廣告輪播、動畫、lazy 媒體)會讓
/// snapshot 拍下「剛好那一刻」,之後每次 check 都在比一個會動的東西——MDN 的廣告版位
/// 貢獻 4 筆 critical、Stripe 兩次 check 差 22 分。與其讓使用者踩到落差才回頭猜哪裡該
/// ignore,不如連拍 N 次、自動列出不穩定的區域,直接給可貼進 config 的 ignore 建議。
/// 純函式:吃 N 棵擷取樹,吐不穩定區域(收攏到最高的不穩定祖先,清單才不會淹沒人)。
/// </summary>
public static class SnapshotStabilizer
{
    /// <summary>
    /// 比對多次擷取,找出不穩定節點並收攏到祖先。tolerancePx:幾何抖動的容許值
    /// (次像素排版抖動不算不穩定;預設 1px,與比對容差無關——這裡抓的是「會動」,不是「不準」)。
    /// </summary>
    public static IReadOnlyList<UnstableRegion> FindUnstable(
        IReadOnlyList<RenderedNode> captures, double tolerancePx = 1.0)
    {
        if (captures.Count < 2) return [];

        var maps = captures
            .Select(c => c.DescendantsAndSelf()
                .GroupBy(n => n.Selector).ToDictionary(g => g.Key, g => g.First()))
            .ToList();

        // 以第一拍為基準;其餘拍次的「新面孔」也要算(presence 是雙向的)
        var selectors = new List<string>(maps[0].Keys);
        var seen = new HashSet<string>(maps[0].Keys);
        foreach (var m in maps.Skip(1))
            foreach (var sel in m.Keys)
                if (seen.Add(sel))
                    selectors.Add(sel);

        var unstable = new List<UnstableRegion>();
        foreach (var sel in selectors)
        {
            string? reason = null;
            RenderedNode? first = null;
            foreach (var m in maps)
            {
                if (!m.TryGetValue(sel, out var node)) { reason = "presence"; break; }
                if (first is null) { first = node; continue; }
                reason = Differs(first, node, tolerancePx);
                if (reason is not null) break;
            }
            if (reason is not null)
                unstable.Add(new UnstableRegion(sel, reason));
        }

        return CollapseToAncestors(unstable);
    }

    /// <summary>把不穩定 selector 轉成可放進 config「ignore」的 CSS selector:
    /// shadow / iframe 內部(含 >>>)querySelectorAll 打不進去,退回宿主元素。</summary>
    public static string ToIgnoreSelector(string selector)
    {
        var i = selector.IndexOf(" >>> ", StringComparison.Ordinal);
        return i >= 0 ? selector[..i] : selector;
    }

    private static string? Differs(RenderedNode a, RenderedNode b, double tol)
    {
        if (Math.Abs(a.Box.X - b.Box.X) > tol || Math.Abs(a.Box.Y - b.Box.Y) > tol ||
            Math.Abs(a.Box.W - b.Box.W) > tol || Math.Abs(a.Box.H - b.Box.H) > tol) return "box";
        if (!string.Equals(a.Text, b.Text, StringComparison.Ordinal)) return "text";
        if (a.Color != b.Color || a.Background != b.Background) return "color";
        if (a.Typography != b.Typography) return "typography";
        if (a.Padding != b.Padding) return "padding";
        return null;
    }

    /// <summary>不穩定的子孫收攏進最高的不穩定祖先——ignore 一個容器,好過 ignore 它的四十個內臟。</summary>
    private static List<UnstableRegion> CollapseToAncestors(List<UnstableRegion> unstable)
    {
        var kept = new List<UnstableRegion>();
        foreach (var u in unstable.OrderBy(u => u.Selector.Length))
        {
            var covered = kept.Any(k =>
                u.Selector.StartsWith(k.Selector + " > ", StringComparison.Ordinal) ||
                u.Selector.StartsWith(k.Selector + " >>> ", StringComparison.Ordinal));
            if (!covered) kept.Add(u);
        }
        return kept;
    }
}
