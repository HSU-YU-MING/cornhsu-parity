using Parity.Engine;

namespace Parity.Cli;

/// <summary>
/// 設定檔 <c>accept</c> 的套用(2.5.0,競品清單「單筆落差豁免」)。<c>ignore</c> 整個元素不看、baseline 整批收下;
/// 這裡是一條一條收、每條帶原因。收下的落差留在報告裡(<see cref="PropDiff.Accepted"/> 寫原因),同時變成 soft:
/// gate、分數、<c>--baseline</c> 本來就跳過 soft,不必每個消費端各自再認一次。嚴重度照實留著——落差是真的,收下是決定。
/// </summary>
internal static class Acceptance
{
    /// <summary>
    /// 把一份報告裡被收下的落差標起來。<paramref name="used"/> 收集這次有對到東西的條目索引(之後找過期的條目用)。
    /// 一條落差同時對到好幾條時,原因用第一條的,但每條都算有用到。
    /// </summary>
    internal static FidelityReport Apply(FidelityReport report, IReadOnlyList<AcceptConfig> entries, ISet<int> used)
    {
        if (entries.Count == 0) return report;
        var changed = false;
        var nodes = report.Nodes.Select(node =>
        {
            var diffs = node.Diffs.Select(diff =>
            {
                string? reason = null;
                for (var i = 0; i < entries.Count; i++)
                {
                    if (!Matches(entries[i], report.Route, node, diff)) continue;
                    used.Add(i);
                    reason ??= entries[i].Reason;
                }
                if (reason is null) return diff;
                changed = true;
                return diff with { Soft = true, Accepted = reason };
            }).ToList();
            return node with { Diffs = diffs };
        }).ToList();
        return changed ? report with { Nodes = nodes } : report;
    }

    /// <summary>layer 與 prop 一字不差;route、selector、actual 有寫才比。</summary>
    internal static bool Matches(AcceptConfig entry, string route, NodeResult node, PropDiff diff)
        => string.Equals(entry.Layer, node.DesignLayer, StringComparison.Ordinal)
            && string.Equals(entry.Prop, diff.Prop, StringComparison.Ordinal)
            && (entry.Route is null || string.Equals(entry.Route, route, StringComparison.Ordinal))
            && (entry.Selector is null || string.Equals(entry.Selector, node.Selector, StringComparison.Ordinal))
            && (entry.Actual is null || string.Equals(entry.Actual, diff.Actual, StringComparison.Ordinal));

    /// <summary>
    /// 這次掃過、卻一條落差都沒對到的條目:落差修好了,或值又變了(有寫 actual 時)。留著的話,同一個地方以後再出錯會被它默默收下,
    /// 所以說一聲。只看這次真的掃到的 target:沒寫 route 的條目只在掃了全部 target 時才判斷(<c>--target</c> 只掃一頁時,它可能是別頁的)。
    /// 設定檔的字串一律過 <see cref="ConsoleText.Safe"/>。
    /// </summary>
    internal static List<string> StaleNotes(IReadOnlyList<AcceptConfig> entries, ISet<int> used, IReadOnlySet<string> scannedRoutes, bool scannedAll)
    {
        var notes = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (used.Contains(i)) continue;
            var entry = entries[i];
            if (entry.Route is null ? !scannedAll : !scannedRoutes.Contains(entry.Route)) continue;
            var where = entry.Route is null ? "" : $" on {ConsoleText.Safe(entry.Route)}";
            var value = entry.Actual is null ? "" : $" at {ConsoleText.Safe(entry.Actual)}";
            notes.Add($"note: accept[{i}] ({ConsoleText.Safe(entry.Layer)} {ConsoleText.Safe(entry.Prop)}{value}{where}) matched no gap — " +
                (entry.Actual is null
                    ? "it looks fixed. Remove the entry, or it will quietly accept the next gap there."
                    : "it is fixed or the value changed. Remove the entry or update \"actual\"."));
        }
        return notes;
    }
}
