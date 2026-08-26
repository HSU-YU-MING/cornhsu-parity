using Microsoft.EntityFrameworkCore;

namespace Parity.Server.Data;

/// <summary>
/// 稽核紀錄(M4.6):誰在什麼時候動了專案的什麼——邀請、成員異動、token 換發、刪 run。
/// 規畫書「這格證明了什麼」自己列了稽核歷史,這裡兌現。
/// ActorEmail 是當下的快照(denormalized):帳號日後被移除,紀錄仍講得出「當時是誰」。
/// 只增不改不刪——稽核表被改寫就不叫稽核了(v1 不做保存期限,量極小)。
/// </summary>
public class AuditEntry
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string ActorEmail { get; set; }
    /// <summary>動作字彙(開放集合,消費端容忍未知值):invite-created / invite-accepted /
    /// member-removed / token-rotated / run-deleted。</summary>
    public required string Action { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
}

public static class Audit
{
    /// <summary>記一筆(不 Save——跟著呼叫端的交易一起落,動作和它的紀錄要嘛都成要嘛都不成)。</summary>
    public static void Log(ServerDbContext db, Guid projectId, Guid? actorUserId, string actorEmail,
        string action, string? detail = null)
        => db.Set<AuditEntry>().Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ActorUserId = actorUserId,
            ActorEmail = actorEmail,
            Action = action,
            Detail = detail,
            At = DateTimeOffset.UtcNow,
        });

    /// <summary>某專案的近期活動(新 → 舊)。呼叫端先驗 Owner。</summary>
    public static Task<List<AuditEntry>> RecentAsync(
        ServerDbContext db, Guid projectId, int limit = 50, CancellationToken ct = default)
        => db.Set<AuditEntry>().Where(a => a.ProjectId == projectId)
            .OrderByDescending(a => a.At).Take(limit).ToListAsync(ct);
}
