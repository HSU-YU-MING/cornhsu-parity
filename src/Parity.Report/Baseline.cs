namespace Parity.Engine;

/// <summary>
/// 一條屬性級落差的扁平、可儲存/可比對檢視(規畫書 M5:baseline / 歷史)。
/// 身分 = 路由 + 設計圖層 + 屬性:同一個地方的同一個屬性,跨次執行視為「同一條」。
/// </summary>
public sealed record DiffRecord(
    string Route,
    string DesignLayer,
    string Selector,
    string Prop,
    Severity Severity,
    string Expected,
    string Actual)
{
    /// <summary>
    /// 跨次執行辨識同一條落差的鍵(不含數值:值變了仍是同一條,只是嚴重度可能變)。
    /// 含 Selector 是為了消歧「重複圖層名」——設計稿常有多個 "Frame"/"Text",
    /// 只用圖層名會把不同元素的同屬性落差誤當成同一條。
    /// </summary>
    public (string Route, string Layer, string Selector, string Prop) Key
        => (Route, DesignLayer, Selector, Prop);

    /// <summary>把一次掃描的所有報告攤平成落差清單(baseline 儲存與比對的輸入)。</summary>
    public static IReadOnlyList<DiffRecord> FromReports(IEnumerable<FidelityReport> reports)
        => reports.SelectMany(r => r.Nodes.SelectMany(n => n.Diffs.Select(d =>
                new DiffRecord(r.Route, n.DesignLayer, n.Selector, d.Prop, d.Severity, d.Expected, d.Actual))))
            .ToList();
}

/// <summary>現況 vs baseline 的比對結果(規畫書 M5:讓 CI 只擋「新增/惡化」的落差,而非全部)。</summary>
public sealed record BaselineComparison(
    IReadOnlyList<DiffRecord> Regressions, // 現在有、baseline 沒有 → 新落差
    IReadOnlyList<DiffRecord> Worsened,    // 兩邊都有,但嚴重度變高
    IReadOnlyList<DiffRecord> Fixed,       // baseline 有、現在沒有 → 修好了
    int Unchanged)
{
    /// <summary>有沒有「相對 baseline 變差」——回歸模式的 gate 就看這個。</summary>
    public bool HasRegressions => Regressions.Count > 0 || Worsened.Count > 0;
}

/// <summary>把現況落差和 baseline 落差按鍵配對,分出新增 / 惡化 / 修好 / 不變。純函式,好測。</summary>
public static class BaselineComparer
{
    public static BaselineComparison Compare(
        IReadOnlyList<DiffRecord> current, IReadOnlyList<DiffRecord> baseline)
    {
        // 同鍵可能多筆(理論上少見)→ 取最嚴重的那筆代表
        var baseByKey = baseline
            .GroupBy(d => d.Key)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Severity)!);
        var curByKey = current
            .GroupBy(d => d.Key)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Severity)!);

        var regressions = new List<DiffRecord>();
        var worsened = new List<DiffRecord>();
        var fixedList = new List<DiffRecord>();
        var unchanged = 0;

        foreach (var (key, cur) in curByKey)
        {
            if (!baseByKey.TryGetValue(key, out var bas)) regressions.Add(cur);
            else if (cur.Severity > bas.Severity) worsened.Add(cur);
            else unchanged++;
        }
        foreach (var (key, bas) in baseByKey)
            if (!curByKey.ContainsKey(key)) fixedList.Add(bas);

        return new BaselineComparison(regressions, worsened, fixedList, unchanged);
    }
}
