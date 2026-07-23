using System.Text.Json;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// Box 的 JSON 相容:寫出 width/height,但讀時 width/height 與舊的 w/h 都吃得動——
/// 升級後新工具讀得動 0.9.x 產的舊快照/報告,不必重拍。
/// </summary>
public class BoxJsonTests
{
    [Fact]
    public void Reads_legacy_wh_and_new_widthheight_identically()
    {
        var legacy = JsonSerializer.Deserialize<Box>("""{"x":8,"y":8,"w":1280,"h":800}""");
        var modern = JsonSerializer.Deserialize<Box>("""{"x":8,"y":8,"width":1280,"height":800}""");

        Assert.Equal(new Box(8, 8, 1280, 800), legacy);
        Assert.Equal(new Box(8, 8, 1280, 800), modern);
    }

    [Fact]
    public void Writes_self_documenting_width_height()
    {
        var json = JsonSerializer.Serialize(new Box(8, 8, 1280, 800));

        Assert.Contains("\"width\":1280", json);
        Assert.Contains("\"height\":800", json);
        Assert.DoesNotContain("\"w\":", json);
        Assert.DoesNotContain("\"h\":", json);
    }

    [Fact]
    public void Round_trips_through_write_then_read()
    {
        var box = new Box(10, 20, 300, 40);
        var back = JsonSerializer.Deserialize<Box>(JsonSerializer.Serialize(box));

        Assert.Equal(box, back);
    }
}
