using Parity.Engine.DesignSources.Image;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Parity.Engine.Comparison;

/// <summary>
/// 圖片內容比對(compare.images):拿參考圖(快照的參考截圖、designImage、Figma 匯出的 frame)與這次的整頁截圖,裁出同一個元素來比。
/// 屬性比對看不到「圖換錯了」:尺寸一樣、位置一樣,內容完全不同。
/// 差異比例 0 = 相同、1 = 完全不同,取下面兩個的最大值:
///   - 結構:縮成 9×8 灰階,比相鄰格的明暗方向(差異雜湊的三態版)。差不到門檻算「一樣亮」,
///     否則純色區塊的壓縮雜訊會讓方向亂跳,同一張圖換個壓縮格式就被算成不同。
///   - 顏色:縮成 8×8,逐格比顏色。結構雜湊對純色無感:藍色方塊換成紅色方塊,結構差是 0。
/// 都先縮圖,所以同一張圖換壓縮格式或縮放不算落差。只有軟落差:會列出、不扣分、不擋。
/// **這個類別不把 ImageSharp 的型別露給外面**:授權金鑰只對 Parity.Engine 有效,CLI 不能直接碰 Six Labors 的套件。
/// </summary>
public static class ImageComparer
{
    /// <summary>內容:結構差(0–1)超過這個才算換了。</summary>
    public const double Tolerance = 0.1;

    /// <summary>顏色:8×8 格平均色差(CIEDE2000)超過這個才算偏掉(2.5.0,見 Compare)。</summary>
    public const double ColorTolerance = 3.0;

    /// <summary>元素的邊至少幾 px 才比:太小的圖示縮完沒有資訊,全是誤報。</summary>
    private const int MinSide = 8;

    /// <summary>元素至少要有這個比例落在圖片裡才比:露出一小角的比不出結果。</summary>
    private const double MinVisibleFraction = 0.5;

    /// <summary>相鄰格的亮度差(0–255)超過這個才算有方向。</summary>
    private const int BrightnessMargin = 8;

    private static readonly HashSet<string> ImageTags = new(StringComparer.OrdinalIgnoreCase) { "img", "svg", "picture" };

    /// <summary>這個元素畫出來的是圖:img、svg、picture,或背景是圖片。</summary>
    public static bool IsImage(RenderedNode r) => ImageTags.Contains(r.Tag) || r.HasBackgroundImage;

    /// <summary>
    /// 把圖片落差加進報告。參考圖或截圖解不開時丟 <see cref="InvalidOperationException"/>,由呼叫端決定怎麼告知。
    /// </summary>
    /// <param name="referencePath">參考圖(PNG/JPEG)。</param>
    /// <param name="screenshot">這次的整頁截圖(PNG)。座標:實作節點的框加上 <see cref="ScanResult.RenderedOrigin"/>。</param>
    /// <param name="referenceOrigin">
    /// 參考圖上,設計 frame 的原點在哪。預設是 <see cref="ScanResult.DesignOrigin"/>:快照截圖與 designImage 用的是設計稿本來的座標。
    /// Figma 匯出的圖就是 frame 本身,傳 <c>default</c>(圖的左上角就是 frame 的原點)。
    /// </param>
    public static async Task<ScanResult> CompareAsync(
        ScanResult result, string referencePath, byte[] screenshot, Box? referenceOrigin = null, CancellationToken ct = default)
    {
        var imageNodes = result.RenderedTree.DescendantsAndSelf()
            .Where(IsImage)
            .GroupBy(r => r.Selector)
            .ToDictionary(g => g.Key, g => g.First());
        if (!result.Report.Nodes.Any(n => imageNodes.ContainsKey(n.Selector))) return result;

        using var reference = await Decode(referencePath, () => ImageDesignSource.LoadPixelsAsync(referencePath, ct));
        using var current = await Decode("the page screenshot",
            () => Task.FromResult(Image.Load<Rgba32>(ImageDesignSource.PngAndJpegOnly, screenshot)));

        var changed = false;
        var nodes = result.Report.Nodes.Select(n =>
        {
            if (!imageNodes.TryGetValue(n.Selector, out var r)) return n;
            // 先看畫面有沒有不一樣,載入失敗只決定怎麼標:基準拍下來時就沒載入的圖,兩邊一樣,不是落差(野生實查:gov.tw 30 條)
            var diff = Compare(reference, Shift(n.DesignBox, referenceOrigin ?? result.DesignOrigin), current, Shift(n.RenderedBox, result.RenderedOrigin), r.ImageBroken);
            if (diff is null) return n;
            changed = true;
            var diffs = n.Diffs.Append(diff).ToList();
            return n with { Diffs = diffs, Severity = diffs.Max(d => d.Severity) };
        }).ToList();
        if (!changed) return result;

        return result with
        {
            Report = result.Report with { Nodes = nodes, Summary = FidelityEngine.Summarize(nodes, result.Report.Unmatched.Count) },
        };
    }

    /// <summary>
    /// 兩種落差分開判斷、分開回報(清單 #10,2.5.0)。以前內容與顏色合成一個分數共用 10% 門檻,
    /// 圖片顏色偏掉(色相轉 30°)多半過不了門檻(論文評估 5.9%)。
    ///   - 內容換了(<c>image</c>):結構差超過 <see cref="Tolerance"/>。內容都換了,顏色不另外報。
    ///   - 顏色偏了(<c>imageColor</c>):結構一樣、8×8 格平均色差(CIEDE2000)超過 <see cref="ColorTolerance"/>。
    /// </summary>
    private static PropDiff? Compare(Image<Rgba32> reference, Box referenceBox, Image<Rgba32> current, Box currentBox, bool broken)
    {
        if (Measure(reference, referenceBox, current, currentBox) is not { } m) return null;
        var content = m.Structure > Tolerance;
        if (!content && m.Color <= ColorTolerance) return null;
        if (broken)
            return new PropDiff("image", "same as the reference image", "(image not loaded)", null, null, Tolerance, Severity.Minor, DiffStatus.Missing, Soft: true);
        if (content)
            return new PropDiff("image", "same as the reference image", $"{Math.Round(m.Structure * 100):0}% different", null,
                Math.Round(m.Structure, 2), Tolerance, Severity.Minor, Soft: true);
        return new PropDiff("imageColor", "same colors as the reference image", $"ΔE {m.Color:0.#} on average", null,
            Math.Round(m.Color, 2), ColorTolerance, Severity.Minor, Soft: true);
    }

    /// <summary>結構差(0–1,相鄰格明暗方向不同的比例)與顏色差(8×8 格的平均 CIEDE2000)。</summary>
    internal readonly record struct ImageDifference(double Structure, double Color);

    /// <summary>兩張圖上兩個區域的差異。區域太小或大半在圖外是 null:比不出結果。</summary>
    internal static ImageDifference? Measure(Image<Rgba32> a, Box boxA, Image<Rgba32> b, Box boxB)
    {
        if (Clip(a, boxA) is not { } ra || Clip(b, boxB) is not { } rb) return null;

        using var structureA = Shrink(a, ra, 9, 8);
        using var structureB = Shrink(b, rb, 9, 8);
        var differentDirections = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                if (Direction(structureA, x, y) != Direction(structureB, x, y)) differentDirections++;

        using var colorA = Shrink(a, ra, 8, 8);
        using var colorB = Shrink(b, rb, 8, 8);
        var deltaSum = 0.0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                deltaSum += ColorDelta.Ciede2000(ToRgba(Flatten(colorA[x, y])), ToRgba(Flatten(colorB[x, y])));

        return new ImageDifference(differentDirections / 64.0, deltaSum / 64.0);
    }

    private static Rgba ToRgba(Rgba32 p) => new(p.R, p.G, p.B);

    private static Box Shift(Box b, Box origin) => new(b.X + origin.X, b.Y + origin.Y, b.W, b.H);

    private static async Task<Image<Rgba32>> Decode(string what, Func<Task<Image<Rgba32>>> load)
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (ex is ImageFormatException or UnknownImageFormatException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"could not read {what} for compare.images: {ex.Message}", ex);
        }
    }

    private static Rectangle? Clip(Image<Rgba32> image, Box box)
    {
        var x0 = Math.Max(0, (int)Math.Round(box.X));
        var y0 = Math.Max(0, (int)Math.Round(box.Y));
        var x1 = Math.Min(image.Width, (int)Math.Round(box.X + box.W));
        var y1 = Math.Min(image.Height, (int)Math.Round(box.Y + box.H));
        var (w, h) = (x1 - x0, y1 - y0);
        if (w < MinSide || h < MinSide || box.W <= 0 || box.H <= 0) return null;
        return w * h / (box.W * box.H) < MinVisibleFraction ? null : new Rectangle(x0, y0, w, h);
    }

    private static Image<Rgba32> Shrink(Image<Rgba32> image, Rectangle region, int width, int height)
        => image.Clone(c => c.Crop(region).Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Box,
        }));

    /// <summary>這一格到右邊那一格的明暗方向:-1、0(差不到門檻,算一樣亮)、1。</summary>
    private static int Direction(Image<Rgba32> image, int x, int y)
    {
        var delta = Luma(Flatten(image[x, y])) - Luma(Flatten(image[x + 1, y]));
        return Math.Abs(delta) <= BrightnessMargin ? 0 : Math.Sign(delta);
    }

    private static double Luma(Rgba32 p) => 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;

    /// <summary>半透明合成到白底:設計圖可能有透明區域,截圖沒有。</summary>
    private static Rgba32 Flatten(Rgba32 p)
    {
        if (p.A == 255) return p;
        var alpha = p.A / 255.0;
        byte Over(byte v) => (byte)Math.Round(v * alpha + 255 * (1 - alpha));
        return new Rgba32(Over(p.R), Over(p.G), Over(p.B));
    }
}
