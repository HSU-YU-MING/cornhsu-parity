using System.Text.RegularExpressions;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// 凍結審查面 4 的配套義務:`reason`/`matchedBy` 是開放集合,**當前字彙表由 README「報告字彙表」維護**
/// (CHANGELOG 1.0.0)。新增一個值本身不算破壞,但忘了更新 README 就是對解析端說謊——
/// 這裡把 Matcher 實際寫得出來的值、本測試的已知清單、兩份 README 三方對齊,任一邊漂了就紅。
/// </summary>
public class ReportVocabularyTests
{
    private static readonly string[] KnownReasons =
        ["no-anchor", "ambiguous-or-missing-text", "randomized-id", "size-implausible"];

    private static readonly string[] KnownMatchedBy =
        ["selector", "explicit", "auto-text", "auto-name", "auto-container"];

    /// <summary>README 那一行裡反引號包住的值。</summary>
    private static List<string> BacktickedValuesOnLine(string readme, string linePattern)
    {
        var line = Regex.Match(readme, linePattern, RegexOptions.Multiline);
        Assert.True(line.Success, $"README 找不到字彙表那一行(pattern: {linePattern})——措辭改了就修 pattern,別刪這條檢查");
        return Regex.Matches(line.Groups["list"].Value, "`([^`]+)`").Select(m => m.Groups[1].Value).ToList();
    }

    [Theory]
    [InlineData("README.md", @"^- `unmatched\[\]\.reason` — .*Currently: (?<list>.+)$", @"^- `nodes\[\]\.matchedBy` — .*Currently: (?<list>.+)$")]
    [InlineData("README.zh-Hant.md", @"^- `unmatched\[\]\.reason` — .*目前:(?<list>.+)$", @"^- `nodes\[\]\.matchedBy` — .*目前:(?<list>.+)$")]
    public void Readme_vocabulary_lists_exactly_the_known_open_set_values(string file, string reasonLine, string matchedByLine)
    {
        var readme = Repo.Read(file).Replace("\r\n", "\n");

        Assert.Equal(KnownReasons.Order(), BacktickedValuesOnLine(readme, reasonLine).Order());
        Assert.Equal(KnownMatchedBy.Order(), BacktickedValuesOnLine(readme, matchedByLine).Order());
    }

    [Theory]
    [InlineData("README.md", @"^- `severity` — \*\*closed set\*\*.*?: (?<list>.+?)\. `status`: (?<status>.+)\.$")]
    [InlineData("README.zh-Hant.md", @"^- `severity` — \*\*封閉集\*\*.*?:(?<list>.+?);`status`:(?<status>.+)。$")]
    public void Readme_closed_sets_match_the_enums(string file, string pattern)
    {
        var readme = Repo.Read(file).Replace("\r\n", "\n");
        var m = Regex.Match(readme, pattern, RegexOptions.Multiline);
        Assert.True(m.Success, $"{file} 找不到 severity/status 封閉集那一行");

        static List<string> Values(string s) => Regex.Matches(s, "`([^`]+)`").Select(x => x.Groups[1].Value).ToList();
        Assert.Equal(Enum.GetNames<Severity>().Select(n => n.ToLowerInvariant()), Values(m.Groups["list"].Value));
        Assert.Equal(Enum.GetNames<DiffStatus>().Select(n => n.ToLowerInvariant()), Values(m.Groups["status"].Value));
    }

    [Fact]
    public void Matcher_emits_no_vocabulary_beyond_the_known_lists()
    {
        // 掃 Matcher.cs 程式碼(去掉註解)裡所有小寫 kebab 字串常值:新增一個 reason/matchedBy
        // 卻沒更新已知清單(→ 上面的 README 檢查)時,這裡先紅。若新常值根本不是字彙,
        // 就把它加進 NotVocabulary,並寫一行理由
        string[] notVocabulary = [];
        var code = string.Join('\n', File.ReadAllLines(Repo.PathOf("src", "Parity.Engine", "Comparison", "Matcher.cs"))
            .Select(l => l.IndexOf("//", StringComparison.Ordinal) is var i and >= 0 ? l[..i] : l));

        var literals = Regex.Matches(code, "\"([a-z]+(?:-[a-z]+)*)\"").Select(m => m.Groups[1].Value)
            .Except(notVocabulary).Distinct().Order();

        Assert.Equal(KnownReasons.Concat(KnownMatchedBy).Order(), literals);
    }
}
