namespace Parity.Server.Data;

/// <summary>
/// 登入失敗的計數與鎖定(決策 #21;2.0.0 審查後改寫)。以正規化 email 為鍵,
/// **存在與不存在的 email 一律記在這張表**:兩條路讀寫的次數與種類完全相同(查使用者、查這一列、
/// 寫這一列),回應內容與時間都不透露帳號存不存在。
/// 真帳號不再用 Identity 的 AccessFailedCount/LockoutEnd——那條路在並發時會因 ConcurrencyStamp 衝突
/// 少算(審查實證:同時 40 次錯誤密碼只算到 2),而且寫的是另一張表、成本不同。
/// 語意照 Identity:第 MaxFailedAccessAttempts 次失敗即鎖 DefaultLockoutTimeSpan、鎖上時計數歸零、
/// 鎖定中的嘗試不累計、成功登入刪掉這一列。沒有容量上限也沒有淘汰(成長速度由登入額度限住)。
/// </summary>
public class LoginThrottle
{
    /// <summary>UserManager.NormalizeEmail 的結果(與 AspNetUsers.NormalizedEmail 同一套正規化)。</summary>
    public required string NormalizedEmail { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
