using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// `parity bug-report`(2.0.0,實驗性)。鐵則:**不自動送出任何資料**——只在本機整理、印出來給使用者看,
/// 再給一個預填好的 GitHub issue 網址;要不要開、要不要送,由使用者決定。測試的重點是遮罩一定生效:
/// 網址、route、selector、節點名稱、Figma fileKey、token、檔案路徑、環境變數的內容,一個都不能出現。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class BugReportTests
{
    private const string SecretUrl = "https://secret-staging.internal.example.com/checkout?session=SESSIONSECRET42";
    private const string SecretRoute = "/secret-checkout-route";
    private const string SecretFrame = "SECRETFRAME:987";
    private const string SecretFileKey = "FIGMAKEYSECRET1234567890";
    private const string SecretLiteralToken = "figd_LITERALTOKENSECRET_abcdef";
    private const string SecretSelector = ".secret-ignored-selector";
    private const string EnvFigmaToken = "ENVFIGMATOKENSECRET_123456";
    private const string EnvParityToken = "tok-ENVPARITYTOKENSECRET-0123456789";

    /// <summary>每個欄位都塞了不該外流的東西的設定檔(兩個 target、Figma 來源、各種檔案路徑)。</summary>
    private static string SecretConfig(TempDir dir)
    {
        var tokens = dir.Write(Path.Combine("private-folder-name", "secret-tokens.json"), "{}");
        return dir.Write("parity.config.json", $$"""
            {
              "figmaFileKey": "{{SecretFileKey}}",
              "designToken": "{{SecretLiteralToken}}",
              "tokensFile": "{{tokens.Replace("\\", "\\\\")}}",
              "mapFile": "private-folder-name/secret.map.json",
              "baselineFile": "private-folder-name/secret.baseline.db",
              "ignore": ["{{SecretSelector}}"],
              "targets": [
                { "route": "{{SecretRoute}}", "frame": "{{SecretFrame}}", "url": "{{SecretUrl}}", "width": 375 },
                { "route": "/another-private-page", "frame": "1:2", "url": "file:///C:/private-folder-name/page.html" }
              ],
              "tolerances": { "sizePx": 3, "colorDeltaE": 1.5 },
              "gate": { "failOn": ["critical", "serious", "medium"], "minMatchRate": 0.8 },
              "compare": { "position": "none" }
            }
            """);
    }

    private static IEnumerable<string> Secrets(TempDir dir) =>
    [
        SecretUrl, "secret-staging", "SESSIONSECRET42", SecretRoute, "another-private-page", SecretFrame, SecretFileKey,
        SecretLiteralToken, SecretSelector, EnvFigmaToken, EnvParityToken, "private-folder-name", dir.Path,
        dir.Path.Replace('\\', '/'), Path.GetFileName(dir.Path),
    ];

    /// <summary>輸出本身,加上輸出裡每個網址解碼後的內容(預填網址把報告編碼進 query string)。</summary>
    private static string Everything(Cli.Result r)
    {
        var decoded = string.Join("\n", System.Text.RegularExpressions.Regex.Matches(r.Out, @"https://\S+")
            .Select(m => Uri.UnescapeDataString(m.Value)));
        return r.Out + "\n" + r.Err + "\n" + decoded;
    }

    [Fact]
    public async Task Nothing_sensitive_from_the_config_or_the_environment_reaches_the_output()
    {
        using var dir = new TempDir();
        var config = SecretConfig(dir);
        using var env = new EnvScope().Set("FIGMA_TOKEN", EnvFigmaToken).Set("PARITY_TOKEN", EnvParityToken);

        var r = await Cli.RunAsync("bug-report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        var all = Everything(r);
        foreach (var secret in Secrets(dir))
            Assert.False(all.Contains(secret, StringComparison.OrdinalIgnoreCase), $"輸出裡出現了不該有的內容:{secret}");
        // 結構摘要照樣有用:數量、來源類型、數值與列舉
        Assert.Contains("targets: 2", r.Out);
        Assert.Contains("design source: figma", r.Out);
        Assert.Contains("sizePx 3", r.Out);
        Assert.Contains("colorDeltaE 1.5", r.Out);
        Assert.Contains("failOn: critical, serious, medium", r.Out);
        Assert.Contains("minMatchRate: 0.8", r.Out);
        Assert.Contains("compare.position: none", r.Out);
        Assert.Contains("designToken: literal value in the config", r.Out); // 只說有寫死,不印值
    }

    [Fact]
    public async Task It_prints_the_environment_and_a_prefilled_issue_link_and_sends_nothing()
    {
        string? opened = null;
        var previous = BugReportCommand.OpenInBrowser;
        BugReportCommand.OpenInBrowser = url => { opened = url; return true; };
        try
        {
            var r = await Cli.RunAsync("bug-report");

            Assert.Equal(0, r.ExitCode);
            Assert.Null(opened); // 沒給 --open 就只印網址
            Assert.Contains("nothing has been sent", r.Out, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"Parity: {VersionCommand.Describe()}", r.Out);
            foreach (var label in new[] { "OS:", ".NET runtime:", "Install channel:", "Node:", "Playwright:", "Chromium:" })
                Assert.Contains(label, r.Out);
            Assert.Contains("config: not given", r.Out);
            var link = System.Text.RegularExpressions.Regex.Match(r.Out, @"https://github\.com/\S+").Value;
            Assert.StartsWith("https://github.com/HSU-YU-MING/cornhsu-parity/issues/new?template=2-bug.yml&", link);
            Assert.Contains("&version=", link);
            Assert.Contains("&bug-report=", link);
        }
        finally { BugReportCommand.OpenInBrowser = previous; }
    }

    [Fact]
    public async Task Open_hands_the_same_link_to_the_browser_only_when_asked()
    {
        var opened = new List<string>();
        var previous = BugReportCommand.OpenInBrowser;
        BugReportCommand.OpenInBrowser = url => { opened.Add(url); return true; };
        try
        {
            var r = await Cli.RunAsync("bug-report", "--open");

            Assert.Equal(0, r.ExitCode);
            var link = System.Text.RegularExpressions.Regex.Match(r.Out, @"https://github\.com/\S+").Value;
            Assert.Equal([link], opened);
        }
        finally { BugReportCommand.OpenInBrowser = previous; }
    }

    [Fact]
    public async Task A_config_that_does_not_load_is_named_by_error_type_only()
    {
        // 載入失敗的訊息常帶路徑與設定值;只報例外型別
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", $$"""{ "targets": null, "figmaFileKey": "{{SecretFileKey}}", "gate": { "failOn": ["{{SecretRoute}}"] } }""");

        var r = await Cli.RunAsync("bug-report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("config: could not be loaded (InvalidOperationException)", r.Out);
        var all = Everything(r);
        foreach (var secret in new[] { SecretFileKey, SecretRoute, dir.Path })
            Assert.DoesNotContain(secret, all);
    }

    [Fact]
    public void A_report_too_long_for_a_link_keeps_only_the_summary_fields_in_the_url()
    {
        var fields = new Dictionary<string, string> { ["version"] = "2.0.0+abc1234", ["os"] = "Windows x64" };
        var text = new string('x', 5000);

        var full = BugReportCommand.IssueUrl(text, fields, maxLength: 20_000, out var fullFits);
        var shortUrl = BugReportCommand.IssueUrl(text, fields, maxLength: 400, out var shortFits);

        Assert.True(fullFits);
        Assert.Contains("&bug-report=", full);
        Assert.False(shortFits);
        Assert.DoesNotContain("bug-report=", shortUrl);
        Assert.Contains("&version=2.0.0%2Babc1234", shortUrl);
        Assert.True(shortUrl.Length <= 400);
    }

    [Theory]
    [InlineData(@"error at C:\Users\someone\proj\parity.config.json line 3", "error at <path> line 3")]
    [InlineData(@"share \\fileserver\team\design.json missing", "share <path> missing")]
    [InlineData("read /home/someone/proj/design.json failed", "read <path> failed")]
    [InlineData("fetch https://intranet.example.com/a?b=c failed", "fetch <url> failed")]
    [InlineData("file:///C:/proj/page.html", "<url>")]
    [InlineData("Windows 11 x64 · .NET 10.0.12 · revision 1243", "Windows 11 x64 · .NET 10.0.12 · revision 1243")]
    public void Redaction_masks_paths_and_urls_and_leaves_plain_text(string input, string expected)
        => Assert.Equal(expected, Redaction.Apply(input, []));

    [Fact]
    public void Redaction_masks_known_sensitive_values_wherever_they_appear()
        => Assert.Equal("token <redacted> and <redacted>!", Redaction.Apply("token s3cr3t-value and s3cr3t-value!", ["s3cr3t-value"]));

    [Fact]
    public async Task Bug_report_is_marked_experimental_in_both_helps()
    {
        foreach (var args in new[] { new[] { "help" }, ["bug-report", "--help"] })
        {
            var r = await Cli.RunAsync(args);
            var lines = r.Out.Split('\n');
            var usage = Array.FindIndex(lines, l => l.Contains("parity bug-report [--config <path>] [--open]"));
            Assert.True(usage >= 0, $"`{string.Join(' ', args)}` 找不到 bug-report 的用法行");
            Assert.Contains("(experimental)", lines[usage + 1]);
        }
    }
}
