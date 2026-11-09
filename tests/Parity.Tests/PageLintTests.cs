using Parity.Engine;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// <c>parity lint --page</c>(2.5.0,競品清單「實作端 token 合規、near-miss 分級」):頁面實際用到的值對 token 檔。
/// 校準過的兩個真實狀況各有一條:半透明的 token 色(作品集的分隔線)、名字看得出種類的尺寸 token(字級不該對到間距)。
/// </summary>
public class PageLintTests
{
    private static readonly DesignTokens Tokens = new(new Dictionary<string, string>
    {
        ["brand"] = "#2563EB",
        ["ink"] = "#1D1A15",
        ["font-size-lg"] = "18px",
        ["font-size-2xl"] = "32px",
        ["space-4"] = "16px",
        ["space-5"] = "20px",
    });

    private static Rgba Hex(string hex) => Rgba.TryParseCss(hex, out var c) ? c : throw new ArgumentException(hex);

    private static RenderedNode Node(string selector, string? text = null, string? color = null, string? background = null,
        double? fontSize = null, Insets padding = default, double? radius = null, string? border = null)
        => new(selector, "div", text, new Box(0, 0, 100, 40), color is null ? null : Hex(color), background is null ? null : Hex(background),
            fontSize is null ? null : new Typography(FontSize: fontSize), padding, [])
        {
            CornerRadius = radius,
            BorderWidth = border is null ? null : new Insets(1, 1, 1, 1),
            BorderColor = border is null ? null : Hex(border),
        };

    private static IReadOnlyList<PageLintFinding> Lint(params RenderedNode[] children)
        => PageLint.Run(new RenderedNode("body", "body", null, new Box(0, 0, 800, 600), null, null, null, default, children), Tokens);

    [Fact]
    public void Values_on_a_token_pass()
    {
        Assert.Empty(Lint(
            Node("h1", "Title", color: "#1D1A15", fontSize: 32),
            Node("button", "Go", color: "#2563EB", background: "#2563EB", padding: new Insets(16, 20, 16, 20)),
            Node("p", "x", color: "#2563EC"))); // ΔE 在容差內:匯出/取樣的微差不算
    }

    [Fact]
    public void A_value_a_hair_from_a_token_is_a_near_miss_with_the_token_to_use()
    {
        var f = Assert.Single(Lint(Node("h1", "Title", fontSize: 30)));

        Assert.Equal(("fontSize", "30px", true, "font-size-2xl", "32px", 2.0), (f.Prop, f.Value, f.NearMiss, f.NearestToken, f.NearestValue, f.Distance));
    }

    [Fact]
    public void A_value_far_from_every_token_is_off_token_not_a_near_miss()
    {
        var f = Assert.Single(Lint(Node("div.card", background: "#F3F4F6")));

        Assert.Equal(("background", false), (f.Prop, f.NearMiss));
    }

    [Fact]
    public void Font_sizes_are_only_compared_with_font_tokens()
    {
        // 19px 離 space-5(20)只差 1,但那是間距 token:字級該對的是 font-size-lg(18),差 1 → near miss 指向字級 token
        var f = Assert.Single(Lint(Node("h3", "Card", fontSize: 19)));
        Assert.Equal("font-size-lg", f.NearestToken);
        // 20px 的內距照常命中 space-5;20px 的字級不會因為有 space-5 就算過
        Assert.Equal("fontSize", Assert.Single(Lint(Node("h2", "Sub", fontSize: 20, padding: new Insets(20, 20, 20, 20)))).Prop);
    }

    [Fact]
    public void A_kind_with_no_tokens_is_not_checked()
        => Assert.Empty(Lint(Node("div", background: "#2563EB", radius: 7))); // 沒有任何圓角 token

    [Theory]
    [InlineData("radius-md", "cornerRadius")]
    [InlineData("rounded-lg", "cornerRadius")]
    [InlineData("font-size-sm", "fontSize")]
    [InlineData("text-xl", "fontSize")]
    [InlineData("spacing-2", "padding")]
    [InlineData("gap-lg", "padding")]
    [InlineData("size-4", null)]
    [InlineData("brand", null)]
    public void Token_kinds_come_from_the_name(string name, string? kind)
        => Assert.Equal(kind, PageLint.KindOf(name));

    [Fact]
    public void A_token_color_at_reduced_opacity_counts_as_the_token()
    {
        // 作品集實測:分隔線是 rgba(29,26,21,.14),也就是 ink 降透明度。ΔE 先合成到白底會變成「像紙色」,以前整頁都是假的 near miss
        Assert.Empty(Lint(Node("hr", border: "rgba(29,26,21,0.14)"), Node("p", "x", color: "rgba(37,99,235,0.6)")));
    }

    [Fact]
    public void Each_value_is_reported_once_with_a_count_and_a_few_examples()
    {
        var f = Assert.Single(Lint(
            Node("#a", "x", color: "#E2795F"), Node("#b", "x", color: "#E2795F"), Node("#c", "x", color: "#E2795F"),
            Node("#d", "x", color: "#E2795F"), Node("#d", "y", color: "#E2795F")));

        Assert.Equal(5, f.Count);
        Assert.Equal(["#a", "#b", "#c"], f.Examples);
    }

    [Fact]
    public void Near_misses_come_first_then_the_most_used_values()
    {
        var findings = Lint(
            Node("#a", background: "#F3F4F6"), Node("#b", background: "#F3F4F6"),
            Node("#c", background: "#DCFCE7"),
            Node("h1", "T", fontSize: 31));

        Assert.Equal(["31px", "#F3F4F6", "#DCFCE7"], findings.Select(f => f.Value));
    }

    [Fact]
    public void Transparent_zero_and_textless_values_are_skipped()
    {
        Assert.Empty(Lint(
            Node("div", background: "rgba(0,0,0,0)", padding: new Insets(0, 0, 0, 0), radius: 0),
            Node("div.icon", color: "#FF0000", fontSize: 13))); // 沒有自己的字:文字色與字級不看
    }

    [Theory]
    [InlineData(false, 2, 16, true)]   // 16 寫成 14
    [InlineData(false, 2, 6, false)]   // 4 寫成 6:差 2,但超過值的四分之一
    [InlineData(false, 3, 30, false)]
    [InlineData(true, 5.9, 0, true)]
    [InlineData(true, 6.1, 0, false)]
    public void Near_miss_thresholds(bool isColor, double distance, double value, bool near)
        => Assert.Equal(near, TokenDistance.IsNearMiss(isColor, distance, value));
}
