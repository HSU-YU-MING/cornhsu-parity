using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

public class NormalizerTests
{
    [Fact]
    public void Design_boxes_become_relative_to_frame_origin()
    {
        // Figma absoluteBoundingBox 是檔案絕對座標(規畫書 4.6)
        var root = Design("1", "frame", box: new Box(100, 200, 800, 600),
            children: Design("2", "child", box: new Box(140, 240, 50, 20),
                children: Design("3", "grandchild", box: new Box(150, 250, 10, 10))));

        var normalized = Normalizer.NormalizeDesign(root);

        Assert.Equal(new Box(0, 0, 800, 600), normalized.Box);
        Assert.Equal(new Box(40, 40, 50, 20), normalized.Children[0].Box);
        Assert.Equal(new Box(50, 50, 10, 10), normalized.Children[0].Children[0].Box);
    }

    [Fact]
    public void Rendered_boxes_become_relative_to_body_origin()
    {
        var root = Rendered("body", box: new Box(8, 8, 800, 600),
            children: Rendered("main", box: new Box(48, 48, 400, 300)));

        var normalized = Normalizer.NormalizeRendered(root);

        Assert.Equal(new Box(0, 0, 800, 600), normalized.Box);
        Assert.Equal(new Box(40, 40, 400, 300), normalized.Children[0].Box);
    }

    [Theory]
    [InlineData("16px", 16.0)]
    [InlineData("16.5px", 16.5)]
    [InlineData("0px", 0.0)]
    [InlineData("400", 400.0)]
    public void Parses_css_px_strings(string input, double expected)
        => Assert.Equal(expected, Normalizer.ParseCssPx(input));

    [Theory]
    [InlineData("normal")] // line-height: normal → 無法解析,不比
    [InlineData("")]
    [InlineData(null)]
    [InlineData("auto")]
    public void Unparsable_px_returns_null(string? input)
        => Assert.Null(Normalizer.ParseCssPx(input));
}
