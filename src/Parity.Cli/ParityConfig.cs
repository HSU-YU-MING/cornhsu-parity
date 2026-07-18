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

    /// <summary>design token 檔(平面 JSON:{"token 名":"值"})。有給時,建議修法會提示對應的 token。</summary>
    public string? TokensFile { get; set; }

    /// <summary>
    /// baseline(回歸把關基準)的 SQLite 檔路徑。預設 parity.baseline.db(放 repo 根、**應 commit**——
    /// CI 的 check --baseline 才吃得到)。刻意不放 .parity/(那裡通常被 gitignore,會讓 CI 靜默失效)。
    /// </summary>
    public string? BaselineFile { get; set; }

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
    public bool ShouldFail(FidelityReport report) => GateFailReason(report) is not null;

    /// <summary>gate 不通過的原因(null = 通過)。先驗配對可信度,再看 failOn 等級落差。</summary>
    public string? GateFailReason(FidelityReport report)
    {
        if (MatchIntegrityFailure(report) is { } untrusted) return untrusted;
        var failOn = Gate.FailOn.Select(s => Enum.Parse<Severity>(s, ignoreCase: true)).ToHashSet();
        return report.Nodes.SelectMany(n => n.Diffs).Any(diff => failOn.Contains(diff.Severity))
            ? $"有 {string.Join("/", Gate.FailOn)} 等級落差"
            : null;
    }

    /// <summary>
    /// 配對可信度檢查(null = 可信)。gate 只看落差——沒配到就沒落差可擋,所以「全部沒配到」
    /// 會給假的 PASS(通常是 url/frame 指錯)。這是「結果可不可信」的底線:與 failOn 無關,
    /// baseline 模式也不可豁免(殘缺的 current 拿去比 baseline,會把一切誤判成「修好」)。
    /// </summary>
    public string? MatchIntegrityFailure(FidelityReport report)
    {
        var s = report.Summary;
        if (s.DesignNodes == 0)
            return "設計端 0 個節點——frame/designFile 可能指錯,沒有東西可驗";
        if (s.Matched == 0)
            return $"0/{s.DesignNodes} 個設計節點配對成功——沒有東西可比,結果不可信" +
                "(檢查 target 的 url/frame,或用 parity map 補配對)";
        if (Gate.MinMatchRate > 0 && (double)s.Matched / s.DesignNodes < Gate.MinMatchRate)
            return $"配對率 {s.Matched}/{s.DesignNodes} 低於 gate.minMatchRate({Gate.MinMatchRate})";
        return null;
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

    /// <summary>
    /// 最低配對率(0–1)。低於此值 gate 直接不過——配不到的節點驗不到,配對率太低時
    /// 「沒落差」不代表「沒問題」。預設 0 = 不設門檻;但「完全 0 配對」永遠擋(那必是設定錯)。
    /// </summary>
    public double MinMatchRate { get; set; }
}
