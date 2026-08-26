using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parity.Engine;

/// <summary>
/// report.json 的標準序列化設定——契約的一部分(camelCase、字串 enum、null 顯式輸出、
/// MaxDepth 512 對齊擷取端)。原住在 Parity.Cli 的 ReportJson;網頁外殼動工時移入引擎:
/// CLI 落地、serve API、Parity.Server 解析,全部對同一組設定,不各抄一份造成漂移。
/// </summary>
public static class ReportWire
{
    // 刻意不設 WhenWritingNull:null 欄位(unit/delta 等)顯式輸出,消費端不必判斷
    // key 存不存在——報告是跨版本契約,少一個「有時消失的 key」的坑。
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // snapshot 也用這組落地整棵 DesignNode 樹:真實網站 DOM 常見 30+ 層巢狀
        // (每層 Children 佔 2 個 JSON 深度),預設 MaxDepth 64 會炸。
        MaxDepth = 512,
    };

    /// <summary>落地檔用(縮排,方便人看與 diff);回讀也用這組。</summary>
    public static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };
}
