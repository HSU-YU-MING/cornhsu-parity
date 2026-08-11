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

    public static SnapshotDocument Of(DesignNode root) => new(CurrentSchemaVersion, root);
}
