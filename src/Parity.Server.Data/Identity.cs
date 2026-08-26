using Microsoft.AspNetCore.Identity;

namespace Parity.Server.Data;

/// <summary>
/// 人的帳號(M3)。認證方案的裁決(2026-08-26,理由詳規畫書待決區):
/// email+密碼(ASP.NET Core Identity)——magic link 需要寄信基礎設施,v1 沒有;
/// 邀請制之下邀請連結就是身分驗證,不做 email confirm;忘記密碼 = Owner 重邀。
/// 只用 Identity 的使用者半邊(IdentityUserContext),不用它的 Role 系統——
/// 我們的角色是「每個專案一個」的成員資格,不是全域角色。
/// </summary>
public class AppUser : IdentityUser<Guid>;

/// <summary>專案角色。字串存庫(同 severity 的教訓:enum 進庫 = 未來加值要 migration)。</summary>
public static class ProjectRole
{
    public const string Owner = "owner";   // 專案設定、發 token、邀請成員
    public const string Member = "member"; // 看全部、推報告(推走 CI token,UI 上與 viewer 同為唯讀)
    public const string Viewer = "viewer"; // 只讀(PM、設計師的預設)

    public static bool IsValid(string role) => role is Owner or Member or Viewer;
}

/// <summary>成員資格:User × Project × Role。Project 即租戶(組織層已裁,見規畫書)。</summary>
public class ProjectMember
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 邀請:Owner 產生連結、親手交給對方(LINE/Slack/口頭都行)——v1 不寄信。
/// token 只存 SHA-256(同專案 CI token 的做法);7 天過期、接受即失效(單次)。
/// </summary>
public class Invite
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public required string Email { get; set; }
    public required string Role { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}
