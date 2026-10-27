using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parity.Server.Data;

/// <summary>
/// XamlContrast 的 `--json` 報告 → Run 實體圖(合併伺服器:一台收兩種工具)。與 RunIngest 同形:純函式、
/// 丟 InvalidOperationException = 400、丟 UnsupportedSchemaException = 422。
///
/// 契約是 XamlContrast 1.0 凍結的 schemaVersion 6(那個 repo 的 docs/report.schema.json):每個欄位一律寫出、
/// 1.x 只增不改、新增欄位不遞增 schemaVersion → 這裡只讀用得到的欄位、其餘忽略。**不引用 XamlContrast 的組件**
/// ——它沒有發佈成函式庫(XamlContrast 的 CLAUDE.md:沒有消費者就不提前凍結 Core 的 API),伺服器的專案參考圖
/// 也維持只到 Parity.Report。所以這裡是照 schema 手寫的最小鏡像,欄位改名要兩邊一起看。
/// </summary>
public static class ContrastIngest
{
    /// <summary>伺服器認得的 XamlContrast 報告版本(1.x 全系列)。</summary>
    public const int SupportedSchemaVersion = 6;

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        // 報告本身就是 camelCase(Web 預設已含);Web 預設還允許「數字寫成字串」,這裡關掉——
        // XamlContrast 寫出的計數一律是數字,"3" 出現在計數欄就是壞報告
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static Run Parse(string rawReportJson, RunMetadata meta, DateTimeOffset receivedAt)
    {
        var version = ReadSchemaVersion(rawReportJson);
        if (version > SupportedSchemaVersion)
            throw new UnsupportedSchemaException(
                $"XamlContrast report schemaVersion {version} is newer than this server supports ({SupportedSchemaVersion}) — " +
                "the report was produced by a newer XamlContrast. Upgrade the server, or pin the tool version in CI.");
        if (version < SupportedSchemaVersion)
            throw new UnsupportedSchemaException(
                $"XamlContrast report schemaVersion {version} is from a 0.x release — this server accepts the 1.x report " +
                $"(schemaVersion {SupportedSchemaVersion}). Upgrade Cornhsu.XamlContrast to 1.x and audit again.");

        ContrastReport? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ContrastReport>(rawReportJson, Wire);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"body is not a valid XamlContrast report: {ex.Message}");
        }
        if (doc is null) throw NotAReport(rawReportJson);

        var s = doc.Summary;
        var run = new Run
        {
            Id = Guid.NewGuid(),
            Tool = RunTool.XamlContrast,
            CommitSha = meta.CommitSha,
            Branch = meta.Branch,
            TriggeredBy = meta.TriggeredBy,
            RepoUrl = meta.RepoUrl,
            CreatedAt = receivedAt,
            Score = null, // XamlContrast 沒有分數(見 Run.Score)
            // gate 優先序與 parity 相同:push 端帶來的真實結果 → 報告上 CLI 自己的判定 → 伺服器預設口徑
            GateFailed = meta.GateFailed ?? doc.ExitCode switch
            {
                0 => false,
                1 or 3 => true, // 3 = 結果不可信,CI 上也是紅的
                _ => s.FailOn == "fail" ? s.Counts.Fail > 0 : s.Counts.Fail + s.Counts.Warn > 0,
            },
            RawReportGzip = ReportBlob.Compress(rawReportJson),
        };
        run.Contrast = new ContrastResult
        {
            RunId = run.Id,
            ToolVersion = doc.ToolVersion,
            ExitCode = doc.ExitCode,
            Files = s.Files,
            Pairs = s.Pairs,
            Coverage = s.Coverage,
            Unresolved = s.Unresolved,
            Fail = s.Counts.Fail,
            Warn = s.Counts.Warn,
            Ok = s.Counts.Ok,
            Decorative = s.Counts.Decorative,
            Suppressed = s.Suppressed,
            ParseErrors = s.ParseErrors,
            PaletteMode = s.PaletteMode,
            PaletteDegraded = s.PaletteDegraded,
            FailOn = s.FailOn,
        };
        return run;
    }

    /// <summary>存著的原文 → 逐筆(「跟上次比」用,<see cref="ContrastCompare"/>)。原文收件時已照同一個鏡像驗過形狀。</summary>
    internal static List<ContrastFinding> Findings(byte[] rawReportGzip)
        => JsonSerializer.Deserialize<ContrastReport>(ReportBlob.Decompress(rawReportGzip), Wire)!.Findings;

    /// <summary>
    /// 有 summary 物件與 findings 陣列、沒有 parity 的 reports 陣列——只用來在錯誤訊息裡指路
    /// (沒帶 X-Parity-Tool 就把 XamlContrast 報告送上來),不拿來決定怎麼解析。
    /// </summary>
    public static bool LooksLikeContrastReport(string raw)
        => TryRoot(raw) is { } root
            && root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array
            && !root.TryGetProperty("reports", out _);

    /// <summary>
    /// 先看形狀再看版本:parity 的 report.json 也有 schemaVersion(1),只看版本會被說成「XamlContrast 0.x 的舊報告」。
    /// </summary>
    private static int ReadSchemaVersion(string raw)
    {
        if (TryRoot(raw) is not { } root || !root.TryGetProperty("schemaVersion", out var v)
            || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version)
            || IsParityEnvelope(root))
            throw NotAReport(raw);
        return version;
    }

    private static bool IsParityEnvelope(JsonElement root)
        => root.TryGetProperty("reports", out var reports) && reports.ValueKind == JsonValueKind.Array
            && !root.TryGetProperty("findings", out _);

    private static JsonElement? TryRoot(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static InvalidOperationException NotAReport(string raw)
    {
        var parity = TryRoot(raw) is { } root && IsParityEnvelope(root);
        return new InvalidOperationException(
            "body is not an XamlContrast report (expected the { schemaVersion, summary, findings } object that " +
            "`xamlcontrast --json` writes)." +
            (parity ? " It looks like a Parity report — push it with `parity push`, without X-Parity-Tool: xamlcontrast." : ""));
    }

    // ── schemaVersion 6 的最小鏡像:只列伺服器讀的欄位;required = 缺了就是壞報告(1.0 起每個欄位一律寫出)──

    internal sealed class ContrastReport
    {
        public required int SchemaVersion { get; init; }
        public required string? ToolVersion { get; init; }
        public required int? ExitCode { get; init; }
        public required ContrastSummary Summary { get; init; }
        public required List<JsonElement> UnresolvedSites { get; init; }
        public required List<ContrastFinding> Findings { get; init; }
    }

    internal sealed class ContrastSummary
    {
        public required string PaletteMode { get; init; }
        public required bool PaletteDegraded { get; init; }
        public required string FailOn { get; init; }
        public required int Files { get; init; }
        public required int Pairs { get; init; }
        public required double Coverage { get; init; }
        public required int Unresolved { get; init; }
        public required int Suppressed { get; init; }
        public required int ParseErrors { get; init; }
        public required ContrastCounts Counts { get; init; }
    }

    internal sealed class ContrastCounts
    {
        public required int Fail { get; init; }
        public required int Warn { get; init; }
        public required int Ok { get; init; }
        public required int Decorative { get; init; }
    }

    /// <summary>逐筆只驗形狀(詳情頁從原文讀),不落庫;「跟上次比」從原文重讀。</summary>
    internal sealed class ContrastFinding
    {
        public required string File { get; init; }
        public required int Line { get; init; }
        public required string Element { get; init; }
        public required string Fg { get; init; }
        public required string Bg { get; init; }
        public required double? RatioDark { get; init; }
        public required double? RatioLight { get; init; }
        public required string Category { get; init; }
    }
}
