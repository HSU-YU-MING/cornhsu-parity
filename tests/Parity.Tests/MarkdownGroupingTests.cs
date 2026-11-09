using Parity.Engine;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// 清單 #9(只改呈現、不改判定):相同落差併成一列;有可能的根本原因時,位移收到後面一組、先講原因。
/// </summary>
public class MarkdownGroupingTests
{
    private static NodeResult Node(string name, params PropDiff[] diffs)
        => new(name, name, "#" + name, "auto-name", diffs.Length == 0 ? Severity.None : diffs.Max(d => d.Severity), diffs,
            new Box(0, 0, 100, 40), new Box(0, 0, 100, 40));

    private static PropDiff Color(string expected = "#2563EB", string actual = "#1D4ED8")
        => new("color", expected, actual, null, 9, 2, Severity.Serious);

    private static PropDiff Shift(string prop = "offsetY") => new(prop, "16", "40", "px", 24, 4, Severity.Critical);

    private static FidelityReport Page(IReadOnlyList<NodeResult> nodes, params UnmatchedNode[] unmatched)
        => new("/", "http://x/", "d.json#1", nodes, unmatched,
            new ReportSummary(nodes.Count + unmatched.Length, nodes.Count, unmatched.Length, nodes.Count(n => n.Diffs.Count > 0), 0, 0, 0, 0, Severity.Critical));

    [Fact]
    public void The_same_wrong_token_on_many_elements_is_one_row()
    {
        var md = MarkdownReport.Render([Page([Node("A", Color()), Node("B", Color()), Node("C", Color())])], gateFail: true);

        Assert.Contains("| 3 elements: `A`, `B`, `C` | color |", md);
        Assert.Single(md.Split('\n'), l => l.Contains("| color |", StringComparison.Ordinal));
    }

    [Fact]
    public void Many_elements_list_three_names_and_different_values_stay_separate()
    {
        var nodes = Enumerable.Range(1, 5).Select(i => Node($"N{i}", Color())).Append(Node("Other", Color(actual: "#000000"))).ToList();

        var md = MarkdownReport.Render([Page(nodes)], gateFail: true);

        Assert.Contains("| 5 elements: `N1`, `N2`, `N3`, … | color |", md);
        Assert.Contains("| `Other` | color |", md); // 實際值不同,不併
    }

    [Fact]
    public void Shifts_after_a_missing_element_are_grouped_behind_the_cause()
    {
        // Badge 整頁找不到,下面兩個元素被推上來:原因先講,位移收進可摺疊的一組
        var md = MarkdownReport.Render([Page([Node("Title", Shift()), Node("Footer", Shift())],
            new UnmatchedNode("Badge", "Badge", "text-not-found", new Box(0, 0, 40, 20)))], gateFail: true);

        Assert.Contains("### Position shifts that may be knock-on effects (2)", md);
        Assert.Contains("- likely caused by `Badge` not found on the page. Fix those first; these shifts often go away with them.", md);
        Assert.Contains("<details><summary>Show 2 position shift(s)</summary>", md);
        Assert.DoesNotContain("### Diffs and suggested fixes", md); // 主表只剩直接的落差,這頁沒有
    }

    [Fact]
    public void A_size_change_is_a_cause_and_stays_in_the_main_table()
    {
        var md = MarkdownReport.Render([Page([Node("Hero", new PropDiff("height", "200", "260", "px", 60, 2, Severity.Critical)), Node("Card", Shift())])], gateFail: true);

        var main = md[..md.IndexOf("### Position shifts", StringComparison.Ordinal)];
        Assert.Contains("| `Hero` | height |", main);
        Assert.DoesNotContain("offsetY", main);
        Assert.Contains("likely caused by `Hero` height `200` → `260`", md);
    }

    [Fact]
    public void Shifts_without_a_likely_cause_stay_in_the_main_table()
    {
        // 沒有元件不見、沒有尺寸改變:那可能就是真的移位,不藏起來
        var md = MarkdownReport.Render([Page([Node("Title", Shift()), Node("Logo", Shift("offsetX"))])], gateFail: true);

        Assert.Contains("| `Title` | offsetY |", md);
        Assert.DoesNotContain("knock-on", md);
    }

    [Fact]
    public void Cause_names_are_inert_in_the_comment()
    {
        // 圖層名來自設計稿,寫成連結或提及的字串只能留在程式碼區段裡
        var md = MarkdownReport.Render([Page([Node("Card", Shift())],
            new UnmatchedNode("[x](https://evil.example) @someone", "1", "text-not-found", new Box(0, 0, 40, 20)))], gateFail: true);

        Assert.Contains("likely caused by `[x](https://evil.example) @someone` not found on the page", md);
    }
}
