using System.Text;
using System.Text.Json;
using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Parity.Tests;

/// <summary>
/// 圖片內容比對(compare.images,C1)。上半是演算法(用程式畫的圖),下半走真的 parity snapshot + check。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class ImageComparisonTests
{
    private static readonly Box Whole = new(0, 0, 64, 64);

    private static Image<Rgba32> Solid(byte r, byte g, byte b, int size = 64)
        => new(size, size, new Rgba32(r, g, b));

    /// <summary>有明暗結構的圖:左亮右暗的漸層加上幾條斜線,像一張普通的照片那樣每一格都不一樣。</summary>
    private static Image<Rgba32> Picture(int size = 64, bool flipped = false)
    {
        var image = new Image<Rgba32>(size, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var u = (flipped ? size - 1 - x : x) * 255 / (size - 1);
                var stripe = (x + y) / (size / 4) % 2 == 0 ? 40 : 0;
                var v = (byte)Math.Clamp(u + stripe, 0, 255);
                image[x, y] = new Rgba32(v, (byte)(v / 2), (byte)(255 - v));
            }
        return image;
    }

    /// <summary>內容與顏色都在門檻內:不算落差。</summary>
    private static void Within(ImageComparer.ImageDifference? m)
    {
        Assert.NotNull(m);
        Assert.True(m.Value.Structure <= ImageComparer.Tolerance, $"structure {m.Value.Structure}");
        Assert.True(m.Value.Color <= ImageComparer.ColorTolerance, $"color ΔE {m.Value.Color}");
    }

    private static Image<Rgba32> AsJpeg(Image<Rgba32> image, int quality)
    {
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
        stream.Position = 0;
        return Image.Load<Rgba32>(stream);
    }

    [Fact]
    public void The_same_image_has_no_difference()
    {
        using var a = Picture();
        using var b = Picture();

        Assert.Equal(new ImageComparer.ImageDifference(0, 0), ImageComparer.Measure(a, Whole, b, Whole));
    }

    [Fact]
    public void Solid_colors_differ_although_their_structure_is_the_same()
    {
        // 結構雜湊對純色無感,要靠顏色那一半:結構差是 0,顏色差很大(2.5.0 起報成 imageColor)
        using var blue = Solid(37, 99, 235);
        using var red = Solid(255, 0, 0);

        var m = ImageComparer.Measure(blue, Whole, red, Whole)!.Value;
        Assert.Equal(0, m.Structure);
        Assert.True(m.Color > 30);
    }

    [Fact]
    public void A_different_picture_with_similar_colors_differs_by_structure()
    {
        using var a = Picture();
        using var b = Picture(flipped: true);

        Assert.True(ImageComparer.Measure(a, Whole, b, Whole)!.Value.Structure > ImageComparer.Tolerance);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(50)]
    [InlineData(30)] // 2.5.0 顏色門檻調低之後:最狠的壓縮,平均色差也只有 0.5 左右(門檻 3)
    public void Recompressing_an_image_is_not_a_difference(int quality)
    {
        using var original = Picture(256);
        using var compressed = AsJpeg(original, quality);
        var box = new Box(0, 0, 256, 256);

        Within(ImageComparer.Measure(original, box, compressed, box));
    }

    [Theory]
    [InlineData(30f)] // 論文評估植入的「圖片變色」
    [InlineData(10f)]
    public void A_hue_shift_is_a_color_difference_not_a_content_difference(float degrees)
    {
        // 清單 #10:以前內容與顏色合成一個分數、共用 10% 門檻,色相轉 30° 多半過不了。現在顏色另外比平均色差
        using var original = Picture(256);
        using var shifted = original.Clone(c => c.Hue(degrees));
        var box = new Box(0, 0, 256, 256);

        var m = ImageComparer.Measure(original, box, shifted, box)!.Value;

        Assert.True(m.Structure <= ImageComparer.Tolerance, $"structure {m.Structure}");
        Assert.True(m.Color > ImageComparer.ColorTolerance, $"color ΔE {m.Color}");
    }

    [Fact]
    public void Compression_noise_on_a_flat_image_is_not_a_difference()
    {
        // 純色區塊的亮度幾乎一樣:沒有門檻的話,雜訊會讓明暗方向亂跳
        using var flat = Solid(120, 120, 120, 128);
        using var noisy = flat.Clone();
        var random = new Random(42);
        for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
            {
                var v = (byte)(120 + random.Next(-3, 4));
                noisy[x, y] = new Rgba32(v, v, v);
            }
        var box = new Box(0, 0, 128, 128);

        Within(ImageComparer.Measure(flat, box, noisy, box));
    }

    [Fact]
    public void Scaling_an_image_is_not_a_difference()
    {
        using var large = Picture(256);
        using var small = large.Clone(c => c.Resize(128, 128));

        Within(ImageComparer.Measure(large, new Box(0, 0, 256, 256), small, new Box(0, 0, 128, 128)));
    }

    [Fact]
    public void Only_the_boxes_are_compared()
    {
        // 兩張圖只有框外不一樣
        using var a = Solid(255, 255, 255, 100);
        using var b = Solid(0, 0, 0, 100);
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                b[x + 10, y + 10] = new Rgba32(255, 255, 255);

        Assert.Equal(new ImageComparer.ImageDifference(0, 0), ImageComparer.Measure(a, new Box(10, 10, 20, 20), b, new Box(10, 10, 20, 20)));
    }

    [Theory]
    [InlineData(0, 0, 4, 4)]          // 太小:縮完沒有資訊
    [InlineData(1000, 1000, 40, 40)]  // 完全在圖外
    [InlineData(-30, 0, 40, 40)]      // 大半在圖外
    [InlineData(0, 0, 0, 0)]          // 沒有大小
    public void Boxes_that_cannot_be_judged_are_skipped(double x, double y, double w, double h)
    {
        using var a = Picture();
        using var b = Solid(0, 0, 0);
        var box = new Box(x, y, w, h);

        Assert.Null(ImageComparer.Measure(a, box, b, box));
    }

    [Fact]
    public void Transparent_areas_of_a_design_image_count_as_white()
    {
        using var transparent = new Image<Rgba32>(64, 64, new Rgba32(0, 0, 0, 0));
        using var white = Solid(255, 255, 255);

        Assert.Equal(new ImageComparer.ImageDifference(0, 0), ImageComparer.Measure(transparent, Whole, white, Whole));
    }

    // ---------- 走真的 parity snapshot + check ----------

    private const string Skipped = "略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器";

    private static string SvgDataUri(string hex) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"""<svg xmlns="http://www.w3.org/2000/svg" width="120" height="64"><rect width="120" height="64" fill="{hex}"/></svg>"""));

    private static string Page(string imageSrc) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>images</title><style>
          * { box-sizing: border-box; margin: 0; }
          body { width: 800px; height: 600px; padding: 40px; background: #FFFFFF; }
          h1 { font: 700 32px Arial, sans-serif; color: #111827; margin-bottom: 16px; }
          img { display: block; width: 120px; height: 64px; }
          .bg { width: 120px; height: 64px; margin-top: 16px; background: url("{{SvgDataUri("#16A34A")}}"); }
        </style></head><body>
        <h1 id="title">Images</h1>
        <img id="hero" src="{{imageSrc}}" alt="">
        <div id="banner" class="bg"></div>
        </body></html>
        """;

    private static string Config(string images) => $$"""
        {
          "designFile": "parity.snapshot.json",
          "targets": [{ "route": "/", "frame": "/", "url": "./index.html", "width": 800, "height": 600 }],
          "compare": { "images": "{{images}}" }
        }
        """;

    /// <summary>藍色的圖拍快照,再對 <paramref name="page"/> 跑 check。瀏覽器沒裝回 null。</summary>
    private static async Task<(int Exit, FidelityReport Report, string Err)?> SnapshotThenCheck(
        string page, string images = "soft", string? baseline = null, params string[] checkArgs)
    {
        using var dir = new TempDir();
        dir.Write("index.html", baseline ?? Page(SvgDataUri("#2563EB")));
        var config = dir.Write("parity.config.json", Config(images));
        var snapshot = await Cli.RunAsync("snapshot", "--config", config);
        if (Cli.BrowserMissing(snapshot)) return null;
        Assert.Equal(0, snapshot.ExitCode);
        Assert.True(File.Exists(dir.File("parity.snapshot.png")), "快照應該寫出參考截圖");

        dir.Write("index.html", page);
        var check = await Cli.RunAsync(["check", "--config", config, .. checkArgs]);
        Assert.True(check.ExitCode is 0 or 1, $"check exited {check.ExitCode}\n{check.Out}\n{check.Err}");
        var document = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(dir.File(".parity", "report.json")), ReportWire.Compact)!;
        return (check.ExitCode, Assert.Single(document.Reports), check.Err);
    }

    private static List<PropDiff> ImageDiffs(FidelityReport report)
        => report.Nodes.SelectMany(n => n.Diffs).Where(d => d.Prop is "image" or "imageColor").ToList();

    [Fact]
    public async Task The_same_page_has_no_image_diff()
    {
        if (await SnapshotThenCheck(Page(SvgDataUri("#2563EB"))) is not { } run) { Console.WriteLine(Skipped); return; }

        Assert.Empty(ImageDiffs(run.Report));
        Assert.Equal(0, run.Exit);
    }

    [Fact]
    public async Task A_replaced_image_is_a_soft_diff_on_that_element_only()
    {
        // 藍色純色圖換成紅色:結構一樣、顏色差很大,2.5.0 起報成 imageColor(以前內容與顏色合成一個 image)
        if (await SnapshotThenCheck(Page(SvgDataUri("#FF0000"))) is not { } run) { Console.WriteLine(Skipped); return; }

        var node = Assert.Single(run.Report.Nodes, n => n.Diffs.Any(d => d.Prop == "imageColor"));
        Assert.Equal("hero", node.DesignLayer);
        var diff = Assert.Single(node.Diffs);
        Assert.True(diff.Soft);
        Assert.Equal(DiffStatus.Mismatch, diff.Status);
        Assert.True(diff.Delta > ImageComparer.ColorTolerance);
        Assert.Equal(0, run.Exit); // 軟落差不擋
        Assert.Equal(1, run.Report.Summary.NodesWithDiffs); // 摘要有重算
        Assert.Equal(1, run.Report.Summary.Minor);
        Assert.Equal(100, FidelityScore.Compute([run.Report])); // 不扣分
    }

    [Fact]
    public async Task A_background_image_counts_too()
    {
        var page = Page(SvgDataUri("#2563EB")).Replace(SvgDataUri("#16A34A"), SvgDataUri("#FF0000"));
        if (await SnapshotThenCheck(page) is not { } run) { Console.WriteLine(Skipped); return; }

        Assert.Equal("banner", Assert.Single(run.Report.Nodes, n => n.Diffs.Any(d => d.Prop == "imageColor")).DesignLayer);
    }

    [Fact]
    public async Task A_broken_image_is_missing()
    {
        if (await SnapshotThenCheck(Page("data:image/png;base64,AAAA")) is not { } run) { Console.WriteLine(Skipped); return; }

        var diff = Assert.Single(ImageDiffs(run.Report));
        Assert.Equal(DiffStatus.Missing, diff.Status);
        Assert.Equal("(image not loaded)", diff.Actual);
    }

    [Fact]
    public async Task A_style_added_after_the_snapshot_is_a_soft_diff_because_the_snapshot_knows_it_had_none()
    {
        // 走真的快照檔:只記非預設值,讀回來要知道「沒寫 = 沒有」,不是「不知道」
        var page = Page(SvgDataUri("#2563EB")).Replace("</style>", ".bg { box-shadow: 0 0 10px #000; border: 1px solid #000; opacity: .5 }\n</style>");
        if (await SnapshotThenCheck(page, images: "none") is not { } run) { Console.WriteLine(Skipped); return; }

        var banner = run.Report.Nodes.Single(n => n.DesignLayer == "banner");
        Assert.Equal(["borderWidth", "opacity", "shadow"], banner.Diffs.Select(d => d.Prop).Order());
        Assert.All(banner.Diffs, d => Assert.True(d.Soft));
    }

    [Fact]
    public async Task An_image_that_was_already_broken_in_the_baseline_is_not_a_diff()
    {
        // 野生實查:gov.tw 有 30 張基準拍下來時就沒載入的圖。兩邊畫面一樣,不是落差
        var broken = Page("data:image/png;base64,AAAA");
        if (await SnapshotThenCheck(broken, baseline: broken) is not { } run) { Console.WriteLine(Skipped); return; }

        Assert.Empty(ImageDiffs(run.Report));
    }

    [Fact]
    public async Task Off_by_default()
    {
        if (await SnapshotThenCheck(Page(SvgDataUri("#FF0000")), images: "none") is not { } run) { Console.WriteLine(Skipped); return; }

        Assert.Empty(ImageDiffs(run.Report));
    }

    [Fact]
    public async Task Check_reverse_does_not_compare_images()
    {
        if (await SnapshotThenCheck(Page(SvgDataUri("#FF0000")), checkArgs: "--reverse") is not { } run) { Console.WriteLine(Skipped); return; }

        Assert.Empty(ImageDiffs(run.Report));
    }

    [Fact]
    public async Task A_missing_reference_image_is_a_note_and_does_not_fail_the_check()
    {
        using var dir = new TempDir();
        dir.Write("index.html", Page(SvgDataUri("#2563EB")));
        var config = dir.Write("parity.config.json", Config("soft"));
        var snapshot = await Cli.RunAsync("snapshot", "--config", config);
        if (Cli.BrowserMissing(snapshot)) { Console.WriteLine(Skipped); return; }
        File.Delete(dir.File("parity.snapshot.png"));

        var check = await Cli.RunAsync("check", "--config", config);

        Assert.Equal(0, check.ExitCode);
        Assert.Contains("no reference image", check.Err);
    }

    [Fact]
    public async Task A_gradient_turned_another_way_is_reported_on_a_real_page()
    {
        // 走真的瀏覽器:計算值的格式要真的解析得出方向
        static string Gradient(string direction) => Page(SvgDataUri("#2563EB"))
            .Replace("</style>", $".bg {{ background: linear-gradient({direction}, #ff0000, #0000ff) }}\n</style>");
        if (await SnapshotThenCheck(Gradient("180deg"), images: "none", baseline: Gradient("90deg")) is not { } run) { Console.WriteLine(Skipped); return; }

        var diff = Assert.Single(run.Report.Nodes.Single(n => n.DesignLayer == "banner").Diffs);
        Assert.Equal("gradient", diff.Prop);
        Assert.Contains("90deg", diff.Expected);
        Assert.Contains("180deg", diff.Actual);
    }

    [Fact]
    public async Task The_same_gradient_written_another_way_is_not_a_diff_on_a_real_page()
    {
        static string Gradient(string direction) => Page(SvgDataUri("#2563EB"))
            .Replace("</style>", $".bg {{ background: linear-gradient({direction}, #ff0000, #0000ff) }}\n</style>");
        if (await SnapshotThenCheck(Gradient("to right"), images: "none", baseline: Gradient("90deg")) is not { } run) { Console.WriteLine(Skipped); return; }

        Assert.Empty(run.Report.Nodes.SelectMany(n => n.Diffs));
    }

    [Fact]
    public async Task A_figma_reference_starts_at_the_frame_so_the_design_origin_is_not_added()
    {
        // 快照與 designImage 的參考圖用設計稿本來的座標;Figma 匯出的圖左上角就是 frame。frame 在檔案裡的位置(這裡是 500, 500)不能算進去
        using var reference = Solid(255, 255, 255, 100);
        for (var y = 10; y < 30; y++)
            for (var x = 10; x < 30; x++)
                reference[x, y] = new Rgba32(0, 0, 255);
        using var screenshot = reference.Clone();
        using var dir = new TempDir();
        var referencePath = dir.File("frame.png");
        await reference.SaveAsPngAsync(referencePath);
        using var shot = new MemoryStream();
        await screenshot.SaveAsPngAsync(shot);

        var box = new Box(10, 10, 20, 20);
        var report = new FidelityReport("/", "http://x/", "d",
            [new NodeResult("hero", "1:2", "#hero", "auto-name", Severity.None, [], box, box)], [],
            new ReportSummary(1, 1, 0, 0, 0, 0, 0, 0, Severity.None));
        var tree = TestData.Rendered("body", children: TestData.Rendered("#hero", "img"));
        var result = new ScanResult(report, TestData.Design("1:1", "frame"), tree, default, new Box(500, 500, 100, 100));

        var same = await ImageComparer.CompareAsync(result, referencePath, shot.ToArray(), referenceOrigin: default(Box));
        Assert.Empty(same.Report.Nodes.Single().Diffs);

        // 對照:用設計稿本來的座標就裁到圖外,比不出結果(也就不會有落差),所以這裡另外確認換了圖會報
        using var other = Solid(255, 0, 0, 100);
        using var otherShot = new MemoryStream();
        await other.SaveAsPngAsync(otherShot);
        var changed = await ImageComparer.CompareAsync(result, referencePath, otherShot.ToArray(), referenceOrigin: default(Box));
        Assert.Equal("imageColor", Assert.Single(changed.Report.Nodes.Single().Diffs).Prop); // 藍塊換紅塊:顏色偏了
    }

    [Fact]
    public void Figma_is_accepted_now_that_frames_can_be_exported()
    {
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", """
            { "figmaFileKey": "abc", "targets": [{ "route": "/", "frame": "1:1", "url": "./a.html" }], "compare": { "images": "soft" } }
            """);

        Assert.True(Parity.Cli.ParityConfig.Load(config).CompareImages);
    }

    [Theory]
    [InlineData("\"soft\"", true)]
    [InlineData("\"SOFT\"", true)]
    [InlineData("\"none\"", false)]
    public void Compare_images_accepts_its_values(string value, bool on)
    {
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", $$"""
            { "designFile": "d.json", "targets": [{ "route": "/", "frame": "", "url": "./a.html" }], "compare": { "images": {{value}} } }
            """);

        Assert.Equal(on, Parity.Cli.ParityConfig.Load(config).CompareImages);
    }

    [Theory]
    [InlineData("\"gate\"")]
    [InlineData("\"true\"")]
    [InlineData("null")]
    public void Compare_images_rejects_anything_else(string value)
    {
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", $$"""
            { "designFile": "d.json", "targets": [{ "route": "/", "frame": "", "url": "./a.html" }], "compare": { "images": {{value}} } }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => Parity.Cli.ParityConfig.Load(config));

        Assert.Contains("compare.images", ex.Message);
    }
}
