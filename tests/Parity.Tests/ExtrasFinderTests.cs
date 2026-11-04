using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// compare.extras = "list":實作端多出來的元件(<see cref="ExtrasFinder"/>)。走 FidelityEngine 的完整路徑,
/// 一併驗證「只列出」——summary、nodes、unmatched 與沒開時一模一樣。真瀏覽器的驗收在 CliBrowserTests。
/// </summary>
public class ExtrasFinderTests
{
    private sealed class StubDesign(DesignNode root) : IDesignSource
    {
        public Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default) => Task.FromResult(root);
    }

    private sealed class StubImpl(RenderedNode root) : IImplementationSource
    {
        public Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default) => Task.FromResult(root);
    }

    private static readonly Rgba Red = new(255, 0, 0);
    private static readonly Rgba Clear = new(0, 0, 0, 0);

    /// <summary>設計稿只有一個標題(文字錨點配得上);frame 800×600 = 視窗。</summary>
    private static DesignNode TitleOnlyDesign(params DesignNode[] more) => Design("f", "frame", box: new Box(0, 0, 800, 600),
        children: [Design("t", "Title", DesignNodeType.Text, box: new Box(0, 300, 200, 40), characters: "Welcome"), .. more]);

    private static RenderedNode Title => Rendered("body > h1", "h1", "Welcome", new Box(0, 300, 200, 40));

    private static RenderedNode Body(params RenderedNode[] children)
        => Rendered("body", "body", box: new Box(0, 0, 800, 600), children: children);

    private static async Task<FidelityReport> Run(DesignNode design, RenderedNode body, bool list = true, ImplRef? impl = null)
    {
        var engine = new FidelityEngine(new StubDesign(design), new StubImpl(body),
            new EngineOptions(new Tolerances()) { ListExtras = list });
        return await engine.RunAsync(new ScanRequest(new DesignRef("d.json", "f"), impl ?? new ImplRef("http://x/")));
    }

    private static List<string> Listed(FidelityReport r) => r.Extras!.Items.Select(i => i.Selector).ToList();

    [Fact]
    public async Task Off_by_default_the_report_carries_null()
    {
        var engine = new FidelityEngine(new StubDesign(TitleOnlyDesign()), new StubImpl(Body(Title)), EngineOptions.Default);

        var report = await engine.RunAsync(new ScanRequest(new DesignRef("d.json", "f"), new ImplRef("http://x/")));

        Assert.False(EngineOptions.Default.ListExtras);
        Assert.Null(report.Extras);
    }

    [Fact]
    public async Task A_red_banner_the_design_never_had_is_listed_and_nothing_else_changes()
    {
        var banner = Rendered("body > div", background: Red, box: new Box(0, 0, 800, 200));
        var body = Body(banner, Title);

        var on = await Run(TitleOnlyDesign(), body);
        var off = await Run(TitleOnlyDesign(), body, list: false);

        var item = Assert.Single(on.Extras!.Items);
        Assert.Equal(1, on.Extras.Total);
        Assert.Equal(("body > div", "div", new Box(0, 0, 800, 200)), (item.Selector, item.Tag, item.Box));
        // 只列出:除了 extras 之外,整份報告(summary 是 gate 與分數的來源)跟沒開時逐位元相同
        Assert.Null(off.Extras);
        Assert.Equal(off.Summary, on.Summary);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(off, ReportWire.Compact),
            System.Text.Json.JsonSerializer.Serialize(on with { Extras = null }, ReportWire.Compact));
    }

    [Fact]
    public async Task Turned_on_with_nothing_extra_is_an_empty_list_not_null()
    {
        var report = await Run(TitleOnlyDesign(), Body(Title));

        Assert.Equal(0, report.Extras!.Total);
        Assert.Empty(report.Extras.Items);
    }

    [Fact]
    public async Task Ancestors_of_matched_nodes_are_layout_not_extras()
    {
        // 有背景的 wrapper 包著已配對的標題:它是版面容器,不是多出來的東西
        var wrapper = Rendered("body > main", "main", background: Red, box: new Box(0, 0, 800, 600), children: Title);

        var report = await Run(TitleOnlyDesign(), Body(wrapper));

        Assert.Equal(0, report.Extras!.Total);
    }

    [Fact]
    public async Task Whatever_sits_inside_a_matched_design_leaf_belongs_to_it()
    {
        // 設計端「Card」是一個沒有子節點的方塊;實作裡面怎麼拆(一張大圖)是實作細節
        var design = TitleOnlyDesign(Design("c", "Card", box: new Box(0, 0, 400, 280)));
        var card = Rendered("body > div.card", classes: "card", box: new Box(0, 0, 400, 280),
            children: Rendered("body > div.card > img", "img", box: new Box(0, 0, 400, 200)));

        var report = await Run(design, Body(card, Title));

        Assert.Contains(report.Nodes, n => n.DesignLayer == "Card");
        Assert.Equal(0, report.Extras!.Total);
    }

    [Fact]
    public async Task A_matched_container_whose_design_children_all_went_unmatched_is_not_reported_twice()
    {
        // 設計端 Card 的子節點(Caption)沒配到,已經在 unmatched 清單裡;實作端卡片裡的內容不再報成多出來
        var design = TitleOnlyDesign(Design("c", "Card", box: new Box(0, 0, 400, 280),
            children: Design("cap", "Caption", DesignNodeType.Text, box: new Box(0, 0, 300, 40), characters: "Not on the page")));
        var card = Rendered("body > div.card", classes: "card", box: new Box(0, 0, 400, 280),
            children: Rendered("body > div.card > p", "p", "Something else", new Box(0, 0, 300, 40)));

        var report = await Run(design, Body(card, Title));

        Assert.Contains(report.Unmatched, u => u.DesignLayer == "Caption");
        Assert.Equal(0, report.Extras!.Total);
    }

    [Fact]
    public async Task Inside_a_matched_container_with_matched_children_an_extra_sibling_is_listed()
    {
        var design = Design("f", "frame", box: new Box(0, 0, 800, 600), children:
            Design("h", "Hero", box: new Box(0, 0, 800, 400), children:
            [
                Design("a", "Heading", DesignNodeType.Text, box: new Box(0, 0, 300, 40), characters: "Big heading"),
                Design("b", "Lead", DesignNodeType.Text, box: new Box(0, 60, 300, 24), characters: "Lead text"),
            ]));
        var hero = Rendered("body > section", "section", box: new Box(0, 0, 800, 400), children:
        [
            Rendered("body > section > h1", "h1", "Big heading", new Box(0, 0, 300, 40)),
            Rendered("body > section > p", "p", "Lead text", new Box(0, 60, 300, 24)),
            Rendered("body > section > aside", "aside", "Limited offer!", new Box(0, 100, 300, 80)),
        ]);

        var report = await Run(design, Body(hero));

        Assert.Contains(report.Nodes, n => n.DesignLayer == "Hero" && n.MatchedBy == "auto-container");
        Assert.Equal(["body > section > aside"], Listed(report));
    }

    [Fact]
    public async Task Only_the_outermost_visible_element_of_a_nested_block_is_listed()
    {
        // 透明 wrapper 本身不算(看不見),往下找;有背景的卡片入選後不再往下,裡面的字與圖不另外列
        var block = Rendered("body > div", background: Clear, box: new Box(0, 0, 800, 300), children:
            Rendered("body > div > div", background: Red, box: new Box(0, 0, 400, 300), children:
            [
                Rendered("body > div > div > p", "p", "Promo text", new Box(0, 0, 400, 40)),
                Rendered("body > div > div > img", "img", box: new Box(0, 40, 400, 200)),
            ]));

        var report = await Run(TitleOnlyDesign(), Body(block, Title));

        Assert.Equal(["body > div > div"], Listed(report));
        Assert.Equal(1, report.Extras!.Total);
    }

    [Fact]
    public async Task Text_images_and_svg_count_as_visible_without_a_background()
    {
        var body = Body(Title,
            Rendered("body > p", "p", "Free shipping on every order", new Box(0, 0, 400, 24)),
            Rendered("body > img", "img", box: new Box(0, 40, 200, 100)),
            Rendered("body > svg", "svg", box: new Box(0, 150, 100, 100)),
            Rendered("body > div", background: Clear, box: new Box(400, 0, 400, 300))); // 透明、沒內容:看不見

        var report = await Run(TitleOnlyDesign(), body);

        Assert.Equal(["body > img", "body > svg", "body > p"], Listed(report)); // 面積大到小:20000、10000、9600
    }

    [Fact]
    public async Task Elements_under_the_area_threshold_are_not_listed()
    {
        var body = Body(Title,
            Rendered("body > i.small", "i", background: Red, box: new Box(0, 0, 63, 64)),   // 4032 < 64×64
            Rendered("body > i.big", "i", background: Red, box: new Box(100, 0, 64, 64)),   // 剛好 64×64
            Rendered("body > hr", "hr", background: Red, box: new Box(0, 100, 800, 1)));    // 長但細:800

        var report = await Run(TitleOnlyDesign(), body);

        Assert.Equal(4096, ExtrasFinder.MinArea);
        Assert.Equal(["body > i.big"], Listed(report));
    }

    [Fact]
    public async Task Only_the_part_inside_the_viewport_counts()
    {
        // 視窗 800×600:整個在視窗下方的不列;一半在視窗外的,只算視窗內那一塊(800×4 = 3200,不到門檻)
        var body = Rendered("body", "body", box: new Box(0, 0, 800, 2000), children:
        [
            Title,
            Rendered("body > footer", "footer", background: Red, box: new Box(0, 1200, 800, 400)),
            Rendered("body > div.peek", background: Red, box: new Box(0, 596, 800, 200)),
            Rendered("body > div.right", background: Red, box: new Box(780, 0, 400, 400)), // 視窗內 20×400 = 8000
        ]);

        var report = await Run(TitleOnlyDesign(), body);

        Assert.Equal(["body > div.right"], Listed(report));
    }

    [Fact]
    public async Task The_viewport_is_placed_in_body_relative_coordinates()
    {
        // body 有 8px margin(擷取座標是頁面座標):頁面 y = 600 起的元素在 800×600 的視窗外。
        // 正規化後它在 y = 592——視窗若沒跟著減掉 body 原點,會誤算成有 8px 露在視窗裡(784×8 = 6272 ≥ 門檻)
        var body = Rendered("body", "body", box: new Box(8, 8, 784, 2000), children:
        [
            Rendered("body > h1", "h1", "Welcome", new Box(8, 308, 200, 40)),
            Rendered("body > div", background: Red, box: new Box(8, 600, 784, 100)),
        ]);

        var report = await Run(TitleOnlyDesign(), body);

        Assert.Equal(0, report.Extras!.Total);
    }

    [Fact]
    public async Task Items_are_largest_first_and_capped_while_total_counts_them_all()
    {
        var blocks = Enumerable.Range(1, ExtrasFinder.MaxItems + 5)
            .Select(i => Rendered($"body > div:nth-of-type({i})", background: Red, box: new Box(0, 0, 100, 60 + i)))
            .ToArray();

        var report = await Run(TitleOnlyDesign(), Body([Title, .. blocks]));

        Assert.Equal(ExtrasFinder.MaxItems + 5, report.Extras!.Total);
        Assert.Equal(ExtrasFinder.MaxItems, report.Extras.Items.Count);
        Assert.Equal($"body > div:nth-of-type({ExtrasFinder.MaxItems + 5})", report.Extras.Items[0].Selector);
        Assert.Equal(report.Extras.Items.OrderByDescending(i => i.Box.W * i.Box.H).ToList(), report.Extras.Items);
    }

    [Fact]
    public async Task With_nothing_matched_at_all_the_page_is_still_walked_from_the_body()
    {
        // 0 配對時 gate 是 exit 3;清單照樣列,不因為沒有配對就整頁略過或把 body 本身列進來
        var banner = Rendered("body > div", background: Red, box: new Box(0, 0, 800, 200));

        var report = await Run(TitleOnlyDesign(), Body(banner));

        Assert.Equal(0, report.Summary.Matched);
        Assert.Equal(["body > div"], Listed(report));
    }
}
