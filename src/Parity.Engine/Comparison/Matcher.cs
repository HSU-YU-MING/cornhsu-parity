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
///   2. 文字錨點:TEXT 節點文字 ↔ 頁面元素文字(唯一才配,模稜兩可不硬湊)
///   3. 圖層名 ↔ DOM id / class / aria-label / data-testid
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

        // --- 第 1 關:手動錨點(ExplicitMatch = data-parity 或 map 檔),對圖層名 ---
        var explicitByName = new Dictionary<string, RenderedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in candidates.Where(r => !string.IsNullOrEmpty(r.ExplicitMatch)))
            explicitByName.TryAdd(r.ExplicitMatch!, r);

        var remaining = new List<DesignNode>();
        foreach (var d in designNodes)
        {
            if (explicitByName.TryGetValue(d.Name, out var hit) && taken.Add(hit))
                pairs.Add(new NodePair(d, hit, "explicit"));
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
                if (free.Count == 1) // 唯一才配;多個相同文字 → 交給下一關或未配對,不硬湊
                {
                    taken.Add(free[0]);
                    pairs.Add(new NodePair(d, free[0], "auto-text"));
                    continue;
                }
            }
            stillRemaining.Add(d);
        }

        // --- 第 3 關:圖層名 ↔ id / class / aria-label ---
        foreach (var d in stillRemaining)
        {
            var key = NormalizeName(d.Name);
            if (key.Length == 0)
            {
                unmatched.Add(new UnmatchedNode(d.Name, d.Id, "no-anchor"));
                continue;
            }

            var hit = candidates.FirstOrDefault(r =>
                !taken.Contains(r) && NameMatches(r, key));

            if (hit is not null)
            {
                taken.Add(hit);
                pairs.Add(new NodePair(d, hit, "auto-name"));
            }
            else
            {
                var reason = !string.IsNullOrWhiteSpace(d.Characters) ? "ambiguous-or-missing-text" : "no-anchor";
                unmatched.Add(new UnmatchedNode(d.Name, d.Id, reason));
            }
        }

        return new MatchResult(pairs, unmatched);
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
