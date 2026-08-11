using System.Text.Json;
using Parity.Cli;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Json;
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

    [Fact]
    public async Task Deep_dom_snapshot_survives_write_and_read_back()
    {
        // 真實網站 DOM 常見 30+ 層巢狀(Wikipedia 條目頁實測 31 層),每層 Children 佔
        // 2 個 JSON 深度,System.Text.Json 預設 MaxDepth 64 在「寫 snapshot」與
        // 「讀 designFile」都會炸。60 層 ≈ JSON 深度 120+,兩端都必須撐得住。
        var node = Rendered("div.leaf", box: new Box(0, 0, 10, 10));
        for (var i = 0; i < 60; i++)
            node = Rendered($"div.level-{i}", box: new Box(0, 0, 800, 600), children: node);

        var frame = SnapshotBuilder.ToFrame(node, "/");
        var json = JsonSerializer.Serialize(frame, ReportJson.Indented); // snapshot 落地同一組設定

        var path = Path.Combine(Path.GetTempPath(), $"parity-deep-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            var back = await new JsonDesignSource().GetFrameAsync(new DesignRef(path, ""));
            Assert.Equal(61, back.DescendantsAndSelf().Count()); // 最外層成為 frame + 59 層 + leaf,一個都不少
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Beyond_the_512_limit_the_error_speaks_dom_levels_not_object_cycles()
    {
        // F1 殘留:超過 512 的極端頁面,原生訊息是誤導的「object cycle」——要翻成人話
        var node = Rendered("div.leaf", box: new Box(0, 0, 10, 10));
        for (var i = 0; i < 300; i++)
            node = Rendered($"div.level-{i}", box: new Box(0, 0, 800, 600), children: node);
        var frame = SnapshotBuilder.ToFrame(node, "/");

        var ex = Assert.Throws<InvalidOperationException>(() => ReportJson.SerializeSnapshotTree(frame));
        Assert.Contains("deeper than", ex.Message);
        Assert.Contains("ignore", ex.Message);
    }

    [Fact]
    public async Task Reading_an_overly_deep_design_json_speaks_dom_levels_too()
    {
        var sb = new System.Text.StringBuilder();
        const int levels = 600; // JSON 深度 1200+,穩超過 512
        for (var i = 0; i < levels; i++)
            sb.Append($$"""{"id":"n{{i}}","name":"n{{i}}","type":"Frame","box":{"x":0,"y":0,"width":10,"height":10},"children":[""");
        sb.Append("""{"id":"leaf","name":"leaf","type":"Frame","box":{"x":0,"y":0,"width":1,"height":1}}""");
        for (var i = 0; i < levels; i++) sb.Append("]}");

        var path = Path.Combine(Path.GetTempPath(), $"parity-toodeep-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, sb.ToString());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new JsonDesignSource().GetFrameAsync(new DesignRef(path, "")));
            Assert.Contains("deeper than", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
