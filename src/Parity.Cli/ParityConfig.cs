using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>repo 根目錄的 parity.config.json——設定即程式碼,team + CI 共用同一份(規畫書 4.9)。</summary>
public sealed class ParityConfig
{
    /// <summary>Figma 檔案 key。與 DesignFile 二擇一。</summary>
    public string? FigmaFileKey { get; set; }

    /// <summary>本機設計 JSON 檔路徑(離線測試/示範/snapshot 基準用)。與 FigmaFileKey 二擇一。</summary>
    public string? DesignFile { get; set; }

    /// <summary>
    /// 設計圖(PNG/JPG)路徑——搭配 DesignFile(此時 DesignFile 是「標註檔」,同 DesignNode 格式,
    /// fill 可省略,由圖片對應區域取樣補上)。XD/Sketch 等其他工具匯出圖片就能走這條。
    /// </summary>
    public string? DesignImage { get; set; }

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

    // 區段在 JSON 裡寫成 null(`"targets": null`)時,反序列化照樣成功、屬性就是 null——
    // 原本一碰就是 NullReferenceException,使用者只看到 "Object reference not set to an instance of an object."。
    // getter 改成丟指名欄位的錯。刻意在「用到時」才丟,不在載入時一律檢查:report/push/baseline list
    // 不碰 targets/tolerances,那些指令的行為(含 exit code)一點都不變;gate/compare 載入時就會讀
    // (Validate),所以照舊在載入時報錯。
    private List<TargetConfig>? _targets = [];
    private CompareConfig? _compare = new();
    private ToleranceConfig? _tolerances = new();
    private GateConfig? _gate = new();

    public List<TargetConfig> Targets
    {
        get
        {
            var targets = _targets ?? throw NullSection("targets", "an array of targets (use [] for none)");
            var hole = targets.IndexOf(null!);
            if (hole >= 0) throw NullSection($"targets[{hole}]", "a target object");
            return targets;
        }
        set => _targets = value;
    }

    public CompareConfig Compare
    {
        get => _compare ?? throw NullSection("compare", "an object, e.g. { \"position\": \"relative\" } (or remove the key)");
        set => _compare = value;
    }

    public ToleranceConfig Tolerances
    {
        get => _tolerances ?? throw NullSection("tolerances", "an object, e.g. { \"sizePx\": 2 } (or remove the key for the defaults)");
        set => _tolerances = value;
    }

    public List<string> Ignore { get; set; } = [];

    public GateConfig Gate
    {
        get => _gate ?? throw NullSection("gate", "an object, e.g. { \"failOn\": [\"critical\", \"serious\"] } (or remove the key for the defaults)");
        set => _gate = value;
    }

    /// <summary>設定檔所在目錄(相對路徑的基準)。</summary>
    [JsonIgnore]
    public string BaseDirectory { get; set; } = ".";

    /// <summary>設定檔路徑(錯誤訊息用;程式裡直接 new 出來的設定沒有)。</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    private InvalidOperationException NullSection(string field, string expected)
        => new($"config {SourcePath ?? "(in memory)"}: \"{field}\" is null — it must be {expected}.");

    /// <summary>
    /// 載入時發現、但不擋載入的問題(Load 會印到 stderr)。例如 gate.failOn 裡讓 gate 靜默失效的值:
    /// 它們從 1.0 起就被接受,收緊成報錯是 semver 問題(待決定),在那之前至少要大聲講出來。
    /// </summary>
    [JsonIgnore]
    public List<string> Warnings { get; } = [];

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
                $"config file not found: {path} (run `parity init` to generate a template)", path);
        var config = JsonSerializer.Deserialize<ParityConfig>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidOperationException($"could not parse config file: {path}");
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        config.SourcePath = path;
        config.Validate(path);
        foreach (var w in config.Warnings)
            Console.Error.WriteLine($"\x1b[33mwarning: {w}\x1b[0m");
        return config;
    }

    /// <summary>設定值驗證——拼錯的 gate 等級或超界的門檻,在載入時就給看得懂的錯,而不是跑到一半炸。</summary>
    private void Validate(string path)
    {
        if (Gate.FailOn is null)
            throw NullSection("gate.failOn", "an array of severities, e.g. [\"critical\", \"serious\"] (use [] to gate on match integrity only)");
        foreach (var s in Gate.FailOn)
        {
            if (!Enum.TryParse<Severity>(s, ignoreCase: true, out var sev))
                throw new InvalidOperationException(
                    $"config {path}: gate.failOn contains \"{s}\", which is not a valid severity (use: minor, medium, serious, critical).");
            if (FailOnWarning(s, sev) is { } warning)
                Warnings.Add(warning);
        }
        if (Gate.MinMatchRate is < 0 or > 1)
            throw new InvalidOperationException(
                $"config {path}: gate.minMatchRate must be between 0 and 1 (got: {Gate.MinMatchRate}).");
        if (!string.Equals(Compare.Position, "relative", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Compare.Position, "none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"config {path}: compare.position is \"{Compare.Position}\", which is not valid (use: relative, none).");
        if (DesignImage is not null && DesignFile is null)
            throw new InvalidOperationException(
                $"config {path}: designImage requires a companion designFile (an annotation file in DesignNode format; " +
                "fill may be omitted and sampled from the image).");
    }

    private static readonly Severity[] GatingSeverities =
        [Severity.Minor, Severity.Medium, Severity.Serious, Severity.Critical];

    /// <summary>
    /// failOn 的值由 <c>Enum.TryParse</c> 解析,它另外收下 <c>none</c>、數字與逗號旗標語法
    /// (<c>"serious, critical"</c> = 3|4 = 7)。落差的嚴重度只會是 minor–critical,所以那些值
    /// 不是永遠對不到(gate 靜默失效),就是被讀成別的等級。
    /// </summary>
    private static string? FailOnWarning(string value, Severity parsed)
    {
        if (GatingSeverities.Any(g => string.Equals(g.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase)))
            return null;
        return GatingSeverities.Contains(parsed)
            ? $"gate.failOn value \"{value}\" is read as \"{parsed.ToString().ToLowerInvariant()}\" " +
              "(only minor, medium, serious, critical are documented; list each severity as its own string)."
            : $"gate.failOn value \"{value}\" never matches any diff, so it never fails the gate " +
              "(use: minor, medium, serious, critical; list each severity as its own string).";
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
        FontSizePx: Tolerances.FontSizePx,
        PositionPx: Tolerances.PositionPx);

    /// <summary>compare.position:"relative"(預設,比相對位置)/ "none"(不比位置)。</summary>
    public bool ComparePosition => !string.Equals(Compare.Position, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>gate 判定:報告裡有任何 failOn 等級的落差 → 該擋(規畫書「還原度把關」)。</summary>
    public bool ShouldFail(FidelityReport report) => GateFailReason(report) is not null;

    /// <summary>gate 不通過的原因(null = 通過)。先驗配對可信度,再看 failOn 等級落差。</summary>
    public string? GateFailReason(FidelityReport report)
    {
        if (MatchIntegrityFailure(report) is { } untrusted) return untrusted;
        var failOn = Gate.FailOn.Select(s => Enum.Parse<Severity>(s, ignoreCase: true)).ToHashSet();
        return report.Nodes.SelectMany(n => n.Diffs).Any(diff => failOn.Contains(diff.Severity))
            ? $"has {string.Join("/", Gate.FailOn)} severity diffs"
            : null;
    }

    /// <summary>各報告的 gate 不通過原因(空 = 全過),附 route 前綴。</summary>
    public List<string> GateFailReasons(IEnumerable<FidelityReport> reports)
        => CollectReasons(reports, GateFailReason);

    /// <summary>各報告的配對可信度問題(空 = 可信)。baseline 模式也不可豁免。</summary>
    public List<string> MatchIntegrityFailures(IEnumerable<FidelityReport> reports)
        => CollectReasons(reports, MatchIntegrityFailure);

    private static List<string> CollectReasons(
        IEnumerable<FidelityReport> reports, Func<FidelityReport, string?> check)
        => reports.Select(r => (r.Route, Reason: check(r)))
            .Where(x => x.Reason is not null)
            .Select(x => $"{x.Route}: {x.Reason}")
            .ToList();

    /// <summary>
    /// 配對可信度檢查(null = 可信)。gate 只看落差——沒配到就沒落差可擋,所以「全部沒配到」
    /// 會給假的 PASS(通常是 url/frame 指錯)。這是「結果可不可信」的底線:與 failOn 無關,
    /// baseline 模式也不可豁免(殘缺的 current 拿去比 baseline,會把一切誤判成「修好」)。
    /// </summary>
    public string? MatchIntegrityFailure(FidelityReport report)
    {
        var s = report.Summary;
        if (s.DesignNodes == 0)
            return "0 design nodes — frame/designFile is probably pointing at the wrong place; there is nothing to verify";
        if (s.Matched == 0)
            return $"0/{s.DesignNodes} design nodes matched — there is nothing to compare, results are not trustworthy " +
                "(check the target's url/frame, or fill in matches with `parity map`)";
        if (Gate.MinMatchRate > 0 && (double)s.Matched / s.DesignNodes < Gate.MinMatchRate)
            return $"match rate {s.Matched}/{s.DesignNodes} is below gate.minMatchRate ({Gate.MinMatchRate})";
        return null;
    }
}

public sealed class TargetConfig
{
    public string Route { get; set; } = "/";
    /// <summary>Figma frame 的 nodeId(如 "10:2");DesignFile 模式下可留空(整棵樹)。</summary>
    public string Frame { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>
    /// 視窗寬/高(選填)。預設 = 設計 frame 尺寸——RWD 多斷點通常**不用填**:
    /// 手機 frame 畫 375 寬,渲染就用 375 寬,media query 自然生效。
    /// 只有「frame 寬 ≠ 想測的視窗寬」時才需要覆蓋(snapshot 模式也吃這裡)。
    /// </summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class CompareConfig
{
    /// <summary>
    /// "relative"(預設):比「相對位置」——相對最近兄弟/父層的偏移(規畫書 4.8),不比絕對 x/y。
    /// "none":位置完全不比。
    /// </summary>
    public string Position { get; set; } = "relative";
}

public sealed class ToleranceConfig
{
    public double SizePx { get; set; } = 2;
    public double SpacingPx { get; set; } = 2;
    public double ColorDeltaE { get; set; } = 2.0;
    public double FontSizePx { get; set; } = 0.5;
    /// <summary>相對位置容差。比尺寸鬆:flow 版面裡文字行高的自然差異會讓後續元素有 2–4px 漂移。</summary>
    public double PositionPx { get; set; } = 4;
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
