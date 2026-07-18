using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>RenderedNode → DesignNode 的凍結轉換(parity snapshot)。</summary>
public class SnapshotBuilderTests
{
    [Fact]
    public void Text_node_freezes_color_and_first_font_family()
    {
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
            Rendered("body > h1:nth-of-type(1)", tag: "h1", text: "Hello",
                box: new Box(40, 40, 300, 38),
                color: new Rgba(0x11, 0x18, 0x27),
                typography: new Typography("\"Segoe UI\", Arial, sans-serif", 32, 700, 38, 0)));

        var frame = SnapshotBuilder.ToFrame(rendered, "/");

        Assert.Equal("/", frame.Id);
        Assert.Equal(DesignNodeType.Frame, frame.Type);
        var title = Assert.Single(frame.Children);
        Assert.Equal(DesignNodeType.Text, title.Type);
        Assert.Equal("body > h1:nth-of-type(1)", title.Id); // Id = selector → 配對走 selector 身分關
        Assert.Equal("Hello", title.Characters);
        Assert.Equal("#111827", title.Fill!.Value.ToHex()); // TEXT 用文字色
        Assert.Equal("Segoe UI", title.Text!.FontFamily);   // stack 只取第一個(比對語意相容)
    }

    [Fact]
    public void Transparent_background_stays_null_and_zero_padding_drops()
    {
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered("div.a", box: new Box(0, 0, 100, 50)), // 無背景、零 padding
            Rendered("div.b", box: new Box(0, 60, 100, 50),
                background: new Rgba(0xF3, 0xF4, 0xF6),
                padding: new Insets(16, 16, 16, 16)),
        ]);

        var frame = SnapshotBuilder.ToFrame(rendered, "/");

        Assert.Null(frame.Children[0].Fill);      // 透明 → 不比(不烙祖先色)
        Assert.Null(frame.Children[0].Padding);   // 全零 → null(子層才吃得到位置比對)
        Assert.Equal("#F3F4F6", frame.Children[1].Fill!.Value.ToHex());
        Assert.Equal(16, frame.Children[1].Padding!.Value.Left);
    }

    [Fact]
    public void Name_prefers_id_then_class_then_tag()
    {
        var rendered = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered("#hero", domId: "hero", classes: "x y", box: new Box(0, 0, 10, 10)),
            Rendered(".card", classes: "card big", box: new Box(0, 20, 10, 10)),
            Rendered("div:nth-of-type(3)", tag: "div", box: new Box(0, 40, 10, 10)),
        ]);

        var frame = SnapshotBuilder.ToFrame(rendered, "/");

        Assert.Equal("hero", frame.Children[0].Name);
        Assert.Equal("card", frame.Children[1].Name);
        Assert.Equal("div", frame.Children[2].Name);
    }
}
