using System.Text.Json.Nodes;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;

namespace Parity.Tests;

/// <summary>FigmaNodeParser——真實使用者最常走的路(Figma)。針對一段真實節點 JSON 驗解析。</summary>
public class FigmaParserTests
{
    private const string NodeJson = """
        {
          "id": "10:2",
          "name": "Home",
          "type": "FRAME",
          "absoluteBoundingBox": { "x": 0, "y": 0, "width": 800, "height": 600 },
          "fills": [{ "type": "SOLID", "visible": true, "opacity": 1, "color": { "r": 1, "g": 1, "b": 1, "a": 1 } }],
          "layoutMode": "VERTICAL",
          "paddingTop": 16, "paddingRight": 24, "paddingBottom": 16, "paddingLeft": 24,
          "itemSpacing": 12,
          "cornerRadius": 8,
          "children": [
            {
              "id": "10:3",
              "name": "Title",
              "type": "TEXT",
              "absoluteBoundingBox": { "x": 24, "y": 16, "width": 200, "height": 40 },
              "characters": "Hello",
              "style": { "fontFamily": "Inter", "fontSize": 32, "fontWeight": 700, "lineHeightPx": 40, "letterSpacing": 0 },
              "fills": [{ "type": "SOLID", "color": { "r": 0.06, "g": 0.09, "b": 0.16, "a": 1 } }],
              "layoutSizingHorizontal": "FILL",
              "layoutSizingVertical": "HUG"
            },
            {
              "id": "10:4",
              "name": "Hidden",
              "type": "RECTANGLE",
              "visible": false,
              "absoluteBoundingBox": { "x": 0, "y": 0, "width": 10, "height": 10 }
            }
          ]
        }
        """;

    private static DesignNode Parse() => FigmaNodeParser.Parse(JsonNode.Parse(NodeJson)!);

    [Fact]
    public void Parses_frame_box_fill_padding_spacing()
    {
        var root = Parse();
        Assert.Equal("Home", root.Name);
        Assert.Equal(DesignNodeType.Frame, root.Type);
        Assert.Equal(800, root.Box.W);
        Assert.Equal(600, root.Box.H);
        Assert.NotNull(root.Fill);
        Assert.Equal("#FFFFFF", root.Fill!.Value.ToHex()); // 0–1 → 0–255
        Assert.NotNull(root.Padding);
        Assert.Equal(16, root.Padding!.Value.Top);
        Assert.Equal(24, root.Padding!.Value.Left);
        Assert.Equal(12, root.ItemSpacing);
        Assert.Equal(8, root.CornerRadius);
        Assert.Equal("VERTICAL", root.LayoutMode);
    }

    [Fact]
    public void Filters_invisible_children()
    {
        // visible:false 的 RECTANGLE 要被濾掉,只剩 Title
        var root = Parse();
        Assert.Single(root.Children);
        Assert.Equal("Title", root.Children[0].Name);
    }

    [Fact]
    public void Parses_text_typography_and_layout_sizing()
    {
        var title = Parse().Children[0];
        Assert.Equal(DesignNodeType.Text, title.Type);
        Assert.Equal("Hello", title.Characters);
        Assert.NotNull(title.Text);
        Assert.Equal(32, title.Text!.FontSize);
        Assert.Equal(700, title.Text.FontWeight);
        Assert.Equal(40, title.Text.LineHeight);
        Assert.Equal("Inter", title.Text.FontFamily);
        Assert.Equal("FILL", title.LayoutSizingHorizontal);
        Assert.Equal("HUG", title.LayoutSizingVertical);
        // 0.06/0.09/0.16 → 15/23/41
        Assert.Equal("#0F1729", title.Fill!.Value.ToHex());
    }
}
