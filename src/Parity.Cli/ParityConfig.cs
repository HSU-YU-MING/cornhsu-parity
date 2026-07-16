using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>repo 根目錄的 parity.config.json——設定即程式碼,team + CI 共用同一份(規畫書 4.9)。</summary>
public sealed class ParityConfig
{
    /// <summary>Figma 檔案 key。與 DesignFile 二擇一。</summary>
    public string? FigmaFileKey { get; set; }

    /// <summary>本機設計 JSON 檔路徑(離線測試/示範用)。與 FigmaFileKey 二擇一。</summary>
    public string? DesignFile { get; set; }

    /// <summary>"env:FIGMA_TOKEN" 表示從環境變數讀;token 不落地、不進 log。</summary>
    public string? DesignToken { get; set; }

    /// <summary>parity map 存的手動對應檔(圖層名 → CSS selector)。</summary>
    public string? MapFile { get; set; }

    public List<TargetConfig> Targets { get; set; } = [];
    public CompareConfig Compare { get; set; } = new();
    public ToleranceConfig Tolerances { get; set; } = new();
    public List<string> Ignore { get; set; } = [];
    public GateConfig Gate { get; set; } = new();

    /// <summary>設定檔所在目錄(相對路徑的基準)。</summary>
    [JsonIgnore]
    public string BaseDirectory { get; set; } = ".";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ParityConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"找不到設定檔:{path}(可用 `parity init` 產生範本)", path);
        var config = JsonSerializer.Deserialize<ParityConfig>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidOperationException($"設定檔解析失敗:{path}");
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return config;
    }

    /// <summary>從 cwd 往上找 parity.config.json。</summary>
    public static string? FindConfigFile(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "parity.config.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        return null;
    }

    /// <summary>解析 designToken:"env:FIGMA_TOKEN" → 讀環境變數;其餘視為字面值。</summary>
    public string? ResolveToken()
    {
        if (string.IsNullOrWhiteSpace(DesignToken)) return Environment.GetEnvironmentVariable("FIGMA_TOKEN");
        return DesignToken.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable(DesignToken[4..])
            : DesignToken;
    }

    public Tolerances ToEngineTolerances() => new(
        SizePx: Tolerances.SizePx,
        SpacingPx: Tolerances.SpacingPx,
        ColorDeltaE: Tolerances.ColorDeltaE,
        FontSizePx: Tolerances.FontSizePx);

    /// <summary>gate 判定:報告裡有任何 failOn 等級的落差 → 該擋(規畫書「還原度把關」)。</summary>
    public bool ShouldFail(FidelityReport report)
    {
        var failOn = Gate.FailOn.Select(s => Enum.Parse<Severity>(s, ignoreCase: true)).ToHashSet();
        return report.Nodes.SelectMany(n => n.Diffs).Any(diff => failOn.Contains(diff.Severity));
    }
}

public sealed class TargetConfig
{
    public string Route { get; set; } = "/";
    /// <summary>Figma frame 的 nodeId(如 "10:2");DesignFile 模式下可留空(整棵樹)。</summary>
    public string Frame { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class CompareConfig
{
    /// <summary>"relative":預設不比絕對 x/y(規畫書 4.8)。目前唯一支援值。</summary>
    public string Position { get; set; } = "relative";
}

public sealed class ToleranceConfig
{
    public double SizePx { get; set; } = 2;
    public double SpacingPx { get; set; } = 2;
    public double ColorDeltaE { get; set; } = 2.0;
    public double FontSizePx { get; set; } = 0.5;
}

public sealed class GateConfig
{
    public List<string> FailOn { get; set; } = ["critical", "serious"];
}
