using System.Text;
using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// 報告會貼成 PR 留言,而圖層名、route 這些字串誰都可能改得到(Figma 的編輯者、快照頁面的 id、report.json 與基準)。
/// 它們不能在留言裡變成連結、圖片或 @ 提及。GitHub 只在程式碼區段裡不處理這些,所以一律要留在區段內。
/// 這裡照 CommonMark 的規則把程式碼區段拿掉,看剩下的文字裡有沒有惡意內容。
/// 修的時候實際交給 GitHub 渲染比對過(2.3.1,修前 7 個連結、2 張圖片、1 個提及);這裡守的是同一件事,不必連網。
/// </summary>
public class MarkdownInjectionTests
{
    public static TheoryData<string> Hostile =>
    [
        "[click](https://evil.example/a)",
        "![pixel](https://evil.example/p.png)",
        "@someone",
        "<img src=https://evil.example/x>",
        "https://evil.example/autolink",
        "www.evil.example",
        "a` [click](https://evil.example/b) `c",        // 反引號:包一層就跳得出來
        "``` [click](https://evil.example/c) ```",
        "line one\n[click](https://evil.example/d)",    // 換行:結束表格列,下一行就是一般文字
        "a|b [click](https://evil.example/e)",          // 直線:切開表格欄位
        "a\\|b [click](https://evil.example/f)",        // 反斜線 + 直線。GitHub 實測不會切開,留著防回歸
        "\\` [click](https://evil.example/g)",
    ];

    private static FidelityReport HostileReport(string s) => new(s, "http://x/", "design",
        [
            new NodeResult(s, "1:1", "#x", "auto-name", Severity.Serious,
            [
                new PropDiff(s, s, s, null, null, 0, Severity.Serious),
                new PropDiff("text", s, s, null, null, 0, Severity.Minor, Soft: true), // 這條有修法建議,建議裡也帶著字串
            ]),
        ],
        [new UnmatchedNode(s, "1:2", s)],
        new ReportSummary(2, 1, 1, 1, 0, 1, 0, 1, Severity.Serious),
        new ExtraElements(1, [new ExtraElement(s, s, default)]));

    /// <summary>照 CommonMark 拿掉程式碼區段:開頭的反引號串要配到一樣長的,配不到就只是一般文字。</summary>
    private static string OutsideCodeSpans(string markdown)
    {
        var outside = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            for (var i = 0; i < line.Length;)
            {
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    outside.Append(line, i, 2); // 區段外的反斜線才是跳脫
                    i += 2;
                }
                else if (line[i] != '`')
                {
                    outside.Append(line[i++]);
                }
                else
                {
                    var length = RunAt(line, i);
                    var close = FindRun(line, i + length, length);
                    if (close < 0)
                    {
                        outside.Append(line, i, length);
                        i += length;
                    }
                    else
                    {
                        i = close + length;
                    }
                }
            }
            outside.Append('\n');
        }
        return outside.ToString();
    }

    private static int RunAt(string line, int start)
    {
        var end = start;
        while (end < line.Length && line[end] == '`') end++;
        return end - start;
    }

    private static int FindRun(string line, int from, int length)
    {
        for (var i = from; i < line.Length;)
        {
            if (line[i] != '`')
            {
                i++;
                continue;
            }
            var run = RunAt(line, i);
            if (run == length) return i;
            i += run;
        }
        return -1;
    }

    private static void AssertNothingEscaped(string markdown)
    {
        var outside = OutsideCodeSpans(markdown);
        Assert.DoesNotContain("evil.example", outside);
        Assert.DoesNotContain("@someone", outside);
        Assert.DoesNotContain("<img", outside);
    }

    [Fact]
    public void The_check_itself_sees_what_escaped_a_code_span()
    {
        // 這條檢查失效的話,下面全部都會靜靜地綠
        Assert.Contains("evil.example", OutsideCodeSpans("[x](https://evil.example)"));
        Assert.Contains("evil.example", OutsideCodeSpans("`a` [x](https://evil.example) `b`")); // 舊寫法:單反引號包住帶反引號的名字
        Assert.DoesNotContain("evil.example", OutsideCodeSpans("`` a` [x](https://evil.example) `b ``"));
        Assert.Contains("evil.example", OutsideCodeSpans("` [x](https://evil.example)")); // 沒有配對的反引號不算區段
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Nothing_in_a_report_can_write_markdown(string hostile)
    {
        var report = HostileReport(hostile);
        var record = new DiffRecord(hostile, hostile, hostile, hostile, Severity.Serious, hostile, hostile);
        var baseline = new BaselineComparison([record], [record], [record], 0);

        foreach (var figmaFileKey in new[] { null, "abc123DEF456" }) // 有 Figma 時圖層名是連結文字,另一條路
        {
            AssertNothingEscaped(MarkdownReport.Render([report], gateFail: true, baseline, figmaFileKey: figmaFileKey));
            AssertNothingEscaped(MarkdownReport.Render(
                [report, report with { Route = "/other" }], gateFail: true, baseline, figmaFileKey: figmaFileKey)); // 多頁才有 route 欄
        }
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Gate_notes_cannot_write_markdown(string hostile)
    {
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["*"] } };
        var report = HostileReport(hostile);
        var nothingMatched = report with { Summary = report.Summary with { Matched = 0 } };

        foreach (var notes in new[] { config.UnmatchedGateNotes([report]), config.MatchIntegrityNotes([nothingMatched]) })
        {
            Assert.NotEmpty(notes);
            AssertNothingEscaped(MarkdownReport.Render([report], gateFail: true, gateNotes: notes));
        }
    }

    [Fact]
    public void Gate_notes_list_layer_names_as_code_and_the_console_keeps_them_plain()
    {
        var config = new ParityConfig { Gate = { FailOnUnmatched = ["*"] } };
        var report = HostileReport("/") with
        {
            Route = "/pricing",
            Unmatched =
            [
                .. Enumerable.Range(1, 6).Select(i => new UnmatchedNode($"label-{i}", $"1:{i}", "text-not-found")),
                new UnmatchedNode("badge", "2:1", "no-anchor"),
            ],
        };

        Assert.Equal(
            new MarkdownNote("/pricing: 7 unmatched design nodes blocked by gate.failOnUnmatched (text-not-found: " +
                "`label-1`, `label-2`, `label-3`, `label-4`, `label-5`, +1 more; no-anchor: `badge`)"),
            Assert.Single(config.UnmatchedGateNotes([report])));
        Assert.Equal(
            "7 unmatched design nodes blocked by gate.failOnUnmatched (text-not-found: " +
            "label-1, label-2, label-3, label-4, label-5, +1 more; no-anchor: badge)",
            config.UnmatchedGateFailure(report));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/pricing")]
    [InlineData("/docs/v2/index.html")]
    [InlineData("paddingLeft")]
    [InlineData("text-not-found")]
    public void Ordinary_words_stay_as_they_are(string word)
        => Assert.Equal(word, MarkdownReport.Word(word)); // 一般的報告不能因為這個修正而變樣

    [Theory]
    [InlineData("/a b")]
    [InlineData("www.example.com")]  // 開頭不是 / 的點:GitHub 會自動變成連結
    [InlineData("/_x_")]             // 底線會變成斜體
    [InlineData("/x\n")]             // 結尾的換行:一般的 $ 會放過它
    [InlineData("")]
    public void Anything_else_goes_into_a_code_span(string word)
        => Assert.StartsWith("`", MarkdownReport.Word(word));

    [Fact]
    public void A_very_long_layer_name_is_cut_in_the_middle()
    {
        var name = new string('a', 150) + new string('b', 150);

        var code = MarkdownReport.Name(name);

        Assert.Equal(MarkdownReport.MaxNameCell + 2, code.Length); // 上限 + 前後的反引號
        Assert.StartsWith("`aaa", code);
        Assert.EndsWith("bbb`", code);
    }
}
