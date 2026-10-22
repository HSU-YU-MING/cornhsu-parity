using System.Text.RegularExpressions;

namespace Parity.Tests;

/// <summary>
/// GitHub issue 範本(2.0.0):錯誤回報、誤判/漏判、功能建議,外加 config.yml。報告裡的「回報誤判」連結
/// 與 `parity bug-report` 產生的預填網址都指向這裡的檔名——改檔名等於讓那些連結失效。
/// 解析只用 regex(不為測試引入 YAML 套件,同 ActionYmlTests)。
/// </summary>
public class IssueTemplateTests
{
    private static string Template(string file) => Repo.Read(".github", "ISSUE_TEMPLATE", file).Replace("\r\n", "\n");

    [Theory]
    [InlineData("1-misjudgment.yml")]
    [InlineData("2-bug.yml")]
    [InlineData("3-feature.yml")]
    public void Each_template_is_a_form_with_a_name_a_description_and_fields(string file)
    {
        var yml = Template(file);

        Assert.Matches(new Regex(@"^name: \S.*$", RegexOptions.Multiline), yml);
        Assert.Matches(new Regex(@"^description: \S.*$", RegexOptions.Multiline), yml);
        Assert.Matches(new Regex(@"^body:\n", RegexOptions.Multiline), yml);
        Assert.True(Regex.Matches(yml, @"^  - type: (input|textarea|dropdown)$", RegexOptions.Multiline).Count >= 3,
            $"{file} 的欄位太少");
        // id 不能重複(GitHub 會拒絕整份表單)
        var ids = Regex.Matches(yml, @"^    id: (\S+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void The_bug_template_asks_for_the_environment_the_maintainer_needs()
    {
        var yml = Template("2-bug.yml");

        foreach (var id in new[] { "version", "os", "node", "chromium", "channel", "steps", "expected", "actual" })
            Assert.Matches(new Regex($@"^    id: {id}$", RegexOptions.Multiline), yml);
        Assert.Contains("parity bug-report", yml); // 由工具整理環境資訊,使用者自己看過再貼
    }

    [Fact]
    public void The_misjudgment_template_asks_for_both_values_and_warns_to_redact_before_attaching()
    {
        var yml = Template("1-misjudgment.yml");

        foreach (var id in new[] { "version", "property", "design-value", "implementation-value", "why", "kind" })
            Assert.Matches(new Regex($@"^    id: {id}$", RegexOptions.Multiline), yml);
        // 截圖與報告片段常帶內部網址、客戶資料:附上之前先遮掉
        Assert.Contains("mask", yml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("URL", yml);
    }

    [Fact]
    public void Blank_issues_are_off_and_security_reports_go_to_the_private_channel()
    {
        var config = Template("config.yml");

        Assert.Matches(new Regex(@"^blank_issues_enabled: false$", RegexOptions.Multiline), config);
        Assert.Contains("https://github.com/HSU-YU-MING/cornhsu-parity/security", config);
        Assert.Contains("SECURITY.md", config);
    }
}
