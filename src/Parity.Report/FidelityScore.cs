namespace Parity.Engine;

/// <summary>
/// 還原度分數(0–100):給 PM 看的一個好消化的數字——忠實實作的設計節點比例(有配對且零落差)。
/// 住在 Parity.Report:Parity.Server 收件時要重算分數,而它不能引用含擷取端的引擎。
/// </summary>
public static class FidelityScore
{
    /// <summary>
    /// 「忠實」= 有配對且沒有硬落差;純軟落差(如 font-family stack 差異)不扣分。
    /// 分數與 Markdown 表頭的計數都用這一個判準——兩處口徑不一致會出現
    /// 「100/100 卻 11/12 忠實」的自相矛盾標題。
    /// </summary>
    public static bool IsFaithful(NodeResult n) => !n.Diffs.Any(d => !d.Soft);

    public static int Compute(IEnumerable<FidelityReport> reports)
    {
        var list = reports as IReadOnlyList<FidelityReport> ?? reports.ToList();
        var total = list.Sum(r => r.Summary.DesignNodes);
        if (total == 0) return 100;
        var clean = list.Sum(r => r.Nodes.Count(IsFaithful));
        var score = (int)Math.Round(100.0 * clean / total);
        // 100 保留給「全部忠實」:四捨五入會把 99.5+ 進成 100,出現「100/100 卻 GATE FAIL」
        // 的矛盾標題(野生實查 F5,Stripe 3306/3322 忠實)。封頂 99,滿分只有一種拿法。
        return score == 100 && clean < total ? 99 : score;
    }
}
