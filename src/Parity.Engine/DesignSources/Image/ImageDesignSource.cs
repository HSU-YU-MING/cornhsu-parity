using Parity.Engine.DesignSources.Json;
using Parity.Engine.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Parity.Engine.DesignSources.Image;

/// <summary>
/// 圖片設計來源(規畫書 M5):一張設計圖(PNG/JPG)+ 一份標註 JSON。
/// 這是「其他所有設計工具」的萬用轉接頭——XD/Sketch/PS 都能匯出圖片。
///
/// 標註檔就是 DesignNode 樹的 JSON(與 designFile 同格式,不發明新格式),差別在:
/// **fill 可以省略**——省略的非 TEXT 節點,顏色由引擎從圖片對應區域取樣補上
/// (這是圖片能給、手寫 JSON 給不了的真資料)。
/// TEXT 的字色刻意不取樣:反鋸齒把字緣混進背景色,取樣必不準——標註可手填,沒填就不比
/// (寧可漏、不可誤報,與位置比對同一哲學)。
///
/// 座標假設:標註框以圖片像素為單位、1x 縮放(retina 2x 圖請先縮回 1x 或標註用 2x 座標系一致即可,
/// 引擎只在「同一座標系內」比相對量)。
/// </summary>
public sealed class ImageDesignSource(string imagePath) : IDesignSource
{
    private readonly JsonDesignSource _annotations = new();

    public async Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"找不到設計圖:{imagePath}", imagePath);

        var root = await _annotations.GetFrameAsync(reference, ct);

        using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(imagePath, ct);
        return Fill(root, image);
    }

    private static DesignNode Fill(DesignNode node, Image<Rgba32> image)
        => node with
        {
            Fill = node.Fill is not null || node.Type == DesignNodeType.Text
                ? node.Fill
                : SampleDominant(image, node.Box),
            Children = node.Children.Select(c => Fill(c, image)).ToList(),
        };

    /// <summary>
    /// 區域主色:往內縮(避開反鋸齒/邊框的混色邊緣)後,取樣格點的眾數色。
    /// 區域太小或完全透明 → null(不比,誠實跳過)。
    /// </summary>
    internal static Rgba? SampleDominant(Image<Rgba32> image, Box box)
    {
        // 內縮:至少 2px、最多吃掉 25% 邊緣;縮完沒剩就用中心點
        var insetX = Math.Min(box.W * 0.25, Math.Max(2, box.W * 0.1));
        var insetY = Math.Min(box.H * 0.25, Math.Max(2, box.H * 0.1));
        var x0 = (int)Math.Round(box.X + insetX);
        var y0 = (int)Math.Round(box.Y + insetY);
        var x1 = (int)Math.Round(box.X + box.W - insetX);
        var y1 = (int)Math.Round(box.Y + box.H - insetY);
        if (x1 <= x0) x0 = x1 = (int)Math.Round(box.X + box.W / 2);
        if (y1 <= y0) y0 = y1 = (int)Math.Round(box.Y + box.H / 2);

        // 邊界裁切:標註框(部分)在圖片外 → 只取圖內部分;完全在外 → null
        x0 = Math.Clamp(x0, 0, image.Width - 1);
        y0 = Math.Clamp(y0, 0, image.Height - 1);
        x1 = Math.Clamp(x1, 0, image.Width - 1);
        y1 = Math.Clamp(y1, 0, image.Height - 1);
        if (box.X >= image.Width || box.Y >= image.Height || box.X + box.W <= 0 || box.Y + box.H <= 0)
            return null;

        // 最多 32×32 格點——大區域也只取 ~1k 像素,夠代表主色又不掃全圖
        var stepX = Math.Max(1, (x1 - x0) / 32);
        var stepY = Math.Max(1, (y1 - y0) / 32);

        var counts = new Dictionary<Rgba32, int>();
        for (var y = y0; y <= y1; y += stepY)
            for (var x = x0; x <= x1; x += stepX)
            {
                var p = image[x, y];
                if (p.A == 0) continue; // 全透明像素不算
                counts[p] = counts.GetValueOrDefault(p) + 1;
            }
        if (counts.Count == 0) return null;

        var dominant = counts.MaxBy(kv => kv.Value).Key;
        return new Rgba(dominant.R, dominant.G, dominant.B, dominant.A / 255.0);
    }
}
