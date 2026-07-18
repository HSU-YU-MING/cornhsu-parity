using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parity.Cli;

/// <summary>
/// 報告 JSON 的統一序列化設定——check 落地的 report.json、serve API、report 指令的回讀
/// 都用同一組(camelCase + 字串 enum),避免兩處定義漂移導致讀不回來。
/// </summary>
public static class ReportJson
{
    /// <summary>serve API 用(緊湊)。</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>report.json 落地用(縮排,方便人看與 diff);回讀也用這組。</summary>
    public static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };
}
