using System.Text.Json.Nodes;
using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;
using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 樣式屬性(斜體、底線、大小寫、對齊、行數、透明度、邊框、陰影、漸層)。全部是軟落差;
/// 任一邊沒有這項資訊就不比,所以舊快照與手寫 JSON 不會多出落差。真瀏覽器那半見 GapLabTests 的 G7～G10。
/// </summary>
public class StyleDiffTests
{
    private static List<PropDiff> Diffs(DesignNode design, RenderedNode rendered, string matchedBy = "auto-name")
        => new DiffEngine(new Tolerances()).Diff(new NodePair(design, rendered, matchedBy)).Diffs.ToList();

    private static DesignNode TextNode(Typography text, string characters = "Hello") => Design("1", "t", DesignNodeType.Text, text: text, characters: characters);

    private static RenderedNode Page(Typography typography, string? text = "Hello") => Rendered("p", "p", text: text, typography: typography);

    private static void AssertSoft(PropDiff d, string prop, string expected, string actual)
    {
        Assert.Equal(prop, d.Prop);
        Assert.Equal(expected, d.Expected);
        Assert.Equal(actual, d.Actual);
        Assert.True(d.Soft);
        Assert.Equal(Severity.Minor, d.Severity);
    }

    // ---------- 文字樣式 ----------

    [Fact]
    public void Font_style_is_compared()
        => AssertSoft(Assert.Single(Diffs(TextNode(new Typography { FontStyle = "normal" }), Page(new Typography { FontStyle = "italic" }))),
            "fontStyle", "normal", "italic");

    [Fact]
    public void Text_decoration_is_compared_regardless_of_word_order()
    {
        AssertSoft(Assert.Single(Diffs(TextNode(new Typography { TextDecoration = "none" }), Page(new Typography { TextDecoration = "underline" }))),
            "textDecoration", "none", "underline");
        Assert.Empty(Diffs(TextNode(new Typography { TextDecoration = "underline line-through" }), Page(new Typography { TextDecoration = "line-through underline" })));
    }

    [Fact]
    public void Text_align_is_compared()
        => AssertSoft(Assert.Single(Diffs(TextNode(new Typography { TextAlign = "center" }), Page(new Typography { TextAlign = "left" }))),
            "textAlign", "center", "left");

    [Fact]
    public void Text_transform_is_compared_when_it_changes_what_you_see()
        => AssertSoft(Assert.Single(Diffs(TextNode(new Typography { TextTransform = "none" }, "Hello"), Page(new Typography { TextTransform = "uppercase" }))),
            "textTransform", "none", "uppercase");

    [Theory]
    [InlineData("none", "uppercase", "HELLO", "hello")]   // 設計稿直接打大寫字母,頁面用 CSS 轉:畫面一樣
    [InlineData("none", "lowercase", "hello", "HELLO")]
    [InlineData("none", "capitalize", "Hello World", "hello world")]
    [InlineData("uppercase", "none", "hello", "HELLO")]   // 反過來:設計稿用 textCase,頁面直接打大寫
    [InlineData("uppercase", "none", "你好", "你好")]     // 中文沒有大小寫
    public void Text_transform_that_looks_the_same_is_not_a_diff(string design, string page, string designText, string pageText)
        => Assert.Empty(Diffs(TextNode(new Typography { TextTransform = design }, designText), Page(new Typography { TextTransform = page }, pageText)));

    [Fact]
    public void Text_style_missing_on_either_side_is_not_compared()
    {
        var empty = new Typography();
        var full = new Typography { FontStyle = "italic", TextDecoration = "underline", TextTransform = "uppercase", TextAlign = "right" };

        Assert.Empty(Diffs(TextNode(empty), Page(full)));
        Assert.Empty(Diffs(TextNode(full), Page(empty)));
    }

    // ---------- 行數 ----------

    private static DesignNode Lines(double boxHeight, double? lineHeight, string? autoResize = null) =>
        Design("1", "t", DesignNodeType.Text, box: new Box(0, 0, 300, boxHeight), text: new Typography(LineHeight: lineHeight), characters: "Hello") with { TextAutoResize = autoResize };

    private static RenderedNode PageLines(int? lines) => Rendered("p", "p", text: "Hello") with { LineCount = lines };

    [Fact]
    public void Line_count_comes_from_box_height_over_line_height()
    {
        var diff = Assert.Single(Diffs(Lines(24, 24), PageLines(5)));

        AssertSoft(diff, "lineCount", "1", "5");
        Assert.Equal(4, diff.Delta);
        Assert.Empty(Diffs(Lines(48, 24), PageLines(2)));
    }

    [Theory]
    [InlineData("NONE")]      // 固定高度:框高跟行數無關
    [InlineData("TRUNCATE")]
    public void A_fixed_height_text_box_says_nothing_about_lines(string autoResize)
        => Assert.Empty(Diffs(Lines(100, 24, autoResize), PageLines(1)));

    [Fact]
    public void Line_count_needs_both_sides()
    {
        Assert.Empty(Diffs(Lines(24, null), PageLines(5)));
        Assert.Empty(Diffs(Lines(24, 24), PageLines(null)));
    }

    [Fact]
    public void A_snapshot_uses_the_measured_line_count_and_an_old_snapshot_is_not_guessed()
    {
        var measured = Lines(48, 24) with { LineCount = 1 };
        Assert.Equal("lineCount", Assert.Single(Diffs(measured, PageLines(2), "selector")).Prop);

        // 舊快照沒有 LineCount:框含 padding,用框高算會誤報
        Assert.Empty(Diffs(Lines(48, 24), PageLines(1), "selector"));
    }

    // ---------- 透明度 ----------

    [Fact]
    public void Opacity_is_compared_with_a_small_tolerance()
    {
        var card = Design("1", "card") with { Opacity = 1 };

        var diff = Assert.Single(Diffs(card, Rendered("div") with { Opacity = 0.2 }));
        AssertSoft(diff, "opacity", "1", "0.2");
        Assert.Equal(0.8, diff.Delta);
        Assert.Empty(Diffs(card, Rendered("div") with { Opacity = 0.995 }));
    }

    // ---------- 邊框 ----------

    private static readonly Insets NoBorder = new(0, 0, 0, 0);
    private static readonly Rgba Black = new(0, 0, 0);
    private static readonly Rgba Red = new(255, 0, 0);

    [Fact]
    public void A_one_pixel_border_counts_even_though_it_is_inside_the_size_tolerance()
    {
        var diff = Assert.Single(Diffs(Design("1", "card") with { BorderWidth = NoBorder }, Rendered("div") with { BorderWidth = new Insets(1, 1, 1, 1) }));

        AssertSoft(diff, "borderWidth", "0", "1");
        Assert.Equal("px", diff.Unit);
    }

    [Fact]
    public void Border_sides_are_one_diff_and_list_every_side_when_they_differ()
    {
        var diff = Assert.Single(Diffs(Design("1", "card") with { BorderWidth = new Insets(0, 0, 1, 0) }, Rendered("div") with { BorderWidth = new Insets(3, 0, 0, 0) }));

        AssertSoft(diff, "borderWidth", "0 0 1 0", "3 0 0 0");
        Assert.Equal(3, diff.Delta);
    }

    [Fact]
    public void Border_color_is_compared_only_when_the_widths_agree()
    {
        var one = new Insets(1, 1, 1, 1);
        AssertSoft(Assert.Single(Diffs(
            Design("1", "card") with { BorderWidth = one, BorderColor = Black },
            Rendered("div") with { BorderWidth = one, BorderColor = Red })), "borderColor", "#000000", "#FF0000");

        // 粗細就不一樣了:同一個根因不報兩次
        Assert.Equal("borderWidth", Assert.Single(Diffs(
            Design("1", "card") with { BorderWidth = new Insets(4, 4, 4, 4), BorderColor = Black },
            Rendered("div") with { BorderWidth = one, BorderColor = Red })).Prop);
    }

    // ---------- 陰影 ----------

    private static ShadowSpec Shadow(double y, double blur, Rgba? color = null, bool inset = false) => new(0, y, blur, 0, color ?? Black, inset);

    [Fact]
    public void A_shadow_on_only_one_side_is_missing()
    {
        var added = Assert.Single(Diffs(Design("1", "card") with { Shadows = [] }, Rendered("div") with { Shadows = [Shadow(0, 30)] }));
        AssertSoft(added, "shadow", "none", "0px 0px 30px 0px");
        Assert.Equal(DiffStatus.Missing, added.Status);
        Assert.Null(added.Delta);

        Assert.Equal(DiffStatus.Missing, Assert.Single(Diffs(Design("1", "card") with { Shadows = [Shadow(4, 8)] }, Rendered("div") with { Shadows = [] })).Status);
    }

    [Fact]
    public void A_shadow_of_a_different_size_reports_the_largest_difference()
    {
        var diff = Assert.Single(Diffs(Design("1", "card") with { Shadows = [Shadow(4, 8)] }, Rendered("div") with { Shadows = [Shadow(4, 20)] }));

        Assert.Equal("shadow", diff.Prop);
        Assert.Equal(12, diff.Delta);
    }

    [Fact]
    public void Shadow_color_is_its_own_diff()
        => AssertSoft(Assert.Single(Diffs(Design("1", "card") with { Shadows = [Shadow(4, 8, Black)] }, Rendered("div") with { Shadows = [Shadow(4, 8, Red)] })),
            "shadowColor", "#000000", "#FF0000");

    [Fact]
    public void Shadow_layers_are_paired_regardless_of_their_order()
    {
        // Figma 由下往上、CSS 由上往下
        var small = Shadow(1, 2);
        var large = Shadow(10, 20);

        Assert.Empty(Diffs(Design("1", "card") with { Shadows = [small, large] }, Rendered("div") with { Shadows = [large, small] }));
    }

    [Fact]
    public void An_inner_shadow_is_not_a_drop_shadow()
        => Assert.Equal(DiffStatus.Missing, Assert.Single(Diffs(
            Design("1", "card") with { Shadows = [Shadow(4, 8, inset: true)] }, Rendered("div") with { Shadows = [Shadow(4, 8)] })).Status);

    [Fact]
    public void Text_nodes_compare_text_shadow_and_other_nodes_box_shadow()
    {
        var page = Rendered("p", "p", text: "Hello") with { Shadows = [Shadow(4, 8)], TextShadows = [] };

        Assert.Empty(Diffs(Design("1", "t", DesignNodeType.Text, characters: "Hello") with { Shadows = [] }, page));
        Assert.Single(Diffs(Design("1", "card") with { Shadows = [] }, page));
    }

    // ---------- 漸層 ----------

    private static readonly Rgba Blue = new(0, 0, 255);

    [Fact]
    public void A_gradient_on_only_the_design_side_is_missing()
    {
        var diff = Assert.Single(Diffs(Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) }, Rendered("div")));

        AssertSoft(diff, "gradient", "#FF0000 → #0000FF", "(no gradient)");
        Assert.Equal(DiffStatus.Missing, diff.Status);
    }

    [Fact]
    public void A_gradient_where_the_design_is_a_solid_fill_is_missing()
    {
        var page = Rendered("div", background: Blue) with { Gradient = new GradientSpec(Red, Blue) };

        AssertSoft(Assert.Single(Diffs(Design("1", "hero", fill: Blue), page)), "gradient", "(no gradient)", "#FF0000 → #0000FF");
    }

    [Fact]
    public void An_old_snapshot_is_not_assumed_to_have_no_gradient()
        => Assert.Empty(Diffs(Design("1", "hero", fill: Blue), Rendered("div", background: Blue) with { Gradient = new GradientSpec(Red, Blue) }, "selector"));

    [Fact]
    public void Gradient_end_colors_are_compared_but_not_their_direction()
    {
        var design = Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) };

        Assert.Empty(Diffs(design, Rendered("div") with { Gradient = new GradientSpec(Blue, Red) }));
        var diff = Assert.Single(Diffs(design, Rendered("div") with { Gradient = new GradientSpec(Red, Black) }));
        Assert.Equal("gradient", diff.Prop);
        Assert.NotNull(diff.Delta);
    }

    [Fact]
    public void A_gradient_whose_colors_could_not_be_read_only_counts_as_having_one()
        => Assert.Empty(Diffs(Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) }, Rendered("div") with { Gradient = new GradientSpec() }));

    // ---------- 舊資料 ----------

    [Fact]
    public void Nothing_is_compared_when_the_design_does_not_say()
    {
        var page = Rendered("div") with
        {
            Opacity = 0.2,
            BorderWidth = new Insets(4, 4, 4, 4),
            BorderColor = Red,
            Shadows = [Shadow(0, 30)],
            LineCount = 3,
        };

        Assert.Empty(Diffs(Design("1", "card"), page));
    }

    // ---------- CSS 解析 ----------

    [Theory]
    [InlineData("none", 0)]
    [InlineData("rgb(0, 0, 0) 0px 0px 30px 0px", 1)]
    [InlineData("rgba(0, 0, 0, 0.5) 0px 4px 8px 0px, rgb(255, 0, 0) 1px 1px 2px 0px inset", 2)]
    [InlineData("rgba(0, 0, 0, 0) 0px 0px 0px 0px, rgba(0, 0, 0, 0) 0px 0px 0px 0px", 0)] // Tailwind 的預設值
    [InlineData("rgb(0, 0, 0) 0px 0px 0px 0px", 0)]                                        // 沒有位移、模糊、擴散:看不見
    [InlineData("rgb(0, 0, 0) 1px 2px", 1)]                                                // text-shadow 沒有模糊也合法
    public void Shadows_are_parsed_and_invisible_ones_dropped(string css, int count)
        => Assert.Equal(count, CssEffects.ParseShadows(css)!.Count);

    [Fact]
    public void A_shadow_keeps_its_values()
    {
        var s = Assert.Single(CssEffects.ParseShadows("rgba(10, 20, 30, 0.5) 1px 2px 3px 4px inset")!);

        Assert.Equal(new ShadowSpec(1, 2, 3, 4, new Rgba(10, 20, 30, 0.5), true), s);
        Assert.Null(CssEffects.ParseShadows(null));
    }

    [Fact]
    public void Gradients_are_parsed_to_their_end_colors()
    {
        var g = CssEffects.ParseGradient("linear-gradient(90deg, rgb(255, 0, 0) 0%, rgb(0, 255, 0) 50%, rgb(0, 0, 255) 100%)")!;

        Assert.Equal(new Rgba(255, 0, 0), g.Start);
        Assert.Equal(new Rgba(0, 0, 255), g.End);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("url(\"https://x/a.png\")")]
    public void No_gradient_is_null(string? css) => Assert.Null(CssEffects.ParseGradient(css));

    [Fact]
    public void The_first_gradient_layer_is_used_and_unreadable_colors_still_count_as_a_gradient()
    {
        Assert.Equal(new Rgba(1, 2, 3), CssEffects.ParseGradient("url(x.png), linear-gradient(rgb(1, 2, 3), rgb(4, 5, 6)), linear-gradient(rgb(7, 8, 9), rgb(0, 0, 0))")!.Start);
        Assert.Null(CssEffects.ParseGradient("linear-gradient(lab(50 0 0), lab(60 0 0))")!.Start);
    }

    // ---------- Figma ----------

    private static DesignNode Figma(string json) => FigmaNodeParser.Parse(JsonNode.Parse(json)!);

    [Fact]
    public void Figma_text_style_defaults_are_known_values_not_unknown()
    {
        // Figma 在預設值時會省略這些欄位
        var t = Figma("""{ "id": "1", "name": "t", "type": "TEXT", "characters": "x", "style": { "fontSize": 16 } }""");

        Assert.Equal("normal", t.Text!.FontStyle);
        Assert.Equal("none", t.Text.TextDecoration);
        Assert.Equal("none", t.Text.TextTransform);
        Assert.Null(t.Text.TextAlign);
        Assert.Equal("NONE", t.TextAutoResize);
        Assert.Equal(1, t.Opacity);
    }

    [Theory]
    [InlineData("UNDERLINE", "underline")]
    [InlineData("STRIKETHROUGH", "line-through")]
    public void Figma_text_decoration_maps_to_css(string figma, string css)
        => Assert.Equal(css, Figma($$"""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textDecoration": "{{figma}}" } }""").Text!.TextDecoration);

    [Theory]
    [InlineData("UPPER", "uppercase")]
    [InlineData("LOWER", "lowercase")]
    [InlineData("TITLE", "capitalize")]
    [InlineData("SMALL_CAPS", "small-caps")]
    [InlineData("SMALL_CAPS_FORCED", "small-caps")]
    public void Figma_text_case_maps_to_css(string figma, string css)
        => Assert.Equal(css, Figma($$"""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textCase": "{{figma}}" } }""").Text!.TextTransform);

    [Fact]
    public void Figma_alignment_is_dropped_when_the_box_hugs_its_text()
    {
        Assert.Equal("center", Figma("""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textAlignHorizontal": "CENTER", "textAutoResize": "HEIGHT" } }""").Text!.TextAlign);
        Assert.Null(Figma("""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textAlignHorizontal": "CENTER", "textAutoResize": "WIDTH_AND_HEIGHT" } }""").Text!.TextAlign);
    }

    [Fact]
    public void Figma_strokes_become_a_border_only_on_box_like_nodes()
    {
        var frame = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 2,
              "strokes": [{ "type": "SOLID", "color": { "r": 1, "g": 0, "b": 0, "a": 1 } }] }
            """);
        Assert.Equal(new Insets(2, 2, 2, 2), frame.BorderWidth);
        Assert.Equal(new Rgba(255, 0, 0), frame.BorderColor);

        var each = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 2,
              "individualStrokeWeights": { "top": 0, "right": 0, "bottom": 1, "left": 0 },
              "strokes": [{ "type": "SOLID", "color": { "r": 0, "g": 0, "b": 0, "a": 1 } }] }
            """);
        Assert.Equal(new Insets(0, 0, 1, 0), each.BorderWidth);

        Assert.Equal(new Insets(0, 0, 0, 0), Figma("""{ "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 1, "strokes": [] }""").BorderWidth);
        Assert.Equal(new Insets(0, 0, 0, 0), Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 1,
              "strokes": [{ "type": "SOLID", "visible": false, "color": { "r": 0, "g": 0, "b": 0 } }] }
            """).BorderWidth);

        // 文字描邊、線條本身都不是 CSS 的 border
        const string Stroke = """ "strokeWeight": 2, "strokes": [{ "type": "SOLID", "color": { "r": 0, "g": 0, "b": 0 } }] """;
        Assert.Null(Figma($$"""{ "id": "1", "name": "t", "type": "TEXT", {{Stroke}} }""").BorderWidth);
        Assert.Null(Figma($$"""{ "id": "1", "name": "l", "type": "LINE", {{Stroke}} }""").BorderWidth);
    }

    [Fact]
    public void Figma_effects_become_shadows()
    {
        var node = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "effects": [
              { "type": "DROP_SHADOW", "color": { "r": 0, "g": 0, "b": 0, "a": 0.25 }, "offset": { "x": 0, "y": 4 }, "radius": 8, "spread": 2 },
              { "type": "INNER_SHADOW", "color": { "r": 1, "g": 0, "b": 0, "a": 1 }, "offset": { "x": 1, "y": 1 }, "radius": 2 },
              { "type": "DROP_SHADOW", "visible": false, "color": { "r": 0, "g": 0, "b": 0, "a": 1 }, "offset": { "x": 9, "y": 9 }, "radius": 9 },
              { "type": "LAYER_BLUR", "radius": 4 } ] }
            """);

        Assert.Equal([new ShadowSpec(0, 4, 8, 2, new Rgba(0, 0, 0, 0.25)), new ShadowSpec(1, 1, 2, 0, new Rgba(255, 0, 0), true)], node.Shadows!);
        Assert.Empty(Figma("""{ "id": "1", "name": "f", "type": "FRAME" }""").Shadows!);
    }

    [Fact]
    public void Figma_gradient_fills_are_read_in_stop_order()
    {
        var node = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "fills": [{ "type": "GRADIENT_LINEAR", "gradientStops": [
              { "position": 1, "color": { "r": 0, "g": 0, "b": 1, "a": 1 } },
              { "position": 0, "color": { "r": 1, "g": 0, "b": 0, "a": 1 } } ] }] }
            """);

        Assert.Equal(new GradientSpec(new Rgba(255, 0, 0), new Rgba(0, 0, 255)), node.Gradient);
        Assert.Null(Figma("""{ "id": "1", "name": "f", "type": "FRAME", "fills": [{ "type": "SOLID", "color": { "r": 1, "g": 1, "b": 1 } }] }""").Gradient);
    }

    // ---------- 快照 ----------

    [Fact]
    public void A_snapshot_freezes_the_new_style_values()
    {
        var shadow = Shadow(4, 8);
        var body = Rendered("body", children:
        [
            Rendered("#card", classes: "card") with
            {
                Opacity = 0.5, BorderWidth = new Insets(1, 1, 1, 1), BorderColor = Red, Shadows = [shadow], Gradient = new GradientSpec(Red, Blue),
            },
            Rendered("#title", "h1", text: "Title", typography: new Typography { FontStyle = "italic", TextTransform = "uppercase" }) with
            {
                LineCount = 2, Shadows = [shadow], TextShadows = [],
            },
        ]);

        var frame = SnapshotBuilder.ToFrame(body, "/");
        var card = frame.Children[0];
        var title = frame.Children[1];

        Assert.Equal(0.5, card.Opacity);
        Assert.Equal(new Insets(1, 1, 1, 1), card.BorderWidth);
        Assert.Equal(Red, card.BorderColor);
        Assert.Equal([shadow], card.Shadows!);
        Assert.Equal(new GradientSpec(Red, Blue), card.Gradient);
        Assert.Null(card.LineCount);
        Assert.Equal(2, title.LineCount);
        Assert.Empty(title.Shadows!); // 文字節點記 text-shadow
        Assert.Equal("italic", title.Text!.FontStyle);
    }
}
