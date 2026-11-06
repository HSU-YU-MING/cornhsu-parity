using System.Text.Json.Serialization;

namespace Parity.Engine.DesignSources.Snapshot;

/// <summary>
/// parity.snapshot.json 的頂層信封(1.0 審查面 6,對齊 report.json 於 0.10.0 的同款決定):
/// 裸的 DesignNode 樹無從辨識格式版本,未來格式演進時讀取端無據可判。
/// 相容規則:**沒有版本欄的舊檔(裸 DesignNode)視為 v1 照吃**——既有 snapshot 與
/// 手寫 design JSON 都不必動;讀到比自己新的版本給明確錯誤,不靜默失敗。
/// </summary>
public sealed record SnapshotDocument(int SchemaVersion, DesignNode Root)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 拍照時實測穩定的 id 白名單(F2 殘留的根治,ROADMAP「連拍實測」設計)。
    /// 只在偵測到「每次載入重新生成的 id」時才存:check 擷取現場頁面時只拿名單內的 id
    /// 當 selector 錨點,現場新長出的隨機 id(不在名單上)自然走結構路徑——快照與現場
    /// 兩邊的 selector 生成規則才對得上。null(含舊檔無此欄)= 不限制,現行行為;
    /// 選填欄位,同 schemaVersion 1(讀不懂它的舊版忽略此欄,該站在舊版本來就配不上)。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StableIdAnchors { get; init; }

    /// <summary>
    /// true = 這份快照的樣式欄位只記非預設值,沒寫的就是預設值(透明度 1、沒有邊框、沒有陰影……,
    /// 見 <see cref="SnapshotBuilder.ExpandStyleDefaults"/>)。全部都寫的話快照會大 34～44%(非作者網站實測),而快照是要 commit 的。
    /// 沒有這個標記的舊快照,沒寫的樣式是「不知道」,不比。選填欄位,同 schemaVersion 1。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StyleDefaultsOmitted { get; init; }

    public static SnapshotDocument Of(DesignNode root, IReadOnlyList<string>? stableIdAnchors = null, bool styleDefaultsOmitted = false)
        => new(CurrentSchemaVersion, root)
        {
            StableIdAnchors = stableIdAnchors,
            StyleDefaultsOmitted = styleDefaultsOmitted ? true : null,
        };
}
