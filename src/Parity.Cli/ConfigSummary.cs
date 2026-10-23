using System.Globalization;

namespace Parity.Cli;

/// <summary>
/// 設定檔的**結構摘要**(2.0.0,`parity bug-report` 用;日後其他需要描述設定檔的回報也用這一份):
/// 只取數量、來源類型、數值與列舉。**絕對不取**網址、route、frame/節點 id、selector、Figma fileKey、
/// token、檔案路徑——不是取了再遮,而是從頭就不讀進來。列舉欄位只印已知值,寫了別的就印 &lt;other&gt;
/// (它是自由字串,可能是任何內容)。
/// </summary>
internal sealed record ConfigSummary(
    int Targets,
    int TargetsWithWidth,
    int TargetsWithHeight,
    IReadOnlyDictionary<string, int> UrlKinds,
    string DesignSource,
    string DesignToken,
    bool HasTokensFile,
    bool HasMapFile,
    bool HasCustomBaselineFile,
    int IgnoreRules,
    ToleranceConfig Tolerances,
    IReadOnlyList<string> FailOn,
    double MinMatchRate,
    string ComparePosition)
{
    public static ConfigSummary From(ParityConfig config)
    {
        var targets = config.Targets;
        return new ConfigSummary(
            Targets: targets.Count,
            TargetsWithWidth: targets.Count(t => t.Width is not null),
            TargetsWithHeight: targets.Count(t => t.Height is not null),
            UrlKinds: targets.GroupBy(t => UrlKind(t.Url)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            DesignSource: config.DesignImage is not null ? "image (designImage + annotations)"
                : config.DesignFile is not null ? "design JSON file (designFile; snapshots use this too)"
                : config.FigmaFileKey is not null ? "figma"
                : "none set",
            DesignToken: config.DesignToken switch
            {
                null => "not set",
                var t when t.StartsWith("env:", StringComparison.OrdinalIgnoreCase) => "read from an environment variable", // 與 ParityConfig 同一個比法
                _ => "literal value in the config",
            },
            HasTokensFile: config.TokensFile is not null,
            HasMapFile: config.MapFile is not null,
            HasCustomBaselineFile: config.BaselineFile is not null,
            IgnoreRules: config.Ignore.Count,
            Tolerances: config.Tolerances,
            FailOn: config.Gate.FailOn.Select(s => s.ToLowerInvariant()).ToList(), // 載入時已驗過只有四個等級名
            MinMatchRate: config.Gate.MinMatchRate,
            // 載入時已驗過只有 relative/none(不分大小寫);印引擎讀到的小寫值
            ComparePosition: config.Compare.Position.ToLowerInvariant() is "relative" or "none" ? config.Compare.Position.ToLowerInvariant() : "<other>");
    }

    /// <summary>只看網址的種類,不留網址本身。</summary>
    private static string UrlKind(string url)
        => url.StartsWith("cdp:", StringComparison.OrdinalIgnoreCase) ? "cdp"
            : Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "file" ? u.Scheme
            : url.Length == 0 ? "empty"
            : "relative path";

    public IEnumerable<string> Lines()
    {
        static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
        yield return $"targets: {Targets} (with width: {TargetsWithWidth}, with height: {TargetsWithHeight})";
        yield return "target url kinds: " + string.Join(", ", UrlKinds.Select(k => $"{k.Key} {k.Value}"));
        yield return $"design source: {DesignSource}";
        yield return $"designToken: {DesignToken}";
        yield return $"tokensFile: {(HasTokensFile ? "set" : "not set")}; mapFile: {(HasMapFile ? "set" : "not set")}; " +
            $"baselineFile: {(HasCustomBaselineFile ? "custom" : "default")}; ignore rules: {IgnoreRules}";
        yield return $"tolerances: sizePx {N(Tolerances.SizePx)}, spacingPx {N(Tolerances.SpacingPx)}, " +
            $"colorDeltaE {N(Tolerances.ColorDeltaE)}, fontSizePx {N(Tolerances.FontSizePx)}, positionPx {N(Tolerances.PositionPx)}";
        yield return $"gate.failOn: {(FailOn.Count == 0 ? "(empty)" : string.Join(", ", FailOn))}";
        yield return $"gate.minMatchRate: {N(MinMatchRate)}";
        yield return $"compare.position: {ComparePosition}";
    }
}
