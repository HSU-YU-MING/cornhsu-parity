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

    [Fact]
    public void Tolerates_null_dimension_as_zero()
    {
        // 壞資料裡的 null 尺寸不該讓 GetDouble 丟 InvalidOperationException;當 0 處理(同缺 key)
        var box = JsonSerializer.Deserialize<Box>("""{"x":0,"y":0,"width":null,"height":40}""");

        Assert.Equal(new Box(0, 0, 0, 40), box);
    }

    [Fact]
    public void Unknown_keys_are_skipped_even_when_they_hold_nested_values()
    {
        // 新版工具若在 box 裡多寫欄位(物件或陣列),舊讀端要整塊跳過,不能錯位讀到別的值
        var box = JsonSerializer.Deserialize<Box>(
            """{"x":1,"meta":{"w":999,"h":[1,2]},"Width":30,"H":40,"y":2}""");

        Assert.Equal(new Box(1, 2, 30, 40), box); // 欄位名不分大小寫
    }

    [Theory]
    [InlineData("\"10,20,30,40\"", "Box must be a JSON object")]
    [InlineData("""{"x":"10"}""", "Box fields must be numbers")]
    [InlineData("""{"x":1,"width":true}""", "Box fields must be numbers")]
    public void Malformed_boxes_fail_with_a_json_exception(string json, string message)
    {
        // 明確的 JsonException(report 指令會轉成「unrecognized report format」),不是難懂的 InvalidOperationException
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Box>(json));
        Assert.Contains(message, ex.Message);
    }
}
