using Microsoft.EntityFrameworkCore;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// Parity.Server 的管理指令(create-project / create-invite):自架者 bootstrap 的唯一入口——
/// 第一個 CI token 與第一個 Owner 都從這裡來。token 與邀請連結只印一次,
/// 印出來的東西必須真的能用(hash 對得上、邀請可兌換)。資料庫路徑走 PARITY_SERVER_DB。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class ServerAdminCommandTests
{
    private static ServerDbContext Open(string path)
        => new(new DbContextOptionsBuilder<ServerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    /// <summary>「標題行」之後那一行的內容(token / 連結都是縮排兩格單獨一行)。</summary>
    private static string LineAfter(string output, string header)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var i = Array.FindIndex(lines, l => l.Contains(header, StringComparison.Ordinal));
        Assert.True(i >= 0 && i + 1 < lines.Length, $"輸出裡找不到「{header}」:\n{output}");
        return lines[i + 1].Trim();
    }

    [Fact]
    public async Task Create_project_prints_a_token_whose_hash_is_stored()
    {
        using var dir = new TempDir();
        var db = dir.File("server.db");
        using var env = new EnvScope().Set("PARITY_SERVER_DB", db);

        var r = await Cli.RunServerAdminAsync("create-project", "demo");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"project created: demo   (db: {db})", r.Out);
        var token = LineAfter(r.Out, "CI token (shown once");
        await using var ctx = Open(db);
        var project = await ctx.Projects.SingleAsync();
        Assert.Equal("demo", project.Name);
        Assert.Equal(ProjectToken.Hash(token), project.TokenHash); // 存 hash、不存明文
        Assert.DoesNotContain(token, project.TokenHash);
    }

    [Theory]
    [InlineData(null, ProjectRole.Owner)] // 沒給角色 = owner(bootstrap 第一個 Owner 的用法)
    [InlineData("viewer", ProjectRole.Viewer)]
    public async Task Create_invite_prints_a_link_that_can_be_redeemed(string? role, string expectedRole)
    {
        using var dir = new TempDir();
        var db = dir.File("server.db");
        using var env = new EnvScope().Set("PARITY_SERVER_DB", db);
        Assert.Equal(0, (await Cli.RunServerAdminAsync("create-project", "demo")).ExitCode);

        var r = await Cli.RunServerAdminAsync(role is null
            ? ["create-invite", "demo", "pm@t.local"]
            : ["create-invite", "demo", "pm@t.local", role]);

        Assert.Equal(0, r.ExitCode);
        var link = LineAfter(r.Out, "hand this link to them");
        Assert.StartsWith("http://", link);
        var token = link[(link.LastIndexOf("/invite/", StringComparison.Ordinal) + "/invite/".Length)..];
        await using var ctx = Open(db);
        var invite = await Invites.FindUsableAsync(ctx, token, DateTimeOffset.UtcNow);
        Assert.NotNull(invite);
        Assert.Equal("pm@t.local", invite.Email);
        Assert.Equal(expectedRole, invite.Role);
    }

    [Theory]
    [InlineData("create-invite", "nope", "a@t.local", "owner", "no project named \"nope\"")]
    [InlineData("create-invite", "demo", "a@t.local", "admin", "unknown role: admin")]
    [InlineData("create-invite", "demo", null, null, "usage:")]
    [InlineData("create-project", null, null, null, "usage:")]
    public async Task Bad_admin_arguments_are_exit_2_and_change_nothing(
        string command, string? a1, string? a2, string? a3, string message)
    {
        using var dir = new TempDir();
        var db = dir.File("server.db");
        using var env = new EnvScope().Set("PARITY_SERVER_DB", db);
        Assert.Equal(0, (await Cli.RunServerAdminAsync("create-project", "demo")).ExitCode);

        var r = await Cli.RunServerAdminAsync([command, .. new[] { a1, a2, a3 }.OfType<string>()]);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains(message, r.Err);
        await using var ctx = Open(db);
        Assert.Equal(1, await ctx.Projects.CountAsync());
        Assert.Equal(0, await ctx.Invites.CountAsync());
    }
}
