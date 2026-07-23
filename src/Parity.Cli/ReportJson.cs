using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>
/// 報告 JSON 的統一序列化設定——check 落地的 report.json、serve API、report 指令的回讀
/// 都用同一組(camelCase + 字串 enum),避免兩處定義漂移導致讀不回來。
/// </summary>
public static class ReportJson
{
    /// <summary>serve API 用(緊湊)。</summary>
    // 刻意不設 WhenWritingNull:null 欄位(unit/delta 等)顯式輸出,消費端(含未來
    // parity push 的伺服器)不必判斷 key 存不存在——報告是跨版本契約,少一個「有時消失的 key」的坑。
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>report.json 落地用(縮排,方便人看與 diff);回讀也用這組。</summary>
    public static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };
}

/// <summary>
/// report.json 的頂層信封。裸陣列無從辨識版本;包一層 schemaVersion,未來報告格式
/// 演進時消費端(parity report / 未來的 parity push 伺服器)有據可判、能相容處理。
/// </summary>
public sealed record ReportDocument(int SchemaVersion, IReadOnlyList<FidelityReport> Reports)
{
    public const int CurrentSchemaVersion = 1;

    public static ReportDocument Of(IReadOnlyList<FidelityReport> reports) => new(CurrentSchemaVersion, reports);
}
