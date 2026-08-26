using Microsoft.EntityFrameworkCore;

namespace Parity.Server.Data;

/// <summary>
/// 邀請的生命週期(建立 → 驗證 → 接受)。純資料層:使用者的建立(UserManager)在端點做,
/// 這裡負責 token 與狀態——才能用 in-memory SQLite 單元測試整條路。
/// </summary>
public static class Invites
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>建立邀請,回 (實體, token 明文)。token 只在這裡出現一次,庫裡只有 hash。</summary>
    public static (Invite Invite, string Token) Create(
        Guid projectId, string email, string role, DateTimeOffset now)
    {
        if (!ProjectRole.IsValid(role))
            throw new ArgumentException($"unknown role: {role} (use owner/member/viewer)", nameof(role));
        var token = ProjectToken.Generate();
        return (new Invite
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Email = email.Trim(),
            Role = role,
            TokenHash = ProjectToken.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        }, token);
    }

    /// <summary>依 token 找「還能用」的邀請:存在、未過期、未被接受。查無 = null,不解釋差在哪。</summary>
    public static async Task<Invite?> FindUsableAsync(
        ServerDbContext db, string token, DateTimeOffset now, CancellationToken ct = default)
    {
        if (token.Length is < 20 or > 200) return null;
        var hash = ProjectToken.Hash(token);
        var invite = await db.Invites.Include(i => i.Project)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        return invite is null || invite.AcceptedAt is not null || invite.ExpiresAt < now ? null : invite;
    }

    /// <summary>把接受落庫:標記單次使用 + 建立成員資格(重複邀請同人同專案 → 升級/覆寫角色)。</summary>
    public static async Task AcceptAsync(
        ServerDbContext db, Invite invite, Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        invite.AcceptedAt = now;
        var existing = await db.Members.FirstOrDefaultAsync(
            m => m.ProjectId == invite.ProjectId && m.UserId == userId, ct);
        if (existing is null)
            db.Members.Add(new ProjectMember
            {
                Id = Guid.NewGuid(),
                ProjectId = invite.ProjectId,
                UserId = userId,
                Role = invite.Role,
                CreatedAt = now,
            });
        else
            existing.Role = invite.Role;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>成員資格的授權查詢——所有讀取端點的過濾器。</summary>
public static class Membership
{
    /// <summary>這個使用者看得到的專案 id(任何角色)。</summary>
    public static IQueryable<Guid> ProjectIdsFor(ServerDbContext db, Guid userId)
        => db.Members.Where(m => m.UserId == userId).Select(m => m.ProjectId);

    /// <summary>是否為該專案的 Owner(管理端點的門檻)。</summary>
    public static Task<bool> IsOwnerAsync(
        ServerDbContext db, Guid userId, Guid projectId, CancellationToken ct = default)
        => db.Members.AnyAsync(
            m => m.UserId == userId && m.ProjectId == projectId && m.Role == ProjectRole.Owner, ct);

    /// <summary>移除成員的防呆:不能移走最後一個 Owner(專案會變成沒人管得了)。</summary>
    public static async Task<bool> WouldRemoveLastOwnerAsync(
        ServerDbContext db, Guid projectId, Guid userId, CancellationToken ct = default)
    {
        var isOwner = await db.Members.AnyAsync(
            m => m.ProjectId == projectId && m.UserId == userId && m.Role == ProjectRole.Owner, ct);
        if (!isOwner) return false;
        var ownerCount = await db.Members.CountAsync(
            m => m.ProjectId == projectId && m.Role == ProjectRole.Owner, ct);
        return ownerCount <= 1;
    }
}
