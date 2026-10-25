using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>
/// 匿名使用統計(2.1.0 起):<c>parity check</c> 跑出報告(exit 0/1/3)時送一次,<c>--reverse</c> 與其他指令不送。
/// **唯一真相源是 stats-collector 的 PROTOCOL.md**(與 XamlContrast 1.1 共用),這裡逐欄照做、不自己發明:
/// 要改欄位先改協定、收集站先上線新版,工具才能送(收集站對多一個、少一個欄位一律回 400)。
/// 用途是碩士論文的補充證據(設計稿與實作差多少)。三條不能退的線:
///   1. 只送統計數字:送的每個值都從報告的計數與 delta 算出來(<see cref="StatsPayload"/>),網址、route、名稱、
///      selector、路徑、顏色原值、截圖一律不碰;不帶 User-Agent、cookie 或任何自訂標頭。
///   2. 不影響結果:送不出去就算了(不重試、不印錯誤),exit code、stdout、報告檔一個字都不變。
///   3. 說明只印在 stderr(stdout 可能被別的程式讀)。
/// </summary>
internal static class UsageStats
{
    /// <summary>
    /// **預設打開,含 CI**(2026-10-23 維護者決定;PROTOCOL.md §3)。倫理審查若要求主動同意,改這一個值:
    /// false = 預設不送,設定檔寫 <c>"stats": { "share": true }</c> 或環境變數設 1/true/yes/on 才送。
    /// 協定在那種情況還要求「第一次在終端機詢問」,那段目前沒有實作;Action 的 <c>share-stats</c> 預設值也要一起決定。
    /// ⚠ 2.1.0 發佈前,維護者要先拿到指導教授(或倫理審查)對「預設打開」的確認(CLAUDE.md)。
    /// </summary>
    internal const bool SendByDefault = true;

    internal const string DefaultRoot = "https://stats.cornhsu.com";

    /// <summary>公開的資料說明頁(收什麼、做什麼用、保存多久)。</summary>
    internal const string InfoUrl = "https://stats.cornhsu.com/";

    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1.5);
    internal static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 開發版的版號前綴(協定 §3 第 5 條):從原始碼建出來的 `0.0.0-dev`(csproj 的佔位值,見 nuget-packages skill)、
    /// CI pack-smoke 的 `0.0.0-ci`、本機乾跑的 `0.0.0-local.*`——發行版永遠不會是 0.0.0。開發版不送。
    /// </summary>
    private const string DevelopmentPrefix = "0.0.0-";

    /// <summary>
    /// 測試用:非 null 時,只送往它放行的網址。測試行程的模組初始化設成「只准 loopback」——
    /// 哪條測試忘了把 CORNHSU_STATS_URL 指到假站,也送不到真的收集站、混不進研究資料。
    /// </summary>
    internal static Func<Uri, bool>? EndpointFilterForTests { get; set; }

    // ---------- 開關(協定 §3:依序檢查,第一個說不送的就不送)----------

    /// <summary>
    /// 這次送不送、送到哪。<paramref name="configShare"/> 是設定檔的 <c>stats.share</c>(null = 沒寫)。
    /// 純函式:環境變數由呼叫端傳入,測試不必動行程狀態。
    /// </summary>
    internal static StatsDecision Decide(
        Func<string, string?> variable, bool? configShare, string toolVersion, bool defaultOn = SendByDefault)
    {
        var custom = CustomRoot(variable("CORNHSU_STATS_URL"));
        var endpoint = new Uri((custom ?? DefaultRoot) + "/v1/parity");
        StatsDecision Off(StatsOff why) => new(endpoint, why);

        // 1. DO_NOT_TRACK:有值而且不是 0/false(不分大小寫)→ 不送。永遠優先
        if (variable("DO_NOT_TRACK") is { Length: > 0 } dnt
            && !dnt.Equals("0", StringComparison.Ordinal) && !dnt.Equals("false", StringComparison.OrdinalIgnoreCase))
            return Off(StatsOff.DoNotTrack);

        // 2. 共用的 CORNHSU_SHARE_STATS、工具自己的 PARITY_SHARE_STATS:1/true/yes/on = 不表態;
        //    其他任何非空值(0、false、no、off、打錯字)→ 不送。開關寫錯時,往「不送」那邊錯
        var optedIn = configShare == true;
        foreach (var (name, why) in new[] { ("CORNHSU_SHARE_STATS", StatsOff.SharedVariable), ("PARITY_SHARE_STATS", StatsOff.ToolVariable) })
        {
            if (variable(name) is not { Length: > 0 } value) continue;
            if (!IsYes(value)) return Off(why);
            optedIn = true;
        }

        // 3. 設定檔 "stats": { "share": false }。
        //    (4. Action 的 share-stats: false 由 action 設成 PARITY_SHARE_STATS=false,走上面第 2 條)
        if (configShare == false) return Off(StatsOff.Config);

        // 5. 開發版不送,除非設了 CORNHSU_STATS_URL——維護者自己的測試與本機建置不混進研究資料
        if (toolVersion.StartsWith(DevelopmentPrefix, StringComparison.Ordinal) && custom is null) return Off(StatsOff.DevelopmentBuild);

        // 都沒說不送 → 看預設(SendByDefault;改成主動同意時,要有人明說要送才送)
        return defaultOn || optedIn ? new(endpoint, null) : Off(StatsOff.NotOptedIn);
    }

    private static bool IsYes(string value)
        => value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CORNHSU_STATS_URL(測試與本機驗證用):只換根網址,路徑照舊加 <c>/v1/parity</c>。必須是絕對的 http/https
    /// 網址,否則當作沒設(Unix 上 <c>/x</c> 也是「絕對 URI」——file:,所以一定要看 scheme)。
    /// </summary>
    internal static string? CustomRoot(string? value)
        => !string.IsNullOrEmpty(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;

    /// <summary><c>CI</c> 或 <c>GITHUB_ACTIONS</c> 是 <c>true</c>(不分大小寫)。</summary>
    internal static bool IsCi(Func<string, string?> variable)
        => string.Equals(variable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(variable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>不送的原因,印在 --stats-preview(協定 §5)。只有常數字串:環境變數的值一律不印。</summary>
    internal static string Describe(StatsOff why) => why switch
    {
        StatsOff.DoNotTrack => "DO_NOT_TRACK is set",
        StatsOff.SharedVariable => "CORNHSU_SHARE_STATS is set to something other than 1, true, yes or on",
        StatsOff.ToolVariable => "PARITY_SHARE_STATS is set to something other than 1, true, yes or on",
        StatsOff.Config => "the config sets \"stats\": { \"share\": false }",
        StatsOff.DevelopmentBuild => "this is a development build (0.0.0-*) and CORNHSU_STATS_URL is not set",
        StatsOff.NotOptedIn => "anonymous usage statistics are off unless you opt in (\"stats\": { \"share\": true })",
        _ => throw new ArgumentOutOfRangeException(nameof(why), why, null),
    };

    // ---------- 資料(協定 §7.2)----------

    private static readonly double[] LengthEdges = [1, 2, 4, 8, 16, 32]; // px
    private static readonly double[] ColorEdges = [1, 2, 5, 10, 20];     // ΔE(CIEDE2000)
    private static readonly double[] WeightEdges = [100, 200, 300];       // 字重值

    /// <summary>
    /// 協定 §7.2 的**封閉集**:屬性 → 家族的分桶邊界(左閉右開);null = 無量值(fontFamily,buckets 是空陣列)。
    /// 順序照協定的表,也就是 JSON 裡 props 的順序。不在表上的屬性**不送**——引擎哪天多一種屬性,
    /// 先改協定(與收集站),不在這裡替它發明家族;UsageStatsTests 讀 DiffEngine 的原始碼核對引擎產得出的屬性都在表上。
    /// </summary>
    internal static readonly IReadOnlyList<(string Prop, double[]? Edges)> PropFamilies =
    [
        ("offsetX", LengthEdges), ("offsetY", LengthEdges), ("width", LengthEdges), ("height", LengthEdges),
        ("paddingTop", LengthEdges), ("paddingRight", LengthEdges), ("paddingBottom", LengthEdges), ("paddingLeft", LengthEdges),
        ("itemSpacing", LengthEdges), ("cornerRadius", LengthEdges), ("fontSize", LengthEdges), ("lineHeight", LengthEdges),
        ("letterSpacing", LengthEdges),
        ("color", ColorEdges), ("background", ColorEdges),
        ("fontWeight", WeightEdges),
        ("fontFamily", null),
    ];

    /// <summary>abs(delta) 落在第幾桶。邊界左閉右開:剛好等於邊界值的進右邊那一桶(長度的 1 → 「1–2」)。</summary>
    internal static int Bucket(double absDelta, double[] edges)
    {
        var i = 0;
        while (i < edges.Length && absDelta >= edges[i]) i++;
        return i;
    }

    /// <summary>從這次 check 的報告(與它的 exit code)算出 data。報告是正向的(--reverse 根本不會走到這裡)。</summary>
    internal static ParityStats Compute(IReadOnlyList<FidelityReport> reports, int exitCode, string designSource)
    {
        var index = PropFamilies.Select((f, i) => (f.Prop, i)).ToDictionary(x => x.Prop, x => x.i, StringComparer.Ordinal);
        var count = new int[PropFamilies.Count];
        var missing = new int[PropFamilies.Count];
        var buckets = PropFamilies.Select(f => new int[f.Edges is null ? 0 : f.Edges.Length + 1]).ToArray();

        // 每一條落差只進一個地方:status: missing 的計入 missing;有 delta 的進 buckets;
        // 兩者都不是的(fontFamily 的不一致,沒有數值差)只計入 count(收集站檢查 missing + sum(buckets) ≤ count)
        foreach (var diff in reports.SelectMany(r => r.Nodes).SelectMany(n => n.Diffs))
        {
            if (!index.TryGetValue(diff.Prop, out var i)) continue; // 封閉集以外:不送
            count[i]++;
            if (diff.Status == DiffStatus.Missing) missing[i]++;
            else if (diff.Delta is { } delta && PropFamilies[i].Edges is { } edges) buckets[i][Bucket(Math.Abs(delta), edges)]++;
        }

        return new ParityStats(
            Score: FidelityScore.Compute(reports), // 與 CLI 印的「fidelity score」同一個數
            GateFailed: exitCode != 0,             // 含 --baseline 的語意:看的是這次 check 真正的 exit code
            Pages: reports.Count,
            DesignSource: designSource,
            DesignNodes: reports.Sum(r => r.Summary.DesignNodes),
            Matched: reports.Sum(r => r.Summary.Matched),
            Severity: new SeverityTotals(
                Critical: reports.Sum(r => r.Summary.Critical),
                Serious: reports.Sum(r => r.Summary.Serious),
                Medium: reports.Sum(r => r.Summary.Medium),
                Minor: reports.Sum(r => r.Summary.Minor)),
            Props: PropFamilies
                .Select((f, i) => new PropStats(f.Prop, count[i], missing[i], buckets[i]))
                .Where(p => p.Count > 0) // 只列至少有一條落差的屬性
                .ToList());
    }

    /// <summary>
    /// 設計來源的類型。判斷順序與 <see cref="ScanSession.CreateDesignSource"/> 相同(designImage → designFile → Figma)。
    /// designFile 再分兩種:<c>parity snapshot</c> 寫的帶 schemaVersion 信封(1.0 起)→ snapshot;
    /// 裸 DesignNode 樹(手寫的 JSON,或 1.0 之前拍的舊快照)→ file。
    /// </summary>
    internal static string DesignSourceOf(ParityConfig config)
    {
        if (config.DesignImage is not null) return "image";
        if (config.DesignFile is not { } file) return "figma";
        return HasSnapshotEnvelope(Path.GetFullPath(Path.Combine(config.BaseDirectory, file))) ? "snapshot" : "file";
    }

    /// <summary>
    /// 設計 JSON 是不是 snapshot 信封。規則與 JsonDesignSource 一致:頂層有 schemaVersion(不分大小寫)而且是數字。
    /// 只掃頂層的 key、跳過值,不反序列化整棵樹(快照可以有好幾 MB)。讀不了就當不是(報告都跑出來了,通常讀得到)。
    /// </summary>
    internal static bool HasSnapshotEnvelope(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith("﻿"u8)) json = json[3..]; // 記事本存的 BOM:引擎讀得了,這裡也要
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 512,
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isVersion = string.Equals(reader.GetString(), "schemaVersion", StringComparison.OrdinalIgnoreCase);
                if (!reader.Read()) return false;
                if (isVersion) return reader.TokenType == JsonTokenType.Number;
                reader.Skip();
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal static StatsPayload Build(StatsEnvironment env, ParityConfig config, IReadOnlyList<FidelityReport> reports, int exitCode)
        => new(env.ToolVersion, env.Os, IsCi(env.Variable), config.Stats.ProjectId,
            Compute(reports, exitCode, DesignSourceOf(config)));

    /// <summary>
    /// 協定 §7 的 JSON,逐欄手寫(不靠序列化器的命名與省略規則):<c>projectId</c> 沒有就寫 null,不省略。
    /// 送出用緊湊的;<c>--stats-preview</c> 印縮排的(buckets 陣列維持一行,方便人看)。
    /// </summary>
    internal static string ToJson(StatsPayload p, bool indented)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("tool", "parity");
            w.WriteString("toolVersion", p.ToolVersion);
            w.WriteString("os", p.Os);
            w.WriteBoolean("ci", p.Ci);
            if (p.ProjectId is null) w.WriteNull("projectId");
            else w.WriteString("projectId", p.ProjectId);

            var d = p.Data;
            w.WriteStartObject("data");
            w.WriteNumber("score", d.Score);
            w.WriteBoolean("gateFailed", d.GateFailed);
            w.WriteNumber("pages", d.Pages);
            w.WriteString("designSource", d.DesignSource);
            w.WriteNumber("designNodes", d.DesignNodes);
            w.WriteNumber("matched", d.Matched);
            w.WriteStartObject("severity");
            w.WriteNumber("critical", d.Severity.Critical);
            w.WriteNumber("serious", d.Severity.Serious);
            w.WriteNumber("medium", d.Severity.Medium);
            w.WriteNumber("minor", d.Severity.Minor);
            w.WriteEndObject();
            w.WriteStartObject("props");
            foreach (var prop in d.Props)
            {
                w.WriteStartObject(prop.Prop);
                w.WriteNumber("count", prop.Count);
                w.WriteNumber("missing", prop.Missing);
                w.WritePropertyName("buckets");
                w.WriteRawValue("[" + string.Join(indented ? ", " : ",",
                    prop.Buckets.Select(b => b.ToString(CultureInfo.InvariantCulture))) + "]");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ---------- 告知(協定 §4)----------

    /// <summary>本機第一次要送的時候印的完整說明(協定 §4 的範本,換成 Parity 的名稱與開關)。</summary>
    internal const string FirstRunNotice = """
        Parity sends anonymous usage statistics: counts and score ranges only - no URLs, names,
        selectors, file paths, colours or screenshots, and no IP address is stored. They support a
        master's thesis on design fidelity.
        What is sent and why: https://stats.cornhsu.com/
        Preview it:  parity check --stats-preview   (sends nothing)
        Turn it off: "stats": { "share": false } in parity.config.json, or PARITY_SHARE_STATS=false,
                     CORNHSU_SHARE_STATS=false or DO_NOT_TRACK=1
        This notice is shown once on this machine.
        """;

    /// <summary>CI 上每次送出都印的一行(CI 的機器每次都是新的,標記檔留不下來)。</summary>
    internal const string CiLine =
        "Parity: anonymous usage statistics sent (counts only) - https://stats.cornhsu.com/ - turn off with DO_NOT_TRACK=1";

    /// <summary>標記檔:<c>&lt;ApplicationData&gt;/cornhsu/parity-stats-notice</c>。拿不到使用者設定目錄 → null(每次都印)。</summary>
    internal static string? MarkerPath(string? applicationData)
        => string.IsNullOrEmpty(applicationData) || !Path.IsPathRooted(applicationData)
            ? null // 沒有 HOME 的 Linux 容器會拿到空字串;拼出相對路徑的話,標記會寫進使用者的 repo
            : Path.Combine(applicationData, "cornhsu", "parity-stats-notice");

    private static void ShowFirstRunNotice(StatsEnvironment env)
    {
        var marker = MarkerPath(env.NoticeDirectory);
        if (marker is not null && File.Exists(marker)) return;
        env.Err.WriteLine(FirstRunNotice);
        if (marker is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker,
                "Parity showed its anonymous usage statistics notice on this machine. Delete this file to see it again.\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 標記寫不進去 → 下次照樣印(寧可多印)
        }
    }

    /// <summary>
    /// Action 的 job summary 那一行(協定 §4:送了,或為什麼沒送)。只在 action 設了 <c>PARITY_STATS_JOB_SUMMARY=true</c>
    /// 時寫——自己在 workflow 裡跑 <c>parity check</c> 的人,job summary 不會多出這行。內容只有常數字串。
    /// </summary>
    private static void AppendJobSummary(StatsEnvironment env, string line)
    {
        if (env.Variable("PARITY_STATS_JOB_SUMMARY") != "true") return;
        if (env.Variable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } path) return;
        try
        {
            File.AppendAllText(path, line + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal const string SentSummary =
        "**Parity** — anonymous usage statistics sent (counts only; [what and why](https://stats.cornhsu.com/)). " +
        "Turn them off with `share-stats: false`.";

    internal static string NotSentSummary(StatsOff why) => why == StatsOff.ToolVariable
        ? "**Parity** — anonymous usage statistics not sent: turned off by PARITY_SHARE_STATS (`share-stats: false` sets it)."
        : $"**Parity** — anonymous usage statistics not sent: {Describe(why)}.";

    // ---------- 進入點 ----------

    /// <summary>
    /// <c>parity check</c> 跑出報告之後呼叫一次(exit 0/1/3;--reverse 走 <see cref="PreviewReverse"/>)。
    /// 依序:決定送不送 → 預覽就只印不送 → 不送就只寫 job summary → 本機第一次印說明 → 送 → 送成功才印 CI 那一行與 job summary。
    /// **任何例外都吞掉**:統計絕不改變 check 的 exit code 與其他輸出(協定 §1)。
    /// </summary>
    internal static async Task AfterCheckAsync(
        StatsEnvironment env, ParityConfig config, IReadOnlyList<FidelityReport> reports, int exitCode, bool preview)
    {
        try
        {
            var decision = Decide(env.Variable, config.ShareStats, env.ToolVersion);
            if (preview)
            {
                PrintPreview(env, () => ToJson(Build(env, config, reports, exitCode), indented: true), decision);
                return;
            }
            if (decision.Off is { } off)
            {
                AppendJobSummary(env, NotSentSummary(off)); // 不送的時候 stderr 什麼都不印
                return;
            }

            var json = ToJson(Build(env, config, reports, exitCode), indented: false);
            var ci = IsCi(env.Variable);
            if (!ci) ShowFirstRunNotice(env); // 先告知再送
            if (!await SendAsync(decision.Endpoint, json)) return; // 送不出去就算了:不印、不寫 summary
            if (ci) env.Err.WriteLine(CiLine);
            AppendJobSummary(env, SentSummary);
        }
        catch (Exception)
        {
            // 統計的任何錯誤都不影響 check(協定 §1)
        }
    }

    /// <summary>--stats-preview 配 --reverse:反向檢視本來就不送,沒有資料可印。</summary>
    internal static void PreviewReverse(StatsEnvironment env)
        => env.Err.WriteLine("--stats-preview: nothing is sent. Would not be sent: check --reverse never sends anonymous usage statistics.");

    private static void PrintPreview(StatsEnvironment env, Func<string> json, StatsDecision decision)
    {
        string body;
        try
        {
            body = json();
        }
        catch (Exception ex)
        {
            env.Err.WriteLine($"--stats-preview: could not build the statistics ({ex.GetType().Name}); nothing is sent.");
            return;
        }
        env.Err.WriteLine("--stats-preview: nothing is sent. The anonymous usage statistics of this run:");
        env.Err.WriteLine(body);
        env.Err.WriteLine(decision.Off is { } off
            ? $"would not be sent: {Describe(off)}"
            : $"would be sent: POST {decision.Endpoint.AbsoluteUri}");
    }

    // ---------- 傳輸(協定 §6)----------

    /// <summary>連線 1.5 秒、整體 3 秒;不跟隨轉址;不帶 cookie;不帶追蹤標頭(traceparent 之類)。</summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectTimeout = ConnectTimeout,
        AllowAutoRedirect = false,
        UseCookies = false,
        ActivityHeadersPropagator = null,
    };

    /// <summary>
    /// POST 一次,回傳是否 2xx。不重試;任何失敗(DNS、拒絕連線、逾時、TLS、非 2xx)一律回 false,不丟例外。
    /// HttpClient 預設就不送 User-Agent;Content-Type 刻意是不帶 charset 的 <c>application/json</c>(本文是 UTF-8、無 BOM)。
    /// 回應只看狀態碼,本文不讀。
    /// </summary>
    internal static async Task<bool> SendAsync(Uri endpoint, string json)
    {
        if (EndpointFilterForTests is { } allow && !allow(endpoint)) return false;
        try
        {
            using var http = new HttpClient(CreateHandler()) { Timeout = TotalTimeout };
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>不送的原因(協定 §3 的順序)。</summary>
internal enum StatsOff { DoNotTrack, SharedVariable, ToolVariable, Config, DevelopmentBuild, NotOptedIn }

/// <summary>送不送(<see cref="Off"/> 是 null = 送)與送到哪。</summary>
internal sealed record StatsDecision(Uri Endpoint, StatsOff? Off)
{
    public bool Send => Off is null;
}

/// <summary>協定 §7.1 的外層(schema 與 tool 是常數,在 ToJson 裡寫死)。</summary>
internal sealed record StatsPayload(string ToolVersion, string Os, bool Ci, string? ProjectId, ParityStats Data);

/// <summary>協定 §7.2 的 data。</summary>
internal sealed record ParityStats(
    int Score,
    bool GateFailed,
    int Pages,
    string DesignSource,
    int DesignNodes,
    int Matched,
    SeverityTotals Severity,
    IReadOnlyList<PropStats> Props);

internal sealed record SeverityTotals(int Critical, int Serious, int Medium, int Minor);

/// <summary>一個屬性的落差:條數(含 missing)、其中 missing 的條數、有 delta 的依 abs(delta) 分桶。</summary>
internal sealed record PropStats(string Prop, int Count, int Missing, IReadOnlyList<int> Buckets);

/// <summary>
/// 統計要讀的外界狀態集中成一個值:CLI 用 <see cref="Current"/>,測試自己組一個(不必改行程的環境變數)。
/// </summary>
internal sealed record StatsEnvironment(
    Func<string, string?> Variable, string ToolVersion, string Os, string? NoticeDirectory, TextWriter Err)
{
    /// <summary>測試用:標記檔改放這裡。行程內跑 CLI 的測試不能寫進使用者真正的設定目錄。</summary>
    internal static string? NoticeDirectoryForTests { get; set; }

    public static StatsEnvironment Current() => new(
        Environment.GetEnvironmentVariable,
        VersionCommand.ToolVersion(),
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other",
        NoticeDirectoryForTests ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Console.Error);
}
