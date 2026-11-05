using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 自動排版容器裡的子元件順序(childOrder)與對齊(mainAxisOffset、crossAxisOffset)。
/// 都是軟落差。真瀏覽器那半見 GapLabTests 的 G4、G11。
/// </summary>
public class StructuralDiffTests
{
    private static readonly DiffEngine Engine = new(new Tolerances());

    /// <summary>設計稿:容器 + 三個水平排的子元件。頁面的子元件位置由 renderedX 決定。</summary>
    private static Dictionary<string, List<PropDiff>> Row(double[] renderedX, Func<DesignNode, DesignNode>? tweak = null, string? names = null)
    {
        var labels = names?.Split(',') ?? ["a", "b", "c"];
        var kids = labels.Select((n, i) =>
            Design($"k{i}", n, box: new Box(100 * i, 0, 80, 40), sizingH: "FIXED", sizingV: "FIXED")).ToArray();
        var row = Design("row", "row", box: new Box(0, 0, 400, 40), layoutMode: "HORIZONTAL", sizingH: "FIXED", sizingV: "FIXED", children: kids);
        if (tweak is not null) row = tweak(row);
        var root = Design("root", "root", box: new Box(0, 0, 800, 600), children: row);

        var renderedKids = renderedX.Select((x, i) => Rendered($"#k{i}", box: new Box(x, 0, 80, 40))).ToArray();
        var renderedRow = Rendered("#row", box: new Box(0, 0, 400, 40), children: renderedKids);
        var pairs = new List<NodePair> { new(row, renderedRow, "auto-name") };
        pairs.AddRange(row.Children.Zip(renderedKids, (d, r) => new NodePair(d, r, "auto-name")));
        return Engine.StructuralDiffs(root, Rendered("body", children: renderedRow), pairs);
    }

    // ---------- 順序 ----------

    [Fact]
    public void Swapped_children_are_a_soft_child_order_diff_on_the_container()
    {
        var result = Row([200, 100, 0]);

        var diff = Assert.Single(result["row"]);
        Assert.Equal("childOrder", diff.Prop);
        Assert.Equal("a, b, c", diff.Expected);
        Assert.Equal("c, b, a", diff.Actual);
        Assert.True(diff.Soft);
        Assert.Equal(Severity.Minor, diff.Severity);
    }

    [Fact]
    public void Same_order_is_not_a_diff_even_when_positions_differ()
        => Assert.Empty(Row([10, 150, 310]));

    [Fact]
    public void Children_within_a_pixel_of_each_other_do_not_count_as_swapped()
        => Assert.Empty(Row([100.5, 100, 200]));

    [Fact]
    public void Duplicate_layer_names_get_their_design_position_so_the_diff_is_readable()
    {
        var diff = Assert.Single(Row([200, 100, 0], names: "Card,Card,Card")["row"]);

        Assert.Equal("Card #1, Card #2, Card #3", diff.Expected);
        Assert.Equal("Card #3, Card #2, Card #1", diff.Actual);
    }

    [Fact]
    public void A_wrapping_container_is_skipped()
        => Assert.Empty(Row([200, 100, 0], row => row with { LayoutWrap = "WRAP" }));

    [Fact]
    public void Children_that_wrapped_onto_another_line_on_the_page_are_skipped()
    {
        // 頁面上換行了(交叉軸不重疊):先後沒有意義
        var a = Design("a", "a", box: new Box(0, 0, 80, 40));
        var b = Design("b", "b", box: new Box(100, 0, 80, 40));
        var row = Design("row", "row", box: new Box(0, 0, 400, 40), layoutMode: "HORIZONTAL", children: [a, b]);
        var ra = Rendered("#a", box: new Box(100, 0, 80, 40));
        var rb = Rendered("#b", box: new Box(0, 60, 80, 40));
        var rrow = Rendered("#row", box: new Box(0, 0, 400, 100), children: [ra, rb]);

        var result = Engine.StructuralDiffs(Design("root", "root", children: row), Rendered("body", children: rrow),
            [new(row, rrow, "auto-name"), new(a, ra, "auto-name"), new(b, rb, "auto-name")]);

        Assert.Empty(result);
    }

    [Fact]
    public void Absolutely_positioned_children_on_either_side_are_left_out()
    {
        // 設計稿標 ABSOLUTE 的不算;頁面上 position: absolute 的也不算
        var a = Design("a", "a", box: new Box(0, 0, 80, 40));
        var b = Design("b", "b", box: new Box(100, 0, 80, 40));
        var row = Design("row", "row", box: new Box(0, 0, 400, 40), layoutMode: "HORIZONTAL", children: [a, b]);
        var ra = Rendered("#a", box: new Box(100, 0, 80, 40)) with { OutOfFlow = true };
        var rb = Rendered("#b", box: new Box(0, 0, 80, 40));
        var rrow = Rendered("#row", box: new Box(0, 0, 400, 40), children: [ra, rb]);

        Assert.Empty(Engine.StructuralDiffs(Design("root", "root", children: row), Rendered("body", children: rrow),
            [new(row, rrow, "auto-name"), new(a, ra, "auto-name"), new(b, rb, "auto-name")]));

        var absolute = a with { LayoutPositioning = "ABSOLUTE" };
        var row2 = row with { Children = [absolute, b] };
        var rrow2 = rrow with { Children = [ra with { OutOfFlow = false }, rb] };
        Assert.Empty(Engine.StructuralDiffs(Design("root", "root", children: row2), Rendered("body", children: rrow2),
            [new(row2, rrow2, "auto-name"), new(absolute, ra, "auto-name"), new(b, rb, "auto-name")]));
    }

    [Fact]
    public void A_freely_positioned_parent_is_not_compared_because_relative_position_already_covers_it()
    {
        var a = Design("a", "a", box: new Box(0, 0, 80, 40));
        var b = Design("b", "b", box: new Box(100, 0, 80, 40));
        var free = Design("free", "free", box: new Box(0, 0, 400, 40), children: [a, b]);
        var ra = Rendered("#a", box: new Box(100, 0, 80, 40));
        var rb = Rendered("#b", box: new Box(0, 0, 80, 40));
        var rfree = Rendered("#free", box: new Box(0, 0, 400, 40), children: [ra, rb]);

        Assert.Empty(Engine.StructuralDiffs(Design("root", "root", children: free), Rendered("body", children: rfree),
            [new(free, rfree, "auto-name"), new(a, ra, "auto-name"), new(b, rb, "auto-name")]));
    }

    [Fact]
    public void A_container_with_only_padding_guesses_its_axis_from_the_design_boxes()
    {
        // 手寫 JSON 常有 padding 沒有 layoutMode:主軸照設計稿子元件的排列方向猜
        var a = Design("a", "a", box: new Box(0, 0, 80, 40));
        var b = Design("b", "b", box: new Box(0, 100, 80, 40));
        var column = Design("col", "col", box: new Box(0, 0, 80, 200), padding: new Insets(0, 0, 0, 0), children: [a, b]);
        var ra = Rendered("#a", box: new Box(0, 100, 80, 40));
        var rb = Rendered("#b", box: new Box(0, 0, 80, 40));
        var rcol = Rendered("#col", box: new Box(0, 0, 80, 200), children: [ra, rb]);

        var result = Engine.StructuralDiffs(Design("root", "root", children: column), Rendered("body", children: rcol),
            [new(column, rcol, "auto-name"), new(a, ra, "auto-name"), new(b, rb, "auto-name")]);

        Assert.Equal("childOrder", Assert.Single(result["col"]).Prop);
    }

    [Fact]
    public void On_the_root_the_diff_goes_to_the_first_child_because_the_root_has_no_result()
    {
        var a = Design("a", "a", box: new Box(0, 0, 80, 40));
        var b = Design("b", "b", box: new Box(100, 0, 80, 40));
        var root = Design("root", "root", box: new Box(0, 0, 800, 600), layoutMode: "HORIZONTAL", children: [a, b]);
        var ra = Rendered("#a", box: new Box(100, 0, 80, 40));
        var rb = Rendered("#b", box: new Box(0, 0, 80, 40));

        var result = Engine.StructuralDiffs(root, Rendered("body", box: new Box(0, 0, 800, 600), children: [ra, rb]),
            [new(a, ra, "auto-name"), new(b, rb, "auto-name")]);

        Assert.Equal("childOrder", Assert.Single(result["a"]).Prop);
    }

    // ---------- 對齊 ----------

    /// <summary>容器 400×120,一個 120×40 的子元件。</summary>
    private static Dictionary<string, List<PropDiff>> Aligned(
        Box designChild, Box renderedChild, Func<DesignNode, DesignNode>? tweakParent = null,
        DesignNodeType childType = DesignNodeType.Frame, Insets renderedPadding = default, Box? renderedParent = null,
        string childSizing = "FIXED")
    {
        var child = Design("chip", "chip", childType, box: designChild, sizingH: childSizing, sizingV: childSizing);
        var parent = Design("row", "row", box: new Box(0, 0, 400, 120), layoutMode: "HORIZONTAL", sizingH: "FIXED", sizingV: "FIXED", children: child);
        if (tweakParent is not null) parent = tweakParent(parent);
        var rchild = Rendered("#chip", box: renderedChild);
        var rparent = Rendered("#row", box: renderedParent ?? new Box(0, 0, 400, 120), padding: renderedPadding, children: rchild);
        return Engine.StructuralDiffs(Design("root", "root", children: parent), Rendered("body", children: rparent),
            [new(parent, rparent, "auto-name"), new(child, rchild, "auto-name")]);
    }

    private static readonly Box Centered = new(0, 40, 120, 40);
    private static readonly Box AtTop = new(0, 0, 120, 40);

    [Fact]
    public void A_child_off_on_the_cross_axis_is_a_soft_diff_on_the_child()
    {
        var diff = Assert.Single(Aligned(Centered, AtTop)["chip"]);

        Assert.Equal("crossAxisOffset", diff.Prop);
        Assert.Equal("40", diff.Expected);
        Assert.Equal("0", diff.Actual);
        Assert.Equal("px", diff.Unit);
        Assert.Equal(40, diff.Delta);
        Assert.True(diff.Soft);
    }

    [Fact]
    public void Offsets_within_the_position_tolerance_are_fine()
        => Assert.Empty(Aligned(Centered, new Box(0, 37, 120, 40)));

    [Fact]
    public void A_child_off_on_the_main_axis_is_compared_when_the_design_says_where_it_goes()
    {
        var diff = Assert.Single(Aligned(new Box(0, 0, 120, 40), new Box(140, 0, 120, 40),
            p => p with { PrimaryAxisAlignItems = "MIN" })["chip"]);

        Assert.Equal("mainAxisOffset", diff.Prop);
        Assert.Equal("0", diff.Expected);
        Assert.Equal("140", diff.Actual);
    }

    [Theory]
    [InlineData("SPACE_BETWEEN")]
    [InlineData(null)]
    public void The_main_axis_is_not_compared_for_other_alignments(string? alignment)
        => Assert.Empty(Aligned(new Box(0, 0, 120, 40), new Box(140, 0, 120, 40),
            p => p with { PrimaryAxisAlignItems = alignment }));

    [Fact]
    public void A_centered_row_whose_total_length_differs_is_not_reported_because_the_shift_has_another_cause()
    {
        // 置中的第一個子元件:位置取決於總長度。子元件寬度不一樣時,位移的根因是寬度,那條落差已經有了
        var diffs = Aligned(new Box(140, 0, 120, 40), new Box(100, 0, 200, 40), p => p with { PrimaryAxisAlignItems = "CENTER" });

        Assert.Empty(diffs);
    }

    [Fact]
    public void A_different_padding_is_its_own_diff_and_is_not_reported_again_as_alignment()
        => Assert.Empty(Aligned(Centered, AtTop, p => p with { Padding = new Insets(40, 0, 40, 0) }, renderedPadding: new Insets(0, 0, 0, 0)));

    [Fact]
    public void A_different_container_size_is_its_own_diff_and_is_not_reported_again_as_alignment()
        => Assert.Empty(Aligned(Centered, AtTop, renderedParent: new Box(0, 0, 400, 40)));

    [Fact]
    public void A_different_child_size_is_its_own_diff_and_is_not_reported_again_as_alignment()
        => Assert.Empty(Aligned(Centered, new Box(0, 0, 120, 120)));

    [Theory]
    [InlineData("HUG")]
    [InlineData("FILL")]
    public void Sizes_that_follow_the_content_or_the_parent_are_not_compared(string sizing)
        => Assert.Empty(Aligned(Centered, AtTop, childSizing: sizing));

    [Fact]
    public void Text_nodes_are_not_compared_because_text_boxes_measure_differently()
        => Assert.Empty(Aligned(Centered, AtTop, childType: DesignNodeType.Text));

    [Fact]
    public void Baseline_alignment_is_not_compared()
        => Assert.Empty(Aligned(Centered, AtTop, p => p with { CounterAxisAlignItems = "BASELINE" }));

    [Fact]
    public void Unknown_sizes_from_hand_written_designs_are_not_compared()
    {
        var child = Design("chip", "chip", box: Centered);
        var parent = Design("row", "row", box: new Box(0, 0, 400, 120), layoutMode: "HORIZONTAL", children: child);
        var rchild = Rendered("#chip", box: AtTop);
        var rparent = Rendered("#row", box: new Box(0, 0, 400, 120), children: rchild);

        Assert.Empty(Engine.StructuralDiffs(Design("root", "root", children: parent), Rendered("body", children: rparent),
            [new(parent, rparent, "auto-name"), new(child, rchild, "auto-name")]));
    }

    // ---------- 從引擎進入點 ----------

    private sealed class Fixed(DesignNode design, RenderedNode rendered) : IDesignSource, IImplementationSource
    {
        public Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default) => Task.FromResult(design);
        public Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default) => Task.FromResult(rendered);
    }

    private static async Task<FidelityReport> RunSwapped(EngineOptions options, int? viewportWidth = null)
    {
        var a = Design("a", "a", box: new Box(0, 0, 80, 40), sizingH: "FIXED", sizingV: "FIXED");
        var b = Design("b", "b", box: new Box(100, 0, 80, 40), sizingH: "FIXED", sizingV: "FIXED");
        var row = Design("row", "row", box: new Box(0, 0, 400, 40), layoutMode: "HORIZONTAL", sizingH: "FIXED", sizingV: "FIXED", children: [a, b]);
        var root = Design("root", "root", box: new Box(0, 0, 800, 600), children: row);
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
            Rendered("#row", domId: "row", box: new Box(0, 0, 400, 40), children:
            [
                Rendered("#a", domId: "a", box: new Box(100, 0, 80, 40)),
                Rendered("#b", domId: "b", box: new Box(0, 0, 80, 40)),
            ]));
        var source = new Fixed(root, rendered);
        return await new FidelityEngine(source, source, options)
            .RunAsync(new ScanRequest(new DesignRef("d", "root"), new ImplRef("http://x/", viewportWidth, viewportWidth is null ? null : 800)));
    }

    [Fact]
    public async Task The_engine_puts_the_diff_on_the_container_and_a_soft_one_does_not_touch_the_score()
    {
        var report = await RunSwapped(new EngineOptions(new Tolerances()));

        var row = report.Nodes.Single(n => n.DesignLayer == "row");
        Assert.Equal("childOrder", Assert.Single(row.Diffs).Prop);
        Assert.Equal(Severity.Minor, row.Severity);
        Assert.Equal(100, FidelityScore.Compute([report]));
    }

    [Fact]
    public async Task Compare_position_none_turns_it_off()
    {
        var report = await RunSwapped(new EngineOptions(new Tolerances()) { ComparePosition = false });

        Assert.Empty(report.Nodes.SelectMany(n => n.Diffs));
    }

    [Fact]
    public async Task A_viewport_wider_or_narrower_than_the_design_can_reflow_legitimately()
    {
        var report = await RunSwapped(new EngineOptions(new Tolerances()), viewportWidth: 375);

        Assert.DoesNotContain(report.Nodes.SelectMany(n => n.Diffs), d => d.Prop == "childOrder");
    }
}
