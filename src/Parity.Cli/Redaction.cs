using System.Text.RegularExpressions;

namespace Parity.Cli;

/// <summary>
/// 要給人貼到公開地方的文字,送出前的遮罩(2.0.0,`parity bug-report` 用;日後其他「讓使用者回報」的功能
/// 也走這裡)。輸出本來就只由白名單欄位組成(見 <see cref="ConfigSummary"/>),這一層是第二道:
/// 已知的敏感值(token、設定檔所在目錄…)換成 <c>&lt;redacted&gt;</c>/<c>&lt;path&gt;</c>,
/// 任何網址換成 <c>&lt;url&gt;</c>,任何絕對路徑(Windows、UNC、Unix)換成 <c>&lt;path&gt;</c>。
/// </summary>
internal static partial class Redaction
{
    /// <summary>
    /// 遮罩 <paramref name="text"/>:先換掉已知的敏感值(長的先換;網址換成 &lt;url&gt;、像路徑的換成 &lt;path&gt;、
    /// 其他換成 &lt;redacted&gt;),再換任何網址與絕對路徑。少於 4 個字元的值不換(會誤傷一般文字)。
    /// </summary>
    public static string Apply(string text, IEnumerable<string> sensitiveValues)
    {
        foreach (var value in sensitiveValues.Where(v => v.Length >= 4).Distinct().OrderByDescending(v => v.Length))
        {
            var mask = value.Contains("://", StringComparison.Ordinal) ? "<url>"
                : value.Contains('/') || value.Contains('\\') ? "<path>"
                : "<redacted>";
            text = text.Replace(value, mask, StringComparison.OrdinalIgnoreCase);
        }
        text = UrlPattern().Replace(text, "<url>");
        text = EmailPattern().Replace(text, "<email>");
        text = SchemelessUrlPattern().Replace(text, "<url>");
        text = WindowsPathPattern().Replace(text, "<path>");
        text = UncPathPattern().Replace(text, "<path>");
        text = HomePathPattern().Replace(text, "<path>");
        text = UnixPathPattern().Replace(text, "<path>");
        // 最後一網打盡:剩下的字裡只要還帶 \ 或 /(有空白的路徑被切開的後半段、相對路徑),整個字都當路徑
        text = AnySlashToken().Replace(text, "<path>");
        return text;
    }

    /// <summary>
    /// 這台機器上「看起來是秘密」的環境變數的值:名稱含 TOKEN、SECRET、PASSWORD、KEY 或 CREDENTIAL,
    /// 值至少 8 個字元。報告從來不印環境變數的內容;這是萬一有東西把它帶進來時的保險。
    /// </summary>
    public static IEnumerable<string> SecretEnvironmentValues()
    {
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var name = e.Key.ToString() ?? "";
            var value = e.Value?.ToString() ?? "";
            if (value.Length >= 8 && SecretName().IsMatch(name)) yield return value;
        }
    }

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.\-]*://\S+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailPattern();

    // 沒有 scheme 的網址:www.開頭,或「主機名.頂級網域」後面接 :port 或 /路徑
    [GeneratedRegex(@"\bwww\.[^\s""'<>|]+|\b(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?::\d+)?/[^\s""'<>|]*")]
    private static partial Regex SchemelessUrlPattern();

    [GeneratedRegex(@"\b[A-Za-z]:[\\/][^\s""'<>|]*")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"(?<!\S)~[\\/][^\s""'<>|]*")]
    private static partial Regex HomePathPattern();

    [GeneratedRegex(@"[^\s<>]*[\\/][^\s<>]*")]
    private static partial Regex AnySlashToken();

    [GeneratedRegex(@"\\\\[^\s\\""'<>|]+\\[^\s""'<>|]*")]
    private static partial Regex UncPathPattern();

    // 絕對 Unix 路徑(至少兩段 /a/b)。後面的 AnySlashToken 會再把任何帶 / 或  的字整個遮掉——
    // bug-report 的文字裡沒有需要保留斜線的內容;日後若有,要先在這裡挑掉例外
    [GeneratedRegex(@"(?<![\w.~-])/(?:[^\s/""'<>|]+/)+[^\s/""'<>|]*")]
    private static partial Regex UnixPathPattern();

    [GeneratedRegex("TOKEN|SECRET|PASSWORD|PASSWD|KEY|CREDENTIAL", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
}
