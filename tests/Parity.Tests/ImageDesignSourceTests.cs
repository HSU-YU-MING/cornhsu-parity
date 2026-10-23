using System.Security.Cryptography;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Image;
using Parity.Engine.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
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

    // ── 不讀中繼資料(GHSA-gwg2-r3hj-4w44:ICC 的 CLUT 依未驗證的尺寸配置記憶體)──

    /// <summary>
    /// 最小的合法 ICC v2 設定檔:128 位元組檔頭 + 一個 A2B0 標籤(lut8,帶 2×2×2 的 CLUT)。
    /// 3.1 線的 ICC 標籤型別是 internal,只能手刻位元組;內容是良性的,只用來確認「解碼器看得到它」。
    /// </summary>
    private static byte[] IccProfileWithClut()
    {
        const int lutSize = 48 + 256 * 3 + 8 * 3 + 256 * 3; // 型別頭與矩陣、輸入表、CLUT、輸出表
        const int tagOffset = 128 + 4 + 12;                  // 檔頭、標籤數、一筆標籤表
        var b = new byte[tagOffset + lutSize];
        void U32(int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        void U16(int at, ushort v) { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }
        void Sig(int at, string s) { for (var i = 0; i < 4; i++) b[at + i] = (byte)s[i]; }

        U32(0, (uint)b.Length); b[8] = 2; b[9] = 0x10;                 // 大小、版本 2.1
        Sig(12, "mntr"); Sig(16, "RGB "); Sig(20, "XYZ ");               // 顯示器、RGB、PCS = XYZ
        U16(24, 2026); U16(26, 1); U16(28, 1); Sig(36, "acsp");
        U32(68, 63190); U32(72, 65536); U32(76, 54061);                 // D50 白點(s15Fixed16)
        U32(128, 1); Sig(132, "A2B0"); U32(136, tagOffset); U32(140, lutSize);

        var t = tagOffset;
        Sig(t, "mft1"); b[t + 8] = 3; b[t + 9] = 3; b[t + 10] = 2;     // 3 進 3 出、每軸 2 格
        U32(t + 12, 65536); U32(t + 28, 65536); U32(t + 44, 65536);     // 單位矩陣
        var p = t + 48;
        for (var c = 0; c < 3; c++) for (var i = 0; i < 256; i++) b[p++] = (byte)i;
        for (var g = 0; g < 8; g++) for (var c = 0; c < 3; c++) b[p++] = (byte)(((g >> (2 - c)) & 1) * 255);
        for (var c = 0; c < 3; c++) for (var i = 0; i < 256; i++) b[p++] = (byte)i;
        return b;
    }

    /// <summary>64×48 漸層;<paramref name="alpha"/> 時左 1/4 全透明、次 1/4 半透明。</summary>
    private static Image<Rgba32> Gradient(bool alpha)
    {
        var img = new Image<Rgba32>(64, 48);
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 64; x++)
                img[x, y] = new Rgba32((byte)(x * 4), (byte)(y * 5), (byte)((x + y) * 2),
                    alpha ? (byte)(x < 16 ? 0 : x < 32 ? 128 : 255) : (byte)255);
        return img;
    }

    /// <summary>
    /// 各種帶中繼資料、或其解讀可能影響像素的 PNG/JPEG:內嵌 ICC(PNG iCCP、JPEG APP2)、
    /// RGB 與 CMYK JPEG(Adobe APP14 的色彩轉換旗標)、調色盤 PNG 的 tRNS 透明、JPEG 的 EXIF 方向。
    /// </summary>
    private static async Task<string> WriteMetadataFixture(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        using var img = Gradient(alpha: name.EndsWith(".png", StringComparison.Ordinal));
        switch (name)
        {
            case "icc.png":
                img.Metadata.IccProfile = new IccProfile(IccProfileWithClut());
                await img.SaveAsPngAsync(path);
                break;
            case "icc.jpg":
                img.Metadata.IccProfile = new IccProfile(IccProfileWithClut());
                await img.SaveAsJpegAsync(path);
                break;
            case "icc-rgb.jpg":
                img.Metadata.IccProfile = new IccProfile(IccProfileWithClut());
                await img.SaveAsJpegAsync(path, new JpegEncoder { ColorType = JpegEncodingColor.Rgb });
                break;
            case "cmyk.jpg":
                await img.SaveAsJpegAsync(path, new JpegEncoder { ColorType = JpegEncodingColor.Cmyk });
                break;
            case "palette-trns.png":
                await img.SaveAsPngAsync(path, new PngEncoder { ColorType = PngColorType.Palette });
                break;
            case "exif.jpg":
                img.Metadata.ExifProfile = new ExifProfile();
                img.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
                await img.SaveAsJpegAsync(path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
        return path;
    }

    private static string PixelHash(Image<Rgba32> img)
    {
        var bytes = new byte[img.Width * img.Height * 4];
        img.CopyPixelDataTo(bytes);
        return $"{img.Width}x{img.Height}:{Convert.ToHexString(SHA256.HashData(bytes))}";
    }

    /// <summary>改動前的解碼設定:同一組 PNG/JPEG 解碼器,不跳過中繼資料。</summary>
    private static DecoderOptions WithMetadata => new() { Configuration = ImageDesignSource.PngAndJpegOnly.Configuration };

    [Theory]
    [InlineData("icc.png")]
    [InlineData("icc.jpg")]
    [InlineData("icc-rgb.jpg")]
    [InlineData("cmyk.jpg")]
    [InlineData("palette-trns.png")]
    [InlineData("exif.jpg")]
    public async Task Skipping_metadata_leaves_the_decoded_pixels_unchanged(string fixture)
    {
        // SkipMetadata 不能只是「看起來無害」:漏讀 APP14 會把 RGB/CMYK JPEG 解成別的顏色,漏讀 tRNS 會讓透明
        // 變不透明——取樣就靜靜地取錯色。逐位元比對改動前後解碼出的 Rgba32 像素
        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = await WriteMetadataFixture(dir, fixture);
            using var before = SixLabors.ImageSharp.Image.Load<Rgba32>(WithMetadata, path);
            using var after = await ImageDesignSource.LoadPixelsAsync(path);

            Assert.Equal(PixelHash(before), PixelHash(after));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("icc.png")]
    [InlineData("icc.jpg")]
    [InlineData("exif.jpg")]
    public async Task Decoded_design_images_carry_no_icc_or_exif_profile(string fixture)
    {
        // GHSA-gwg2-r3hj-4w44:3.1.12 在讀 IccProfile.Entries 時才解析 CLUT。設計圖解碼後不留任何 ICC,
        // 這條路就不存在。PNG 要特別測:3.1.12 的 PNG 解碼器不理 SkipMetadata,iCCP 照收(2026-10-23 實測),
        // 是 LoadPixelsAsync 解碼後丟掉的
        var dir = Path.Combine(Path.GetTempPath(), $"parity-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = await WriteMetadataFixture(dir, fixture);
            using (var withMetadata = SixLabors.ImageSharp.Image.Load<Rgba32>(WithMetadata, path))
            {
                // fixture 自證:不跳過中繼資料時解碼器確實讀得到,下面的 null 才有意義
                if (fixture.StartsWith("icc", StringComparison.Ordinal)) Assert.NotNull(withMetadata.Metadata.IccProfile);
                else Assert.NotNull(withMetadata.Metadata.ExifProfile);
            }

            using var image = await ImageDesignSource.LoadPixelsAsync(path);

            Assert.Null(image.Metadata.IccProfile);
            Assert.Null(image.Metadata.ExifProfile);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
