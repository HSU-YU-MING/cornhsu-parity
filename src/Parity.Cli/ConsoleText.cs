using System.Text;

namespace Parity.Cli;

/// <summary>
/// 印到主控台的字串裡,來自 repo 內容的那些(設計稿的圖層名、selector、設計值、設定檔的 route、
/// 指令列參數)一律先過這裡:控制字元跳脫成字面(換行 → <c>\n</c>)。
/// 為什麼:在 GitHub Actions 裡,換行後落在行首的內容就是 runner 會解析的 workflow command
/// (<c>::warning …</c>、<c>::add-mask::</c>)——gate 繞不過,但不該讓 repo 內容決定 runner 收到什麼指令。
/// 定位字元(tab)保留;console 輸出不屬於凍結的六面。例外訊息(含 Playwright 的多行說明)不經過這裡,
/// 那條另外留帳(ROADMAP)。
/// </summary>
internal static class ConsoleText
{
    public static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!text.Any(c => c != '\t' && char.IsControl(c))) return text;
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
            sb.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
