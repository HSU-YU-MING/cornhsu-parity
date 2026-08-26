using System.Text.Json;
using System.Text.Json.Serialization;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>
/// 報告 JSON 的統一序列化設定——check 落地的 report.json、serve API、report 指令的回讀
/// 都用同一組(camelCase + 字串 enum),避免兩處定義漂移導致讀不回來。
/// 設定本體已上移至引擎的 ReportWire(Parity.Server 也要同一組),這裡保留同名欄位
/// 委派過去,既有呼叫點不動。
/// </summary>
public static class ReportJson
{
    /// <summary>serve API 用(緊湊)。</summary>
    public static readonly JsonSerializerOptions Compact = ReportWire.Compact;

    /// <summary>report.json 落地用(縮排,方便人看與 diff);回讀也用這組。</summary>
    public static readonly JsonSerializerOptions Indented = ReportWire.Indented;

    /// <summary>
    /// snapshot 樹的序列化——自 1.0 審查面 6 起包 schemaVersion 信封
    /// (無版本欄的舊檔讀取端視為 v1 照吃,見 JsonDesignSource)。
    /// 超深時給人話:System.Text.Json 超過 MaxDepth 丟的是
    /// 「A possible object cycle was detected」——樹沒有環,只是深,照原文丟出去
    /// 只會讓人往錯的方向查(野生實查 F1 的殘留備忘)。
    /// </summary>
    public static string SerializeSnapshotTree(
        Parity.Engine.DesignSources.DesignNode root,
        IReadOnlyList<string>? stableIdAnchors = null)
    {
        try
        {
            return JsonSerializer.Serialize(
                Parity.Engine.DesignSources.Snapshot.SnapshotDocument.Of(root, stableIdAnchors), Indented);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException && ex.Message.Contains("object cycle"))
        {
            throw new InvalidOperationException(
                "the captured DOM nests deeper than the supported limit (JSON depth 512 ≈ 250 DOM levels) — " +
                "cannot write the snapshot. Add the deepest region to \"ignore\" in parity.config.json to prune it.", ex);
        }
    }
}

// ReportDocument(report.json 頂層信封)已於網頁外殼動工時移入 Parity.Engine(Report.cs)
// ——報告契約的每個消費者(CLI / serve / Parity.Server)對同一個型別。
