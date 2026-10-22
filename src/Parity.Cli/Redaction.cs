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
        text = WindowsPathPattern().Replace(text, "<path>");
        text = UncPathPattern().Replace(text, "<path>");
        text = UnixPathPattern().Replace(text, "<path>");
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

    [GeneratedRegex(@"\b[A-Za-z]:[\\/][^\s""'<>|]*")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"\\\\[^\s\\""'<>|]+\\[^\s""'<>|]*")]
    private static partial Regex UncPathPattern();

    // 至少兩段的 /a/b 才算路徑:避免把「x64/arm64」這種用語或單一的 / 吃掉
    [GeneratedRegex(@"(?<![\w.~-])/(?:[^\s/""'<>|]+/)+[^\s/""'<>|]*")]
    private static partial Regex UnixPathPattern();

    [GeneratedRegex("TOKEN|SECRET|PASSWORD|PASSWD|KEY|CREDENTIAL", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
}
