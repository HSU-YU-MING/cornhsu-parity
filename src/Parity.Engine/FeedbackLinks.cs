namespace Parity.Engine;

/// <summary>
/// 讓使用者回報的連結(2.0.0)。全部指向 GitHub 的 issue 範本(.github/ISSUE_TEMPLATE/,預填網址
/// <c>issues/new?template=…</c>):Markdown 報告與 PR 留言的最後一行、本機報告 UI、<c>parity bug-report</c>。
/// **只是連結**——工具不會自己連網、不會送出任何資料;使用者點了、看過內容、自己按送出。
/// 範本改檔名時這裡要跟著改(測試會檢查檔案存在)。
/// </summary>
public static class FeedbackLinks
{
    public const string Repository = "https://github.com/HSU-YU-MING/cornhsu-parity";

    /// <summary>誤判/漏判(假落差、漏掉的落差、嚴重度、配錯元素)。</summary>
    public const string Misjudgment = Repository + "/issues/new?template=1-misjudgment.yml";

    /// <summary>錯誤回報(當掉、exit code 不對、安裝與瀏覽器問題)。</summary>
    public const string Bug = Repository + "/issues/new?template=2-bug.yml";
}
