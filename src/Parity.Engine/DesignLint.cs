using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;

namespace Parity.Engine;

/// <summary>lint 的一條違規:哪個圖層的哪個屬性,值是什麼、最近的 token 是誰。</summary>
public sealed record LintViolation(
    string Layer,
    string NodeId,
    string Prop,
    string Value,
    string? NearestToken,
    string? NearestValue,
    double? Distance);

/// <summary>
/// design lint:**只看設計稿這一邊**,驗「值有沒有落在 design token 的允許集合裡」。
/// 場景:設計師畫新頁面,要跟現有設計系統一致——顏色不能手滑生出 #2564EC、
/// 間距字級要落在規範階梯上。不需要瀏覽器、不需要配對,對象是還沒實作的稿。
///
/// 規則(v1):
///   - 顏色(fill):ΔE ≤ 容差內命中任一顏色 token 算過(容忍匯出/取樣的微差)
///   - 尺寸(fontSize / padding / itemSpacing / cornerRadius):等於任一尺寸 token 算過
///   - 0 值不檢查(0 不需要 token);沒有 fill / 該屬性的節點跳過
/// 違規時附「最近的 token」——訊息是「改成這個」,不是只有「你錯了」。
/// </summary>
public static class DesignLint
{
    public static IReadOnlyList<LintViolation> Run(
        DesignNode root, DesignTokens tokens, double colorDeltaE = 2.0)
    {
        var violations = new List<LintViolation>();

        foreach (var n in root.DescendantsAndSelf())
        {
            if (n.Fill is { IsTransparent: false } fill)
                CheckColor(violations, n, fill, tokens, colorDeltaE);

            if (n.Text?.FontSize is { } fs)
                CheckSize(violations, n, "fontSize", fs, tokens);

            if (n.Padding is { } pad)
                foreach (var v in new[] { pad.Top, pad.Right, pad.Bottom, pad.Left }.Distinct())
                    CheckSize(violations, n, "padding", v, tokens);

            if (n.ItemSpacing is { } gap)
                CheckSize(violations, n, "itemSpacing", gap, tokens);

            if (n.CornerRadius is { } radius)
                CheckSize(violations, n, "cornerRadius", radius, tokens);
        }
        return violations;
    }

    private static void CheckColor(
        List<LintViolation> vs, DesignNode n, Model.Rgba fill, DesignTokens tokens, double tolerance)
    {
        if (tokens.Colors.Count == 0) return; // 沒定義任何顏色 token → 這個維度不 lint

        (string Name, Model.Rgba Color)? nearest = null;
        var best = double.MaxValue;
        foreach (var t in tokens.Colors)
        {
            var d = ColorDelta.Ciede2000(fill, t.Color);
            if (d < best) { best = d; nearest = t; }
        }
        if (best <= tolerance) return;

        vs.Add(new LintViolation(n.Name, n.Id, "color", fill.ToHex(),
            nearest?.Name, nearest?.Color.ToHex(), Math.Round(best, 2)));
    }

    private static void CheckSize(
        List<LintViolation> vs, DesignNode n, string prop, double value, DesignTokens tokens)
    {
        if (value == 0) return;
        if (tokens.Sizes.Count == 0) return; // 沒定義任何尺寸 token → 這個維度不 lint

        (string Name, double Px)? nearest = null;
        var best = double.MaxValue;
        foreach (var t in tokens.Sizes)
        {
            var d = Math.Abs(value - t.Px);
            if (d < best) { best = d; nearest = t; }
        }
        if (best <= 0.01) return; // 命中(浮點誤差容忍)

        vs.Add(new LintViolation(n.Name, n.Id, prop, $"{value:0.##}px",
            nearest?.Name, $"{nearest?.Px:0.##}px", Math.Round(best, 2)));
    }
}
