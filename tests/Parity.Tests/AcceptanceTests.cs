using System.Text.Json;
using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// 設定檔 <c>accept</c>(2.5.0,競品清單「單筆落差豁免」):一條一條收下落差、每條帶原因。
/// 收下的落差留在報告裡,同時變成 soft——gate、分數、baseline 都照 soft 的規則走。
/// </summary>
public class AcceptanceTests
{
    private static ParityConfig LoadJson(string json)
    {
        using var dir = new TempDir();
        return ParityConfig.Load(dir.Write("parity.config.json", json));
    }

    private static AcceptConfig Entry(string layer = "Plan title", string prop = "fontSize", string reason = "designer approved 18px",
        string? route = null, string? selector = null, string? actual = null)
        => new() { Layer = layer, Prop = prop, Reason = reason, Route = route, Selector = selector, Actual = actual };

    private static PropDiff FontSize(string actual = "18") => new("fontSize", "16", actual, "px", 2, 0.5, Severity.Serious);

    private static PropDiff Padding() => new("paddingTop", "12", "8", "px", 4, 2, Severity.Serious);

    private static NodeResult Node(string layer, string selector, params PropDiff[] diffs)
        => new(layer, layer, selector, "auto-text", diffs.Max(d => d.Severity), diffs);

    private static FidelityReport Page(string route, params NodeResult[] nodes)
        => new(route, "url", "design", nodes, [],
            new ReportSummary(nodes.Length, nodes.Length, 0, nodes.Length, 0, nodes.Sum(n => n.Diffs.Count), 0, 0, Severity.Serious));

    private static FidelityReport Apply(FidelityReport report, params AcceptConfig[] entries)
        => Acceptance.Apply(report, entries, new HashSet<int>());

    // ---------- 設定檔 ----------

    [Fact]
    public void No_accept_key_means_an_empty_list()
        => Assert.Empty(LoadJson("{}").Accept);

    [Fact]
    public void An_entry_loads_with_its_optional_fields()
    {
        var config = LoadJson("""
            { "accept": [ { "route": "/pricing", "layer": "Plan title", "selector": "#plan h2", "prop": "fontSize", "actual": "18", "reason": "approved" } ] }
            """);

        var entry = Assert.Single(config.Accept);
        Assert.Equal(("/pricing", "Plan title", "#plan h2", "fontSize", "18", "approved"),
            (entry.Route, entry.Layer, entry.Selector, entry.Prop, entry.Actual, entry.Reason));
    }

    [Theory]
    [InlineData("""{ "accept": null }""", "\"accept\" is null")]
    [InlineData("""{ "accept": [ null ] }""", "\"accept[0]\" is null")]
    [InlineData("""{ "accept": [ { "prop": "fontSize", "reason": "x" } ] }""", "accept[0] needs a \"layer\"")]
    [InlineData("""{ "accept": [ { "layer": "A", "reason": "x" } ] }""", "accept[0] needs a \"prop\"")]
    [InlineData("""{ "accept": [ { "layer": "A", "prop": "fontSize" } ] }""", "accept[0] needs a \"reason\"")]
    [InlineData("""{ "accept": [ { "layer": "A", "prop": "fontSize", "reason": "  " } ] }""", "accept[0] needs a \"reason\"")]
    [InlineData("""{ "accept": [ { "layer": "A", "prop": "fontSize", "reason": "x" }, { "layer": "B", "prop": "font-size", "reason": "x" } ] }""", "accept[1].prop is \"font-size\"")]
    [InlineData("""{ "accept": [ { "layer": "A", "prop": "FontSize", "reason": "x" } ] }""", "accept[0].prop is \"FontSize\"")]
    public void A_broken_entry_is_a_load_error_that_names_it(string json, string message)
    {
        // 拼錯的屬性名永遠對不到——收下的落差會靜靜地照擋,所以載入時就報錯(exit 2)
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson(json));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Every_property_the_engine_reports_can_be_accepted()
    {
        foreach (var (prop, _) in UsageStats.PropFamilies)
            Assert.Single(LoadJson($$"""{ "accept": [ { "layer": "A", "prop": "{{prop}}", "reason": "x" } ] }""").Accept);
    }

    // ---------- 套用 ----------

    [Fact]
    public void An_accepted_gap_stays_in_the_report_marked_soft_with_its_reason()
    {
        var report = Apply(Page("/", Node("Plan title", "h2", FontSize(), Padding())), Entry());

        var diffs = Assert.Single(report.Nodes).Diffs;
        Assert.Equal(("fontSize", true, "designer approved 18px"), (diffs[0].Prop, diffs[0].Soft, diffs[0].Accepted));
        Assert.Equal(Severity.Serious, diffs[0].Severity); // 落差是真的,嚴重度照實留著
        Assert.Equal((false, null), (diffs[1].Soft, diffs[1].Accepted)); // 同一個元素的其他落差不受影響
    }

    [Fact]
    public void An_accepted_gap_does_not_gate_or_cost_points()
    {
        var config = new ParityConfig();
        var before = Page("/", Node("Plan title", "h2", FontSize()));
        var after = Apply(before, Entry());

        Assert.True(config.ShouldFail(before));
        Assert.False(config.ShouldFail(after));
        Assert.Equal(100, FidelityScore.Compute([after]));
    }

    [Fact]
    public void An_accepted_gap_is_not_saved_into_a_baseline_and_is_not_new_against_one()
    {
        var accepted = Apply(Page("/", Node("Plan title", "h2", FontSize())), Entry());

        Assert.Empty(DiffRecord.FromReports([accepted]));
        // 收下之前存的基準裡有這一列:現在落差還在,不算修好
        var oldBaseline = DiffRecord.FromReports([Page("/", Node("Plan title", "h2", FontSize()))]);
        var cmp = BaselineComparer.Compare(DiffRecord.FromReports([accepted]), oldBaseline, DiffRecord.KeysOf([accepted]));
        Assert.Equal((0, 0, 1), (cmp.Regressions.Count, cmp.Fixed.Count, cmp.Ignored));
    }

    [Theory]
    [InlineData("Plan title", "fontSize", null, null, null, true)]
    [InlineData("plan title", "fontSize", null, null, null, false)] // 圖層名大小寫要對
    [InlineData("Plan title", "lineHeight", null, null, null, false)]
    [InlineData("Plan title", "fontSize", "/pricing", null, null, true)]
    [InlineData("Plan title", "fontSize", "/", null, null, false)]
    [InlineData("Plan title", "fontSize", null, "#plan h2", null, true)]
    [InlineData("Plan title", "fontSize", null, "#other h2", null, false)]
    [InlineData("Plan title", "fontSize", null, null, "18", true)]
    [InlineData("Plan title", "fontSize", null, null, "20", false)] // 值又變了:落差回來
    public void Route_selector_and_actual_narrow_what_an_entry_accepts(
        string layer, string prop, string? route, string? selector, string? actual, bool accepted)
    {
        var report = Apply(Page("/pricing", Node("Plan title", "#plan h2", FontSize("18"))),
            Entry(layer, prop, route: route, selector: selector, actual: actual));

        Assert.Equal(accepted, Assert.Single(Assert.Single(report.Nodes).Diffs).Accepted is not null);
    }

    [Fact]
    public void Duplicate_layer_names_are_told_apart_by_selector()
    {
        var report = Apply(Page("/", Node("Title", "#a", FontSize()), Node("Title", "#b", FontSize())), Entry("Title", selector: "#b"));

        Assert.Equal([null, "designer approved 18px"], report.Nodes.Select(n => n.Diffs[0].Accepted));
    }

    [Fact]
    public void The_first_matching_entry_gives_the_reason_and_every_matching_entry_counts_as_used()
    {
        var used = new HashSet<int>();
        var report = Acceptance.Apply(Page("/", Node("Plan title", "h2", FontSize())),
            [Entry(reason: "first"), Entry(reason: "second"), Entry(prop: "color")], used);

        Assert.Equal("first", report.Nodes[0].Diffs[0].Accepted);
        Assert.Equal([0, 1], used.Order());
    }

    [Fact]
    public void Without_entries_the_report_is_returned_unchanged()
    {
        var report = Page("/", Node("Plan title", "h2", FontSize()));
        Assert.Same(report, Acceptance.Apply(report, [], new HashSet<int>()));
        Assert.Same(report, Apply(report, Entry("Other")));
    }

    // ---------- 過期的條目 ----------

    [Fact]
    public void An_entry_that_matched_nothing_on_a_scanned_page_gets_a_note()
    {
        AcceptConfig[] entries = [Entry(), Entry("Hero", "color", actual: "#FF0000", route: "/")];
        var notes = Acceptance.StaleNotes(entries, new HashSet<int>(), new HashSet<string> { "/" }, scannedAll: true);

        Assert.Equal(2, notes.Count);
        Assert.StartsWith("note: accept[0] (Plan title fontSize) matched no gap — it looks fixed.", notes[0]);
        Assert.StartsWith("note: accept[1] (Hero color at #FF0000 on /) matched no gap — it is fixed or the value changed.", notes[1]);
    }

    [Fact]
    public void Entries_for_pages_that_were_not_scanned_get_no_note()
    {
        // --target 只掃 /:寫了 /pricing 的不判斷,沒寫 route 的也不判斷(它對的可能是別頁)
        AcceptConfig[] entries = [Entry(route: "/pricing"), Entry(), Entry(route: "/")];
        var notes = Acceptance.StaleNotes(entries, new HashSet<int>(), new HashSet<string> { "/" }, scannedAll: false);

        Assert.StartsWith("note: accept[2] ", Assert.Single(notes));
    }

    [Fact]
    public void A_used_entry_gets_no_note_and_config_text_is_made_safe()
    {
        AcceptConfig[] entries = [Entry(), Entry("Evil\n::error::x")];
        var notes = Acceptance.StaleNotes(entries, new HashSet<int> { 0 }, new HashSet<string> { "/" }, scannedAll: true);

        Assert.DoesNotContain('\n', Assert.Single(notes));
    }

    // ---------- 呈現 ----------

    [Fact]
    public void Report_json_writes_accepted_only_when_there_is_a_reason()
    {
        var plain = JsonSerializer.Serialize(FontSize(), ReportWire.Compact);
        var accepted = JsonSerializer.Serialize(FontSize() with { Soft = true, Accepted = "approved" }, ReportWire.Compact);

        Assert.DoesNotContain("accepted", plain); // 沒用 accept 的人,report.json 一個位元組都不變
        Assert.EndsWith(",\"soft\":true,\"accepted\":\"approved\"}", accepted);
        Assert.Equal("approved", JsonSerializer.Deserialize<PropDiff>(accepted, ReportWire.Compact)!.Accepted);
        Assert.Null(JsonSerializer.Deserialize<PropDiff>(plain, ReportWire.Compact)!.Accepted);
    }

    [Fact]
    public void Markdown_moves_accepted_gaps_out_of_the_main_table_into_their_own_section()
    {
        var report = Apply(Page("/", Node("Plan title", "h2", FontSize(), Padding())), Entry(reason: "see [link](https://evil.example) @someone"));

        var md = MarkdownReport.Render([report], gateFail: false);

        var main = md[..md.IndexOf("### Accepted gaps", StringComparison.Ordinal)];
        Assert.Contains("paddingTop", main);
        Assert.DoesNotContain("fontSize", main);
        Assert.Contains("### Accepted gaps (1)", md);
        // 原因是設定檔寫的字:放進程式碼區段,不變成連結或提及
        Assert.Contains("`see [link](https://evil.example) @someone`", md);
    }

    [Fact]
    public void Markdown_has_no_accepted_section_when_nothing_was_accepted()
        => Assert.DoesNotContain("Accepted gaps", MarkdownReport.Render([Page("/", Node("Plan title", "h2", FontSize()))], gateFail: true));
}
