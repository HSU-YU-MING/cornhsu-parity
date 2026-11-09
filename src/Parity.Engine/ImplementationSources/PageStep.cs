namespace Parity.Engine.ImplementationSources;

/// <summary>
/// 擷取前在頁面上做的一步(2.5.0,競品清單「登入態與頁面狀態」):打開選單、切到某個分頁、填搜尋框……
/// 頁面載完之後、凍結動畫與量測之前依序執行。設定檔的寫法見 Parity.Cli 的 TargetConfig.Steps。
/// </summary>
/// <param name="Action">click / hover / fill / press / waitFor / wait。</param>
/// <param name="Selector">click、hover、fill、waitFor 的對象;press 的選填對象(沒有就按在整頁)。</param>
/// <param name="Value">fill 要填的字(已解析過 env:);press 的按鍵名(Playwright 的寫法,例如 Escape、Enter、Control+A)。</param>
/// <param name="Milliseconds">wait 要等多久。</param>
public sealed record PageStep(string Action, string? Selector = null, string? Value = null, int? Milliseconds = null)
{
    /// <summary>出錯時說是哪一步。**不帶 fill 的值**:那可能是密碼或從環境變數讀來的祕密。</summary>
    public string Describe() => Action switch
    {
        "wait" => $"wait {Milliseconds} ms",
        "press" => Selector is null ? $"press {Value}" : $"press {Value} in {Selector}",
        _ => $"{Action} {Selector}",
    };
}
