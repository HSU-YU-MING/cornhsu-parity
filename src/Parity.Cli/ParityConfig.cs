using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.ImplementationSources;

namespace Parity.Cli;

/// <summary>repo 根目錄的 parity.config.json——設定即程式碼,team + CI 共用同一份(規畫書 4.9)。</summary>
public sealed class ParityConfig
{
    /// <summary>Figma 檔案 key。與 DesignFile 二擇一。</summary>
    public string? FigmaFileKey { get; set; }

    /// <summary>
    /// 報告裡「圖層名連回 Figma」要用的 file key:只有設計來源**真的是 Figma** 時才有。
    /// designFile 優先於 figmaFileKey(ScanSession);init 範本預設帶 figmaFileKey,改走 snapshot
    /// 時沒刪掉的話,designId 是 CSS selector,拿它組 figma.com 連結點了就壞。
    /// </summary>
    [JsonIgnore]
    public string? FigmaLinkFileKey => DesignFile is null ? FigmaFileKey : null;

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

    // accept(2.5.0):一條一條收下的落差,每條都要寫原因。寫成 null 與 targets 一樣在載入時就報錯、指名欄位
    private List<AcceptConfig>? _accept = [];

    public List<AcceptConfig> Accept
    {
        get => _accept ?? throw NullSection("accept", "an array of accepted gaps (use [] for none, or remove the key)");
        set => _accept = value;
    }

    public GateConfig Gate
    {
        get => _gate ?? throw NullSection("gate", "an object, e.g. { \"failOn\": [\"critical\", \"serious\"] } (or remove the key for the defaults)");
        set => _gate = value;
    }

    // auth(2.5.0):要登入才看得到的頁面。寫成 null 一樣在載入時就報錯
    private AuthConfig? _auth = new();

    public AuthConfig Auth
    {
        get => _auth ?? throw NullSection("auth", "an object, e.g. { \"storageState\": \".parity/auth.json\" } (or remove the key)");
        set => _auth = value;
    }

    // stats(2.1.0):匿名使用統計的開關與選填的專案 ID(stats-collector 的 PROTOCOL.md §3、§7.1)。
    // 寫成 `"stats": null` 時與 gate/compare 一樣在載入時就報錯、指名欄位——它是隱私開關,不猜使用者想不想送。
    private StatsConfig? _stats = new();

    public StatsConfig Stats
    {
        get => _stats ?? throw NullSection("stats", "an object, e.g. { \"share\": false } (or remove the key)");
        set => _stats = value;
    }

    /// <summary>設定檔對匿名統計的表態:false = 不送;true = 要送;null = 沒寫(照預設)。載入時已驗過只有這三種。</summary>
    [JsonIgnore]
    public bool? ShareStats => Stats.Share.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>設定檔所在目錄(相對路徑的基準)。</summary>
    [JsonIgnore]
    public string BaseDirectory { get; set; } = ".";

    /// <summary>設定檔路徑(錯誤訊息用;程式裡直接 new 出來的設定沒有)。</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    private InvalidOperationException NullSection(string field, string expected)
        => new($"config {SourcePath ?? "(in memory)"}: \"{field}\" is null — it must be {expected}.");

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
        return config;
    }

    /// <summary>設定值驗證——拼錯的 gate 等級或超界的門檻,在載入時就給看得懂的錯,而不是跑到一半炸。</summary>
    private void Validate(string path)
    {
        if (Gate.FailOn is null)
            throw NullSection("gate.failOn", "an array of severities, e.g. [\"critical\", \"serious\"] (use [] to gate on match integrity only)");
        foreach (var s in Gate.FailOn)
            if (s is null || !FailOnSeverities.ContainsKey(s))
                throw new InvalidOperationException(
                    $"config {path}: gate.failOn contains \"{ConsoleText.Safe(s)}\", which is not a valid severity " +
                    "(use: minor, medium, serious, critical — one per string).");
        if (Gate.FailOnUnmatched is null)
            throw NullSection("gate.failOnUnmatched",
                "an array of unmatched reasons, e.g. [\"text-not-found\"] or [\"*\"] (use [] to never gate on unmatched nodes)");
        foreach (var r in Gate.FailOnUnmatched)
            if (r is null || !IsUnmatchedGateValue(r))
                throw new InvalidOperationException(
                    $"config {path}: gate.failOnUnmatched contains \"{ConsoleText.Safe(r)}\", which is not an unmatched reason " +
                    $"(use: {string.Join(", ", Matcher.UnmatchedReasons)}, or \"*\" for every reason — one per string).");
        if (Gate.MinMatchRate is < 0 or > 1)
            throw new InvalidOperationException(
                $"config {path}: gate.minMatchRate must be between 0 and 1 (got: {Gate.MinMatchRate}).");
        if (!string.Equals(Compare.Position, "relative", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Compare.Position, "none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"config {path}: compare.position is \"{Compare.Position}\", which is not valid (use: relative, none).");
        // compare.extras 刻意是字串不是布林(與 position 同一種寫法):"false"、"true"、"yes" 這類手誤在載入時就報錯
        if (!string.Equals(Compare.Extras, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Compare.Extras, "list", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"config {path}: compare.extras is {(Compare.Extras is null ? "null" : $"\"{ConsoleText.Safe(Compare.Extras)}\"")}, " +
                "which is not valid (use: none, list).");
        if (Compare.Text?.ToLowerInvariant() is not ("soft" or "gate" or "none"))
            throw new InvalidOperationException(
                $"config {path}: compare.text is {(Compare.Text is null ? "null" : $"\"{ConsoleText.Safe(Compare.Text)}\"")}, " +
                "which is not valid (use: soft, gate, none).");
        if (Compare.Images?.ToLowerInvariant() is not ("none" or "soft"))
            throw new InvalidOperationException(
                $"config {path}: compare.images is {(Compare.Images is null ? "null" : $"\"{ConsoleText.Safe(Compare.Images)}\"")}, " +
                "which is not valid (use: none, soft).");
        if (DesignImage is not null && DesignFile is null)
            throw new InvalidOperationException(
                $"config {path}: designImage requires a companion designFile (an annotation file in DesignNode format; " +
                "fill may be omitted and sampled from the image).");
        // accept:每條要說清楚收下哪一條落差、為什麼。拼錯的屬性名永遠對不到,收下的落差會靜靜地照擋——所以載入時就報錯
        for (var i = 0; i < Accept.Count; i++)
        {
            var entry = Accept[i] ?? throw NullSection($"accept[{i}]", "an object like { \"layer\": \"…\", \"prop\": \"…\", \"reason\": \"…\" }");
            if (string.IsNullOrWhiteSpace(entry.Layer))
                throw new InvalidOperationException($"config {path}: accept[{i}] needs a \"layer\" (the design layer name the report shows).");
            if (string.IsNullOrWhiteSpace(entry.Prop))
                throw new InvalidOperationException($"config {path}: accept[{i}] needs a \"prop\" (the property the report shows, e.g. fontSize).");
            if (!AcceptableProps.Contains(entry.Prop))
                throw new InvalidOperationException(
                    $"config {path}: accept[{i}].prop is \"{ConsoleText.Safe(entry.Prop)}\", which is not a property Parity reports " +
                    $"(use one of: {string.Join(", ", UsageStats.PropFamilies.Select(p => p.Prop))}).");
            if (string.IsNullOrWhiteSpace(entry.Reason))
                throw new InvalidOperationException(
                    $"config {path}: accept[{i}] needs a \"reason\" — say why this gap is accepted, so the next person reading the report knows.");
        }
        // auth.headers:標頭名不能空、值要是字串。值本身(可能是 token)不進任何訊息
        foreach (var (name, value) in Auth.Headers ?? [])
            if (string.IsNullOrWhiteSpace(name) || value is null)
                throw new InvalidOperationException(
                    $"config {path}: auth.headers needs a header name and a string value for each entry " +
                    "(e.g. \"Authorization\": \"env:PARITY_AUTH\"; got an empty name or a null value).");
        if (Auth.StorageState is { } state && string.IsNullOrWhiteSpace(state))
            throw new InvalidOperationException($"config {path}: auth.storageState is empty — give the path `parity login` saves to, or remove it.");
        // targets[].steps:動作拼錯、少了值,在載入時就擋——跑到一半才發現,量到的是沒打開選單的畫面
        // 讀欄位不讀 Targets:targets 是 null 或有 null 項時,要到用的時候才報錯(report/push 不碰 targets,行為不變)
        var targets = _targets ?? [];
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t?.Steps is not { Count: > 0 } steps) continue;
            if (Parity.Engine.ImplementationSources.Web.WebImplementationSource.IsAttachUrl(t.Url))
                throw new InvalidOperationException(
                    $"config {path}: targets[{i}] attaches to a running app (cdp:) and also has steps — steps would click around in your " +
                    "live window, so they are not allowed there. Put the app in the state you want before running the check.");
            t.ParsedSteps = steps.Select((s, j) => PageSteps.Parse(s, $"targets[{i}].steps[{j}]", path)).ToList();
        }
        // stats.share 只收 true/false:寫成字串 "false" 是最可能的手誤,而它是隱私開關——不能當成「沒表態」照送
        if (Stats.Share.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException(
                $"config {path}: stats.share must be true or false (got: {ConsoleText.Safe(Stats.Share.GetRawText())}) — " +
                "write \"stats\": { \"share\": false } to turn anonymous usage statistics off.");
        // stats.projectId:協定 §7.1 的格式,格式不對是載入錯誤(exit 2),不是安靜丟掉
        if (Stats.ProjectId is { } id && !IsValidProjectId(id))
            throw new InvalidOperationException(
                $"config {path}: stats.projectId must be 16–64 characters of A-Z, a-z, 0-9, '_' or '-' " +
                $"(got: \"{ConsoleText.Safe(id)}\", {id.Length} characters). Make one up at random — " +
                "never derive it from the repository name or anything else that identifies you.");
    }

    /// <summary>accept[].prop 的白名單:引擎報得出來的所有屬性(就是統計協定的封閉集,UsageStatsTests 核對引擎沒有漏)。大小寫要對。</summary>
    private static readonly HashSet<string> AcceptableProps = UsageStats.PropFamilies.Select(p => p.Prop).ToHashSet(StringComparer.Ordinal);

    /// <summary>stats.projectId 的格式(PROTOCOL.md §7.1):16–64 字元,只能有 A-Za-z0-9_-。</summary>
    internal static bool IsValidProjectId(string id)
        => id.Length is >= 16 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>
    /// gate.failOn 的白名單(不分大小寫)——Validate 與 GateFailReason 共用這一張表。
    /// 2.0.0 前用 <c>Enum.TryParse</c>,它另外收下 <c>none</c>、數字與逗號旗標語法(<c>"serious, critical"</c>
    /// = 3|4 = 7):不是讓 gate 靜默失效,就是被讀成別的等級。
    /// </summary>
    private static readonly Dictionary<string, Severity> FailOnSeverities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["minor"] = Severity.Minor,
        ["medium"] = Severity.Medium,
        ["serious"] = Severity.Serious,
        ["critical"] = Severity.Critical,
    };

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

    /// <summary>
    /// 一個 target 的擷取參照補上登入態與步驟(2.5.0;check、snapshot、baseline、serve、mcp 都走這裡,兩邊才拍到同一個狀態)。
    /// cdp: 接現成視窗時不套登入態(那個視窗本來就是登入的)。env: 在這裡才讀,沒設就丟錯、指名變數,不印值。
    /// </summary>
    public ImplRef WithPageSetup(ImplRef reference, TargetConfig target)
    {
        if (Parity.Engine.ImplementationSources.Web.WebImplementationSource.IsAttachUrl(reference.Url)) return reference;
        return reference with
        {
            StorageStatePath = Auth.StorageState is { } state ? Path.GetFullPath(Path.Combine(BaseDirectory, state)) : null,
            ExtraHeaders = Auth.Headers is { Count: > 0 } headers
                ? headers.ToDictionary(h => h.Key, h => ResolveSecret(h.Value, $"auth.headers.{h.Key}"))
                : null,
            Steps = target.ParsedSteps is { Count: > 0 } steps
                ? steps.Select(s => s.Action == "fill" ? s with { Value = ResolveSecret(s.Value ?? "", $"a fill step of target {target.Route}") } : s).ToList()
                : null,
        };
    }

    /// <summary>"env:NAME" → 環境變數(沒設就丟錯,只說變數名);其他字串原樣。</summary>
    internal static string ResolveSecret(string value, string field)
    {
        if (!value.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return value;
        var name = value[4..];
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{field} reads the environment variable {name}, which is not set.");
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

    /// <summary>compare.extras:"none"(預設)/ "list"(列出實作端多出來的元件,永遠不擋)。</summary>
    public bool ListExtras => string.Equals(Compare.Extras, "list", StringComparison.OrdinalIgnoreCase);

    /// <summary>compare.images:"none"(預設)/ "soft"(比圖片內容,會列出、不擋)。載入時已驗過只有這兩種。</summary>
    public bool CompareImages => string.Equals(Compare.Images, "soft", StringComparison.OrdinalIgnoreCase);

    /// <summary>compare.text:"soft"(預設)/ "gate"(會擋)/ "none"(不比)。載入時已驗過只有這三種。</summary>
    public TextComparison CompareText => Compare.Text.ToLowerInvariant() switch
    {
        "gate" => TextComparison.Gate,
        "none" => TextComparison.None,
        _ => TextComparison.Soft,
    };

    /// <summary>gate 判定:報告裡有任何 failOn 等級的落差 → 該擋(規畫書「還原度把關」)。</summary>
    public bool ShouldFail(FidelityReport report) => GateFailReason(report) is not null;

    /// <summary>
    /// gate 不通過的原因(null = 通過)。先驗配對可信度(它不過就只回它,exit 3),
    /// 再看 failOn 等級落差與 failOnUnmatched 的未配對原因(exit 1);兩條都擋時以「; 」接起來,看得出是哪條擋的。
    /// </summary>
    public string? GateFailReason(FidelityReport report)
    {
        if (MatchIntegrityFailure(report) is { } untrusted) return untrusted;
        var failOn = Gate.FailOn.Select(s => FailOnSeverities[s]).ToHashSet();
        var reasons = new List<string>(2);
        // soft 落差(font-family)不擋 gate(2.0.0 起;與分數口徑一致——soft 也不扣分)
        if (report.Nodes.SelectMany(n => n.Diffs).Any(diff => !diff.Soft && failOn.Contains(diff.Severity)))
            reasons.Add($"has {string.Join("/", Gate.FailOn)} severity diffs");
        if (UnmatchedGateFailure(report) is { } unmatched)
            reasons.Add(unmatched);
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    /// <summary>
    /// gate.failOnUnmatched:未配對清單裡有要擋的原因 → 原因的說明(null = 不擋)。與配對可信度無關;
    /// <c>check --baseline</c>(已有基準時)不套用這條。給主控台用:圖層名不是工具寫的,先過 ConsoleText.Safe
    /// (換行會變成 workflow command)。它擋不住 Markdown,貼進報告要用 <see cref="UnmatchedGateNotes"/>。
    /// </summary>
    public string? UnmatchedGateFailure(FidelityReport report)
        => DescribeUnmatchedGate(report, ConsoleText.Safe, ConsoleText.Safe);

    /// <summary>同一句話有兩個出口,怕的東西不同:主控台怕控制字元,Markdown 怕語法。所以字串怎麼包由呼叫端決定。</summary>
    private string? DescribeUnmatchedGate(FidelityReport report, Func<string, string> name, Func<string, string> reason)
    {
        var gated = Gate.FailOnUnmatched;
        if (gated.Count == 0) return null;
        var all = gated.Contains("*");
        var hits = report.Unmatched
            .Where(u => all || gated.Contains(u.Reason, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (hits.Count == 0) return null;

        const int namesPerReason = 5;
        var byReason = hits.GroupBy(u => u.Reason, StringComparer.Ordinal).Select(g =>
        {
            var names = g.Take(namesPerReason).Select(u => name(u.DesignLayer));
            var more = g.Count() > namesPerReason ? $", +{g.Count() - namesPerReason} more" : "";
            return $"{reason(g.Key)}: {string.Join(", ", names)}{more}";
        });
        return $"{hits.Count} unmatched design node{(hits.Count == 1 ? "" : "s")} blocked by gate.failOnUnmatched " +
            $"({string.Join("; ", byReason)})";
    }

    /// <summary>Markdown 報告的警示:各報告被 failOnUnmatched 擋下的節點(空 = 沒有),附 route 前綴。圖層名放進程式碼區段。</summary>
    public List<MarkdownNote> UnmatchedGateNotes(IEnumerable<FidelityReport> reports)
        => CollectNotes(reports, r => DescribeUnmatchedGate(r, MarkdownReport.Name, MarkdownReport.Word));

    /// <summary>GATE FAIL / PASS 那一行括號裡的把關條件。failOnUnmatched 沒設時與以前一字不差(含 failOn 空的 "fail on: ")。</summary>
    public string GateCriteria()
    {
        if (Gate.FailOnUnmatched.Count == 0) return $"fail on: {string.Join(", ", Gate.FailOn)}";
        var unmatched = $"fail on unmatched: {string.Join(", ", Gate.FailOnUnmatched)}";
        return Gate.FailOn.Count == 0 ? unmatched : $"fail on: {string.Join(", ", Gate.FailOn)}; {unmatched}";
    }

    /// <summary>failOnUnmatched 的白名單:Matcher 寫得出來的原因(不分大小寫),或 "*" = 全部。</summary>
    private static bool IsUnmatchedGateValue(string value)
        => value == "*" || Matcher.UnmatchedReasons.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>各報告的 gate 不通過原因(空 = 全過),附 route 前綴。</summary>
    public List<string> GateFailReasons(IEnumerable<FidelityReport> reports)
        => CollectReasons(reports, GateFailReason);

    /// <summary>各報告的配對可信度問題(空 = 可信)。baseline 模式也不可豁免。</summary>
    public List<string> MatchIntegrityFailures(IEnumerable<FidelityReport> reports)
        => CollectReasons(reports, MatchIntegrityFailure);

    /// <summary>Markdown 報告的警示:各報告的配對可信度問題。句子裡只有工具寫的字與數字,要包的只有 route。</summary>
    public List<MarkdownNote> MatchIntegrityNotes(IEnumerable<FidelityReport> reports)
        => CollectNotes(reports, MatchIntegrityFailure);

    private static List<MarkdownNote> CollectNotes(
        IEnumerable<FidelityReport> reports, Func<FidelityReport, string?> check)
        => reports.Select(r => (r.Route, Reason: check(r)))
            .Where(x => x.Reason is not null)
            .Select(x => new MarkdownNote($"{MarkdownReport.Word(x.Route)}: {x.Reason}"))
            .ToList();

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

    /// <summary>
    /// 量測之前在頁面上做的步驟(2.5.0):<c>{ "click": "#menu" }</c>、<c>{ "hover": … }</c>、<c>{ "waitFor": … }</c>、
    /// <c>{ "fill": "#q", "value": "…" }</c>(value 可寫 env:NAME)、<c>{ "press": "Escape" }</c>(可加 <c>"in": selector</c>)、
    /// <c>{ "wait": 500 }</c>(毫秒,最多 10000)。原樣收進來,載入時由 <see cref="PageSteps.Parse"/> 驗過、存進 <see cref="ParsedSteps"/>。
    /// </summary>
    public List<JsonElement>? Steps { get; set; }

    [JsonIgnore]
    public IReadOnlyList<PageStep>? ParsedSteps { get; internal set; }
}

/// <summary>"auth" 區段(2.5.0):要登入才看得到的頁面。cdp: 接現成視窗的 target 不套用。</summary>
public sealed class AuthConfig
{
    /// <summary>Playwright 的 storage state 檔(cookie + localStorage),相對於設定檔。<c>parity login</c> 產生。**含登入 cookie,不要 commit。**</summary>
    public string? StorageState { get; set; }

    /// <summary>每個請求多帶的 HTTP 標頭。值可寫 env:NAME(建議:設定檔會進版控)。</summary>
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>targets[].steps 的解析(2.5.0)。寫法刻意短:一個動作一個 key,值就是對象。</summary>
public static class PageSteps
{
    /// <summary>動作 → 這個動作另外可以帶的 key。</summary>
    private static readonly Dictionary<string, string[]> Actions = new(StringComparer.Ordinal)
    {
        ["click"] = [],
        ["hover"] = [],
        ["waitFor"] = [],
        ["fill"] = ["value"],
        ["press"] = ["in"],
        ["wait"] = [],
    };

    internal const int MaxWaitMs = 10_000;

    public static PageStep Parse(JsonElement step, string where, string path)
    {
        Exception Bad(string why) => new InvalidOperationException($"config {path}: {where} {why}");
        if (step.ValueKind != JsonValueKind.Object)
            throw Bad("must be an object like { \"click\": \"#menu\" }.");
        var keys = step.EnumerateObject().Select(p => p.Name).ToList();
        var actions = keys.Where(Actions.ContainsKey).ToList();
        if (actions.Count != 1)
            throw Bad($"needs exactly one action ({string.Join(", ", Actions.Keys)}); got {(keys.Count == 0 ? "none" : string.Join(", ", keys.Select(ConsoleText.Safe)))}.");
        var action = actions[0];
        if (keys.FirstOrDefault(k => k != action && !Actions[action].Contains(k)) is { } extra)
            throw Bad($"has \"{ConsoleText.Safe(extra)}\", which {action} does not take" +
                (Actions[action].Length == 0 ? "." : $" (it takes: {string.Join(", ", Actions[action])})."));

        string Text(string key, bool required)
        {
            if (!step.TryGetProperty(key, out var v))
                return required ? throw Bad($"needs \"{key}\".") : null!;
            if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()) && key != "value")
                throw Bad($"\"{key}\" must be a non-empty string.");
            return v.GetString()!;
        }

        return action switch
        {
            "wait" => step.GetProperty("wait") is { ValueKind: JsonValueKind.Number } ms && ms.TryGetInt32(out var n) && n is >= 0 and <= MaxWaitMs
                ? new PageStep("wait", Milliseconds: n)
                : throw Bad($"\"wait\" must be a whole number of milliseconds from 0 to {MaxWaitMs}."),
            "fill" => new PageStep("fill", Text("fill", true), Text("value", true)),
            "press" => new PageStep("press", step.TryGetProperty("in", out _) ? Text("in", true) : null, Text("press", true)),
            _ => new PageStep(action, Text(action, true)),
        };
    }
}

/// <summary>
/// "accept" 的一條(2.5.0):收下一條落差並寫下原因。收下的落差照樣列在報告裡(標明 accepted 與原因),但跟軟落差一樣
/// 不擋 gate、不扣分、`--baseline` 不算新增。layer + prop 必填;route、selector、actual 是選填的收窄條件。
/// </summary>
public sealed class AcceptConfig
{
    /// <summary>只收這個 target 的(選填;沒寫 = 每個 target)。</summary>
    public string? Route { get; set; }

    /// <summary>設計端的圖層名,跟報告上顯示的一字不差(必填)。</summary>
    public string Layer { get; set; } = "";

    /// <summary>頁面元素的 selector(選填):同名圖層有好幾個時用來指定是哪一個。</summary>
    public string? Selector { get; set; }

    /// <summary>屬性名,例如 fontSize(必填)。</summary>
    public string Prop { get; set; } = "";

    /// <summary>
    /// 只在頁面上的值剛好是這個時才收(選填,跟報告的 actual 一字不差)。值又變了,落差就回來——
    /// 收下的是「改成 18px」這個決定,不是「字級以後怎樣都行」。
    /// </summary>
    public string? Actual { get; set; }

    /// <summary>為什麼收下(必填)。會寫進報告。</summary>
    public string Reason { get; set; } = "";
}

public sealed class CompareConfig
{
    /// <summary>
    /// "relative"(預設):比「相對位置」——相對最近兄弟/父層的偏移(規畫書 4.8),不比絕對 x/y。
    /// "none":位置完全不比。
    /// </summary>
    public string Position { get; set; } = "relative";

    /// <summary>
    /// "none"(預設):只看設計稿裡有的節點。"list":另外列出實作端多出來的元件(報告的 <c>extras</c>),
    /// 只是清單——不進 gate、分數與 summary。不分大小寫;其他值載入即報錯。
    /// </summary>
    public string Extras { get; set; } = "none";

    /// <summary>
    /// 文字內容比對。"soft"(預設):會列出、不扣分、不擋;快照基準不比,動態內容每次都不同。
    /// "gate":硬落差(serious),會擋,快照基準也比。"none":不比。不分大小寫;其他值載入即報錯。
    /// </summary>
    public string Text { get; set; } = "soft";

    /// <summary>
    /// 圖片內容比對。"none"(預設):不比,每頁也不必多拍一張整頁截圖。"soft":拿參考圖(快照的參考截圖、designImage、
    /// Figma 匯出的 frame)與這次的截圖比,會列出、不扣分、不擋。Figma 來源每個 frame 要多打一次 API(有快取)。
    /// 不分大小寫;其他值載入即報錯。
    /// </summary>
    public string Images { get; set; } = "none";
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

/// <summary>
/// "stats" 區段(2.1.0):匿名使用統計(README「Anonymous usage statistics」;協定是 stats-collector 的 PROTOCOL.md)。
/// 區段裡拼錯的 key 跟整份設定檔一樣靜默忽略(載入規則一致;改成報錯是 major)。
/// </summary>
public sealed class StatsConfig
{
    /// <summary>
    /// false = 不送。型別刻意是 JsonElement:原樣收進來、載入時才驗(ParityConfig.Validate),寫成字串 "false"
    /// 這種手誤才能得到指名欄位的錯誤——宣告成 bool? 的話是 System.Text.Json 的型別錯誤訊息,看不出是哪個開關、該寫什麼。
    /// 沒寫 = Undefined;JSON 的 null 也算錯(隱私開關不猜)。讀值用 <see cref="ParityConfig.ShareStats"/>。
    /// </summary>
    public JsonElement Share { get; set; }

    /// <summary>
    /// 選填的專案 ID:使用者自己隨機取、自己寫進來才有(同一個專案的多次執行才連得起來);沒有就是 null,
    /// 資料對應不到任何人。不能從 repo 名稱、路徑或任何機器資訊算出來(協定 §1)。
    /// </summary>
    public string? ProjectId { get; set; }
}

public sealed class GateConfig
{
    public List<string> FailOn { get; set; } = ["critical", "serious"];

    /// <summary>
    /// 最低配對率(0–1)。低於此值 gate 直接不過——配不到的節點驗不到,配對率太低時
    /// 「沒落差」不代表「沒問題」。預設 0 = 不設門檻;但「完全 0 配對」永遠擋(那必是設定錯)。
    /// </summary>
    public double MinMatchRate { get; set; }

    /// <summary>
    /// 要擋的未配對原因(選填)。例:<c>["text-not-found"]</c> = 設計稿的文字整頁都找不到(元件不見)就 GATE FAIL(exit 1);
    /// <c>["*"]</c> = 任何未配對都擋。預設空 = 未配對只列出、不擋(舊設定檔結果不變)。
    /// 拼錯的原因載入時就報錯(exit 2):永遠對不到的原因會讓 gate 靜靜失效。<c>check --baseline</c> 不套用。
    /// </summary>
    public List<string> FailOnUnmatched { get; set; } = [];
}
