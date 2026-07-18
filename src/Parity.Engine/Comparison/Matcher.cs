using System.Text;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;

namespace Parity.Engine.Comparison;

public sealed record NodePair(DesignNode Design, RenderedNode Rendered, string MatchedBy);

public sealed record MatchResult(
    IReadOnlyList<NodePair> Pairs,
    IReadOnlyList<UnmatchedNode> Unmatched);

/// <summary>
/// 節點配對(規畫書 4.7)——以設計端為錨,自動優先、只補漏:
///   1. 手動錨點(map 檔或 data-parity,擷取端已標成 ExplicitMatch)→ 最高優先
///   2. 文字錨點:TEXT 節點文字 ↔ 頁面元素文字(唯一才配;多個同文字時用圖層名消歧,仍不硬湊)
///   3. 圖層名 ↔ DOM id / class / aria-label / data-testid
///   4. 容器推論:配不到的容器,用「已配對子孫的最近共同祖先(LCA)」反推——純結構、不猜
/// 配不到 → 誠實進未配對清單,不假裝全對上。
/// </summary>
public static class Matcher
{
    public static MatchResult Match(DesignNode designRoot, RenderedNode renderedRoot)
    {
        // 設計端為錨:報告長度 = 設計節點數,有界又有意義(不反向掃整頁)
        var designNodes = designRoot.DescendantsAndSelf().Skip(1).ToList(); // 跳過 root frame 本身
        var candidates = renderedRoot.DescendantsAndSelf().ToList();

        var pairs = new List<NodePair>();
        var unmatched = new List<UnmatchedNode>();
        var taken = new HashSet<RenderedNode>(ReferenceEqualityComparer.Instance);
        // 設計節點 Id → 已配到的實作節點,供第 4 關算 LCA
        var matchedByDesign = new Dictionary<string, RenderedNode>();

        void Pair(DesignNode d, RenderedNode r, string by)
        {
            taken.Add(r);
            matchedByDesign[d.Id] = r;
            pairs.Add(new NodePair(d, r, by));
        }

        // --- 第 0 關:selector 身分(snapshot 設計來源:設計節點 Id 就是擷取時的 CSS selector)---
        // 100% 確定性——同一個 selector 就是同一個元素。Figma id("10:2")長得不像 selector,不會誤中。
        var bySelector = new Dictionary<string, RenderedNode>();
        foreach (var r in candidates)
            bySelector.TryAdd(r.Selector, r);

        // --- 第 1 關:手動錨點(ExplicitMatch = data-parity 或 map 檔),對圖層名 ---
        var explicitByName = new Dictionary<string, RenderedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in candidates.Where(r => !string.IsNullOrEmpty(r.ExplicitMatch)))
            explicitByName.TryAdd(r.ExplicitMatch!, r);

        var remaining = new List<DesignNode>();
        foreach (var d in designNodes)
        {
            if (bySelector.TryGetValue(d.Id, out var same) && !taken.Contains(same))
                Pair(d, same, "selector");
            else if (explicitByName.TryGetValue(d.Name, out var hit) && !taken.Contains(hit))
                Pair(d, hit, "explicit");
            else
                remaining.Add(d);
        }

        // --- 第 2 關:文字錨點(主力,不用人做任何事)---
        var byText = candidates
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .GroupBy(r => NormalizeText(r.Text!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var stillRemaining = new List<DesignNode>();
        foreach (var d in remaining)
        {
            if (!string.IsNullOrWhiteSpace(d.Characters)
                && byText.TryGetValue(NormalizeText(d.Characters!), out var hits))
            {
                var free = hits.Where(h => !taken.Contains(h)).ToList();
                if (free.Count == 1) // 唯一才配
                {
                    Pair(d, free[0], "auto-text");
                    continue;
                }
                // 多個同文字:若剛好一個候選的 id/class/aria 對得上圖層名 → 用它消歧
                // (高精準;沒有明確線索就不猜位置,避免配錯 → 產生假落差)
                var key = NormalizeName(d.Name);
                if (free.Count > 1 && key.Length > 0)
                {
                    var named = free.Where(h => NameMatches(h, key)).ToList();
                    if (named.Count == 1)
                    {
                        Pair(d, named[0], "auto-text");
                        continue;
                    }
                }
            }
            stillRemaining.Add(d);
        }

        // --- 第 3 關:圖層名 ↔ id / class / aria-label ---
        var pending = new List<(DesignNode Node, string Reason)>();
        foreach (var d in stillRemaining)
        {
            var key = NormalizeName(d.Name);
            if (key.Length == 0)
            {
                pending.Add((d, "no-anchor"));
                continue;
            }

            var hit = candidates.FirstOrDefault(r =>
                !taken.Contains(r) && NameMatches(r, key));

            if (hit is not null)
                Pair(d, hit, "auto-name");
            else
                pending.Add((d, !string.IsNullOrWhiteSpace(d.Characters) ? "ambiguous-or-missing-text" : "no-anchor"));
        }

        // --- 第 4 關:容器推論——已配到 ≥2 個子孫的容器,對應到那些子孫的最近共同祖先 ---
        // 純結構推論(不比對外觀、不猜),把「無文字/名字對不上」但內容配對得上的容器救回來。
        var chainOf = BuildAncestorChains(renderedRoot);
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var d = pending[i].Node;
                if (d.Children is null || d.Children.Count == 0) continue;

                var matchedDesc = d.DescendantsAndSelf().Skip(1)
                    .Select(c => matchedByDesign.TryGetValue(c.Id, out var rn) ? rn : null)
                    .Where(rn => rn is not null).Select(rn => rn!).ToList();
                if (matchedDesc.Count < 2) continue;

                var lca = LowestCommonAncestor(matchedDesc, chainOf);
                if (lca is null || ReferenceEquals(lca, renderedRoot) || taken.Contains(lca)) continue;

                Pair(d, lca, "auto-container");
                pending.RemoveAt(i);
                progressed = true;
            }
        }

        foreach (var (node, reason) in pending)
            unmatched.Add(new UnmatchedNode(node.Name, node.Id, reason, node.Box));

        return new MatchResult(pairs, unmatched);
    }

    /// <summary>每個實作節點 → 從 root 到自己的祖先鏈(含自己),供算 LCA。</summary>
    private static Dictionary<RenderedNode, IReadOnlyList<RenderedNode>> BuildAncestorChains(RenderedNode root)
    {
        var map = new Dictionary<RenderedNode, IReadOnlyList<RenderedNode>>(ReferenceEqualityComparer.Instance);
        void Walk(RenderedNode n, List<RenderedNode> prefix)
        {
            var chain = new List<RenderedNode>(prefix) { n };
            map[n] = chain;
            foreach (var c in n.Children ?? []) Walk(c, chain);
        }
        Walk(root, []);
        return map;
    }

    /// <summary>一組實作節點的最近共同祖先 = 各自祖先鏈的最長共同前綴的末端。</summary>
    private static RenderedNode? LowestCommonAncestor(
        List<RenderedNode> nodes, Dictionary<RenderedNode, IReadOnlyList<RenderedNode>> chainOf)
    {
        if (nodes.Count == 0) return null;
        var common = chainOf[nodes[0]];
        var len = common.Count;
        foreach (var n in nodes.Skip(1))
        {
            var chain = chainOf[n];
            var i = 0;
            var max = Math.Min(len, chain.Count);
            while (i < max && ReferenceEquals(common[i], chain[i])) i++;
            len = i;
        }
        return len > 0 ? common[len - 1] : null;
    }

    private static bool NameMatches(RenderedNode r, string normalizedLayerName)
    {
        if (r.DomId is not null && NormalizeName(r.DomId) == normalizedLayerName) return true;
        if (r.AriaLabel is not null && NormalizeName(r.AriaLabel) == normalizedLayerName) return true;
        if (r.Classes is not null)
            foreach (var cls in r.Classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (NormalizeName(cls) == normalizedLayerName) return true;
        return false;
    }

    /// <summary>文字正規化:壓空白、不分大小寫。</summary>
    internal static string NormalizeText(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>名稱正規化:只留字母數字、小寫。"CTA Button" == "cta-button" == "ctaButton"。</summary>
    internal static string NormalizeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
