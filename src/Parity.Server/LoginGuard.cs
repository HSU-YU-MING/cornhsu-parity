using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Parity.Server.Data;

/// <summary>
/// 登入的額度、序列化與鎖定(決策 #21;2.0.0 審查後改寫)。目標:回應的內容與時間都不透露帳號存不存在,
/// 而且並發、灌爆都繞不過鎖定。三層,順序就是 /api/auth/login 的順序:
/// <list type="number">
/// <item>額度:每個用戶端 IP、每個正規化 email 各一個一分鐘的滑動視窗(預設 20 次、10 次;設定
///   <c>Parity:Login:PerIpPerMinute</c>、<c>Parity:Login:PerEmailPerMinute</c>)。超過就 429,
///   在任何查詢與密碼驗證之前擋下,存在與否一視同仁。</item>
/// <item>序列化:同一個正規化 email 的登入一次只跑一個(不同 email 互不影響),
///   「讀計數 → 驗密碼 → 寫計數」不會被並發穿插。只在單一行程內有效(現在的部署是單一行程 + SQLite)。</item>
/// <item>計數與鎖定記在 db 的 <see cref="LoginThrottle"/>:存在與不存在的 email 同一張表、同一套讀寫。</item>
/// </list>
/// 反向代理後面部署時,要先設定 ForwardedHeaders 讓 RemoteIpAddress 是真正的用戶端,
/// 否則所有人共用代理那一個 IP 的額度。
/// </summary>
internal sealed class LoginGuard : IDisposable
{
    public const int DefaultPerIpPerMinute = 20;
    public const int DefaultPerEmailPerMinute = 10;

    private readonly LockoutOptions _lockout;
    private readonly TimeProvider _time;
    private readonly PartitionedRateLimiter<string> _perIp;
    private readonly PartitionedRateLimiter<string> _perEmail;
    private readonly KeyedGate _gate = new();

    public LoginGuard(LockoutOptions lockout, TimeProvider time, int perIpPerMinute, int perEmailPerMinute)
    {
        if (perIpPerMinute <= 0 || perEmailPerMinute <= 0)
            throw new InvalidOperationException(
                $"Parity:Login:PerIpPerMinute and Parity:Login:PerEmailPerMinute must be positive (got {perIpPerMinute} and {perEmailPerMinute}).");
        _lockout = lockout;
        _time = time;
        _perIp = PerMinute(perIpPerMinute);
        _perEmail = PerMinute(perEmailPerMinute);
    }

    private static PartitionedRateLimiter<string> PerMinute(int limit)
        => PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetSlidingWindowLimiter(key,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    /// <summary>先扣這個 IP 的額度,再扣這個 email 的;任一不夠就回 false 與建議的等待時間。</summary>
    public bool TryAcquire(IPAddress? client, string normalizedEmail, out TimeSpan retryAfter)
    {
        foreach (var (limiter, key) in new[] { (_perIp, client?.ToString() ?? "unknown"), (_perEmail, normalizedEmail) })
        {
            using var lease = limiter.AttemptAcquire(key);
            if (lease.IsAcquired) continue;
            retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var hint) ? hint : TimeSpan.FromMinutes(1);
            return false;
        }
        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>同一個正規化 email 一次只放行一個;Dispose 才放下一個。</summary>
    public Task<IDisposable> SerializeAsync(string normalizedEmail, CancellationToken ct) => _gate.EnterAsync(normalizedEmail, ct);

    public bool IsLockedOut(LoginThrottle? row) => row?.LockedUntil is { } until && until > _time.GetUtcNow();

    /// <summary>
    /// 記一次失敗(不 Save,呼叫端存);回傳這一次是否剛好鎖上。鎖定中的嘗試不該走到這裡(呼叫端先擋),
    /// 所以鎖定中不累計;鎖上時計數歸零,到期後從 0 重算——與 Identity 的 AccessFailedAsync 同語意。
    /// </summary>
    public bool RecordFailure(ServerDbContext db, string normalizedEmail, LoginThrottle? row)
    {
        if (row is null)
        {
            row = new LoginThrottle { NormalizedEmail = normalizedEmail };
            db.LoginThrottles.Add(row);
        }
        row.Failures++;
        if (row.Failures < _lockout.MaxFailedAccessAttempts) return false;
        row.Failures = 0;
        row.LockedUntil = _time.GetUtcNow() + _lockout.DefaultLockoutTimeSpan;
        return true;
    }

    public void Dispose()
    {
        _perIp.Dispose();
        _perEmail.Dispose();
    }
}

/// <summary>依 key 互斥的非同步鎖:同一個 key 同時只有一個持有者;沒有人持有或等待的 key 立刻回收(不會越積越多)。</summary>
internal sealed class KeyedGate
{
    private sealed class Slot
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users; // 持有者 + 等待者,只在 _slots 的鎖內改
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    public async Task<IDisposable> EnterAsync(string key, CancellationToken ct)
    {
        Slot slot;
        lock (_slots)
        {
            if (!_slots.TryGetValue(key, out slot!)) _slots[key] = slot = new Slot();
            slot.Users++;
        }
        try
        {
            await slot.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Leave(key, slot, entered: false);
            throw;
        }
        return new Releaser(() => Leave(key, slot, entered: true));
    }

    private void Leave(string key, Slot slot, bool entered)
    {
        if (entered) slot.Semaphore.Release();
        lock (_slots)
        {
            if (--slot.Users == 0) _slots.Remove(key);
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) release();
        }
    }
}

/// <summary>
/// 登入失敗路徑的時序對齊(見 /api/auth/login):帳號不存在、帳號鎖定中,都補一次與真帳號同成本的
/// 密碼驗證,結果丟掉。
/// </summary>
internal static class LoginTiming
{
    private static readonly AppUser DummyUser = new() { Id = Guid.Empty, UserName = "timing@invalid", Email = "timing@invalid" };
    private static readonly ConcurrentDictionary<Type, string> DummyHashes = new();

    public static void SpendOneVerification(IPasswordHasher<AppUser> hasher, string? password)
    {
        // 假雜湊每種 hasher 只算一次:同一個型別 = 同樣的格式與成本設定(用別的型別算的雜湊,
        // 格式對不上會被秒退,成本就不一樣了)
        var hash = DummyHashes.GetOrAdd(hasher.GetType(), _ => hasher.HashPassword(DummyUser, Guid.NewGuid().ToString("N")));
        _ = hasher.VerifyHashedPassword(DummyUser, hash, password ?? "");
    }
}
