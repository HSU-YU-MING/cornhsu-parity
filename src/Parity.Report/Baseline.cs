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

    /// <summary>
    /// 把一次掃描的所有報告攤平成落差清單(baseline 儲存與比對的輸入)。soft 落差(font-family)不進來:
    /// 它不擋 gate,也就不該在 --baseline 模式算「新增」(2.0.0 起;1.x 一起攤平)。schema 不必改——
    /// 1.x 存的 baseline 裡的 soft 列,比對時靠 <see cref="KeysOf"/> 認出「那條落差還在」,不會被當成修好;
    /// 重存 baseline 就不再有這些列。
    /// </summary>
    public static IReadOnlyList<DiffRecord> FromReports(IEnumerable<FidelityReport> reports)
        => reports.SelectMany(r => r.Nodes.SelectMany(n => n.Diffs.Where(d => !d.Soft).Select(d =>
                new DiffRecord(r.Route, n.DesignLayer, n.Selector, d.Prop, d.Severity, d.Expected, d.Actual))))
            .ToList();

    /// <summary>
    /// 現況裡**所有**落差(含 soft)的鍵——判斷 baseline 的哪幾列「修好了」用。只用
    /// <see cref="FromReports"/> 的結果判斷的話,舊 baseline 裡的 soft 列每一次比對都會算成修好。
    /// </summary>
    public static IReadOnlySet<(string Route, string Layer, string Selector, string Prop)> KeysOf(IEnumerable<FidelityReport> reports)
        => reports.SelectMany(r => r.Nodes.SelectMany(n => n.Diffs.Select(d => (r.Route, n.DesignLayer, n.Selector, d.Prop))))
            .ToHashSet();
}

/// <summary>現況 vs baseline 的比對結果(規畫書 M5:讓 CI 只擋「新增/惡化」的落差,而非全部)。</summary>
public sealed record BaselineComparison(
    IReadOnlyList<DiffRecord> Regressions, // 現在有、baseline 沒有 → 新落差
    IReadOnlyList<DiffRecord> Worsened,    // 兩邊都有,但嚴重度變高
    IReadOnlyList<DiffRecord> Fixed,       // baseline 有、現在沒有 → 修好了
    int Unchanged,
    // baseline 有、落差也還在,但現況不拿它比(1.x 存的 soft 列):另計,baseline 的每一列都有去處
    int Ignored = 0)
{
    /// <summary>有沒有「相對 baseline 變差」——回歸模式的 gate 就看這個。</summary>
    public bool HasRegressions => Regressions.Count > 0 || Worsened.Count > 0;
}

/// <summary>把現況落差和 baseline 落差按鍵配對,分出新增 / 惡化 / 修好 / 不變。純函式,好測。</summary>
public static class BaselineComparer
{
    /// <param name="current">現況要比的落差(<see cref="DiffRecord.FromReports"/>,不含 soft)。</param>
    /// <param name="baseline">baseline 的落差(1.x 存的可能含 soft 列)。</param>
    /// <param name="stillPresent">
    /// 現況所有落差(含 soft)的鍵(<see cref="DiffRecord.KeysOf"/>)。baseline 的某一列只有在這裡也找不到時
    /// 才算修好;省略時只看 <paramref name="current"/>。
    /// </param>
    public static BaselineComparison Compare(
        IReadOnlyList<DiffRecord> current, IReadOnlyList<DiffRecord> baseline,
        IReadOnlySet<(string Route, string Layer, string Selector, string Prop)>? stillPresent = null)
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
        var ignored = 0;
        foreach (var (key, bas) in baseByKey)
        {
            if (curByKey.ContainsKey(key)) continue;
            if (stillPresent?.Contains(key) == true) ignored++;
            else fixedList.Add(bas);
        }

        return new BaselineComparison(regressions, worsened, fixedList, unchanged, ignored);
    }
}
