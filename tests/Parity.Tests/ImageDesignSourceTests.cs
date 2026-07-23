using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Image;
using Parity.Engine.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Parity.Tests;

/// <summary>
/// 圖片設計來源:標註 JSON(DesignNode 格式、fill 可省略)+ 圖片像素取樣。
/// </summary>
public class ImageDesignSourceTests
{
    private static Image<Rgba32> TestImage()
    {
        // 100×100:左上 40×40 純紅(帶 2px 白邊模擬反鋸齒),其餘白
        var img = new Image<Rgba32>(100, 100, new Rgba32(255, 255, 255));
        for (var y = 12; y < 38; y++)
            for (var x = 12; x < 38; x++)
                img[x, y] = new Rgba32(255, 0, 0);
        return img;
    }

    [Fact]
    public void Samples_dominant_color_with_inset()
    {
        using var img = TestImage();
        // 標註框 (10,10,30,30):內縮後避開白邊,主色應為紅
        var c = ImageDesignSource.SampleDominant(img, new Box(10, 10, 30, 30));
        Assert.Equal("#FF0000", c!.Value.ToHex());
    }

    [Fact]
    public void Region_outside_image_returns_null()
    {
        using var img = TestImage();
        Assert.Null(ImageDesignSource.SampleDominant(img, new Box(500, 500, 50, 50)));
    }

    [Fact]
    public async Task Fills_missing_colors_but_keeps_explicit_and_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var imgPath = Path.Combine(dir, "design.png");
            using (var img = TestImage()) await img.SaveAsPngAsync(imgPath);

            var annPath = Path.Combine(dir, "design.json");
            await File.WriteAllTextAsync(annPath, """
                {
                  "id": "1:1", "name": "frame", "type": "Frame",
                  "box": { "x": 0, "y": 0, "width": 100, "height": 100 },
                  "children": [
                    { "id": "1:2", "name": "red-box", "type": "Shape",
                      "box": { "x": 10, "y": 10, "width": 30, "height": 30 } },
                    { "id": "1:3", "name": "explicit", "type": "Shape",
                      "box": { "x": 10, "y": 10, "width": 30, "height": 30 },
                      "fill": { "r": 0, "g": 0, "b": 255, "a": 1 } },
                    { "id": "1:4", "name": "label", "type": "Text",
                      "box": { "x": 50, "y": 50, "width": 40, "height": 20 }, "characters": "hi" }
                  ]
                }
                """);

            var source = new ImageDesignSource(imgPath);
            var root = await source.GetFrameAsync(new DesignRef(annPath, ""));

            Assert.Equal("#FF0000", root.Children[0].Fill!.Value.ToHex()); // 取樣補上
            Assert.Equal("#0000FF", root.Children[1].Fill!.Value.ToHex()); // 手填的不動
            Assert.Null(root.Children[2].Fill);                            // TEXT 不取樣(反鋸齒會混色)
            Assert.NotNull(root.Fill);                                     // 根框取樣到白
            Assert.Equal("#FFFFFF", root.Fill!.Value.ToHex());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
