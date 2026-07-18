using Parity.Engine;
using Parity.Engine.DesignSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>design lint:設計稿的值要落在 design token 允許集合(顏色 ΔE 容差、尺寸精確)。</summary>
public class DesignLintTests
{
    private static readonly DesignTokens Tokens = new(new Dictionary<string, string>
    {
        ["color-primary"] = "#2563EB",
        ["font-size-2xl"] = "32px",
        ["space-5"] = "20px",
    });

    [Fact]
    public void Off_token_color_reports_nearest()
    {
        // #2564EC 跟 color-primary 只差一咪咪但超出 ΔE 容差?不——先驗「明顯外」的:紅色
        var root = Design("1", "frame", box: new Box(0, 0, 100, 100),
            children: Design("2", "cta", fill: new Rgba(0xDC, 0x26, 0x26)));

        var v = Assert.Single(DesignLint.Run(root, Tokens));
        Assert.Equal("cta", v.Layer);
        Assert.Equal("color", v.Prop);
        Assert.Equal("#DC2626", v.Value);
        Assert.Equal("color-primary", v.NearestToken); // 唯一的顏色 token,附上讓人「改成這個」
    }

    [Fact]
    public void Near_token_color_within_deltaE_passes()
    {
        // #2564EC vs #2563EB:ΔE 遠小於 2 → 視為命中(容忍匯出/取樣微差)
        var root = Design("1", "frame",
            children: Design("2", "cta", fill: new Rgba(0x25, 0x64, 0xEC)));

        Assert.Empty(DesignLint.Run(root, Tokens));
    }

    [Fact]
    public void Sizes_check_font_padding_spacing_radius()
    {
        var offSize = Design("2", "title", DesignNodeType.Text,
            text: new Typography(FontSize: 30), characters: "t"); // 30 不在 scale(最近 32)
        var okPad = Design("3", "card", padding: new Insets(20, 20, 20, 20)); // 20 = space-5 ✓
        var badGap = Design("4", "list", itemSpacing: 13); // 13 不在 scale

        var root = Design("1", "frame", children: [offSize, okPad, badGap]);
        var violations = DesignLint.Run(root, Tokens);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v is { Layer: "title", Prop: "fontSize", NearestToken: "font-size-2xl" });
        Assert.Contains(violations, v => v is { Layer: "list", Prop: "itemSpacing", Value: "13px" });
    }

    [Fact]
    public void Zero_values_and_transparent_fills_are_skipped()
    {
        var root = Design("1", "frame", children:
        [
            Design("2", "ghost", fill: new Rgba(0, 0, 0, 0)),              // 全透明 → 跳過
            Design("3", "flat", padding: new Insets(0, 0, 0, 0)),          // 0 → 跳過
        ]);
        Assert.Empty(DesignLint.Run(root, Tokens));
    }

    [Fact]
    public void Dimension_without_tokens_is_not_linted()
    {
        // 只定義了顏色 token → 尺寸維度不 lint(沒規範就不裝有規範)
        var onlyColors = new DesignTokens(new Dictionary<string, string> { ["c"] = "#FFFFFF" });
        var root = Design("1", "frame",
            children: Design("2", "list", itemSpacing: 13));
        Assert.Empty(DesignLint.Run(root, onlyColors));
    }
}
