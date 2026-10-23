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
    public void Transparent_pixels_do_not_vote_and_an_all_transparent_region_has_no_color()
    {
        // 匯出的 PNG 常有透明底:透明像素即使佔多數也不能贏(否則取樣出「透明黑」);
        // 整塊都透明 → null(不比),而不是瞎猜一個顏色
        using var img = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 25; x++)
                img[x, y] = new Rgba32(0, 128, 0);

        Assert.Equal("#008000", ImageDesignSource.SampleDominant(img, new Box(0, 0, 100, 100))!.Value.ToHex());
        Assert.Null(ImageDesignSource.SampleDominant(img, new Box(50, 0, 50, 100)));
    }

    [Fact]
    public async Task Missing_design_image_names_the_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var annPath = Path.Combine(dir, "design.json");
            await File.WriteAllTextAsync(annPath, """{ "id": "1:1", "name": "frame", "type": "Frame", "box": { "x": 0, "y": 0, "width": 10, "height": 10 } }""");
            var missing = Path.Combine(dir, "nope.png");

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => new ImageDesignSource(missing).GetFrameAsync(new DesignRef(annPath, "")));
            Assert.Contains($"design image not found: {missing}", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("tiff")]
    public async Task Only_png_and_jpeg_are_decoded(string format)
    {
        // 決策 #7(2.0.0):designImage 一直只文件化 PNG/JPG,1.x 的 Image.LoadAsync 卻依內容自動判格式、
        // 什麼都解(ImageSharp 3.1.12 的 BigTIFF 解碼 DoS 就從這裡進來;3.1 線沒有修補版)。
        // 現在只註冊 PNG 與 JPEG 解碼器,其他格式給明確的錯——副檔名改成 .png 也一樣擋得住
        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var imgPath = Path.Combine(dir, "design.png"); // 副檔名騙不過:看內容
            using (var img = TestImage())
            {
                if (format == "bmp") await img.SaveAsBmpAsync(imgPath);
                else if (format == "gif") await img.SaveAsGifAsync(imgPath);
                else await img.SaveAsTiffAsync(imgPath);
            }
            var annPath = Path.Combine(dir, "design.json");
            await File.WriteAllTextAsync(annPath, """{ "id": "1:1", "name": "frame", "type": "Frame", "box": { "x": 0, "y": 0, "width": 10, "height": 10 } }""");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ImageDesignSource(imgPath).GetFrameAsync(new DesignRef(annPath, "")));
            Assert.Contains("PNG or JPEG", ex.Message);
            Assert.Contains(imgPath, ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Every_format_imagesharp_knows_besides_png_and_jpeg_is_refused()
    {
        // 審查的 M07e:在解碼器清單多加一個 WebP,上面那條(只測 bmp/gif/tiff)照樣通過。
        // 這裡列舉 ImageSharp 預設設定認得的**全部**格式,除了 PNG/JPEG 之外逐一編碼、逐一確認被拒
        var others = Configuration.Default.ImageFormats
            .Where(f => f.Name is not ("PNG" or "JPEG"))
            .ToList();
        Assert.True(others.Count >= 7, $"ImageSharp 認得的格式比預期少:{string.Join(", ", others.Select(f => f.Name))}");

        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var annPath = Path.Combine(dir, "design.json");
            await File.WriteAllTextAsync(annPath, """{ "id": "1:1", "name": "frame", "type": "Frame", "box": { "x": 0, "y": 0, "width": 10, "height": 10 } }""");
            var accepted = new List<string>();
            foreach (var format in others)
            {
                var imgPath = Path.Combine(dir, $"design-{format.Name}.png"); // 副檔名一律 .png:判格式看內容
                using (var img = TestImage())
                    await img.SaveAsync(imgPath, Configuration.Default.ImageFormatsManager.GetEncoder(format));
                try
                {
                    await new ImageDesignSource(imgPath).GetFrameAsync(new DesignRef(annPath, ""));
                    accepted.Add(format.Name);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("PNG or JPEG") && ex.Message.Contains(imgPath))
                {
                    // 預期:明確拒絕,訊息帶路徑
                }
            }
            Assert.True(accepted.Count == 0, $"這些格式被解碼了:{string.Join(", ", accepted)}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>審查第 9 條的錯誤訊息:寫一個 designImage 進暫存目錄,回傳 ImageDesignSource 丟出的訊息。</summary>
    private static async Task<string> ErrorFor(Func<string, Task> writeImage)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var imgPath = Path.Combine(dir, "design.png");
            await writeImage(imgPath);
            var annPath = Path.Combine(dir, "design.json");
            await File.WriteAllTextAsync(annPath, """{ "id": "1:1", "name": "frame", "type": "Frame", "box": { "x": 0, "y": 0, "width": 10, "height": 10 } }""");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ImageDesignSource(imgPath).GetFrameAsync(new DesignRef(annPath, "")));
            Assert.Contains(imgPath, ex.Message); // 每一種錯都要說是哪個檔
            return ex.Message;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task An_empty_file_is_called_empty_not_another_format()
    {
        var message = await ErrorFor(path => File.WriteAllBytesAsync(path, []));

        Assert.Contains("empty", message);
        Assert.DoesNotContain("another format", message);
    }

    [Fact]
    public async Task A_text_file_is_called_unrecognized_not_another_format()
    {
        var message = await ErrorFor(path => File.WriteAllTextAsync(path, "this is not an image, just some text\n"));

        Assert.Contains("not an image Parity can read", message);
        Assert.DoesNotContain("another format", message);
    }

    [Fact]
    public async Task A_recognized_other_format_is_named()
    {
        var message = await ErrorFor(async path => { using var img = TestImage(); await img.SaveAsGifAsync(path); });

        Assert.Contains("must be PNG or JPEG", message);
        Assert.Contains("is a GIF image", message);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("png-bad-crc")]
    public async Task A_damaged_png_or_jpeg_names_the_file(string format)
    {
        // 審查實證:截斷的 PNG/JPEG 原本只印 ImageSharp 的原文(「PNG Image does not contain a data chunk.」),
        // 不說是哪個檔
        var message = await ErrorFor(async path =>
        {
            using var stream = new MemoryStream();
            using (var img = TestImage())
            {
                if (format.StartsWith("png", StringComparison.Ordinal)) await img.SaveAsPngAsync(stream);
                else await img.SaveAsJpegAsync(stream);
            }
            var bytes = stream.ToArray();
            if (format == "png-bad-crc")
            {
                // 壞掉的 CRC:ImageSharp 的訊息是 "CRC Error. PNG IDAT chunk is corrupt!"——結尾自己帶標點
                var idat = bytes.AsSpan().IndexOf("IDAT"u8);
                bytes[idat + 6] ^= 0xFF;
                await File.WriteAllBytesAsync(path, bytes);
            }
            else await File.WriteAllBytesAsync(path, bytes[..(int)(bytes.Length * 0.4)]);
        });

        Assert.Contains("damaged", message);
        // 驗證者:ImageSharp 的原文自帶句尾標點,原本包進括號再加句點,變成「corrupt!).」這種重複的標點
        Assert.DoesNotMatch(@"[.!?]\)|[.!?][.!?]", message);
    }

    [Fact]
    public async Task Jpeg_is_still_decoded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var imgPath = Path.Combine(dir, "design.jpg");
            using (var img = TestImage()) await img.SaveAsJpegAsync(imgPath);
            var annPath = Path.Combine(dir, "design.json");
            await File.WriteAllTextAsync(annPath, """{ "id": "1:1", "name": "frame", "type": "Frame", "box": { "x": 0, "y": 0, "width": 100, "height": 100 } }""");

            var root = await new ImageDesignSource(imgPath).GetFrameAsync(new DesignRef(annPath, ""));

            Assert.NotNull(root.Fill);
        }
        finally { Directory.Delete(dir, recursive: true); }
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
