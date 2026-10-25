using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    [Fact]
    public async Task The_web_host_opens_the_database_the_admin_commands_wrote_to()
    {
        // 只設 PARITY_SERVER_DB:原本 create-project 寫進那個檔,web host 卻只看 ConnectionStrings:parity
        // (沒設就是 ContentRoot 下的 parity-server.db)——push 回 401、邀請連結回 404
        using var dir = new TempDir();
        var db = dir.File("server.db");
        using var env = new EnvScope().Set("PARITY_SERVER_DB", db).Set("ConnectionStrings__parity", null);
        var token = LineAfter((await Cli.RunServerAdminAsync("create-project", "demo")).Out, "CI token (shown once");

        await using var factory = new WebApplicationFactory<ParityServerEntryPoint>(); // 刻意不設 ConnectionStrings
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ServerDbContext>();

        Assert.Equal(Path.GetFullPath(db), new SqliteConnectionStringBuilder(ctx.Database.GetConnectionString()).DataSource);
        Assert.Equal(ProjectToken.Hash(token), (await ctx.Projects.SingleAsync()).TokenHash);
    }

    [Fact]
    public async Task Create_invite_prints_the_configured_public_url()
    {
        // M5:CLAUDE.md 原本的地雷「邀請連結硬寫 http://127.0.0.1:4322」——部署後發出去的連結是壞的
        using var dir = new TempDir();
        using var env = new EnvScope().Set("PARITY_SERVER_DB", dir.File("server.db"))
            .Set("Parity__PublicUrl", "https://parity.example.com/");
        Assert.Equal(0, (await Cli.RunServerAdminAsync("create-project", "demo")).ExitCode);

        var r = await Cli.RunServerAdminAsync("create-invite", "demo", "pm@t.local");

        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("https://parity.example.com/invite/", LineAfter(r.Out, "hand this link to them"));
        Assert.DoesNotContain("local development address", r.Out);
    }

    [Fact]
    public async Task Without_a_public_url_the_invite_says_it_is_the_local_address()
    {
        using var dir = new TempDir();
        using var env = new EnvScope().Set("PARITY_SERVER_DB", dir.File("server.db")).Set("Parity__PublicUrl", null);
        Assert.Equal(0, (await Cli.RunServerAdminAsync("create-project", "demo")).ExitCode);

        var r = await Cli.RunServerAdminAsync("create-invite", "demo", "pm@t.local");

        Assert.StartsWith("http://127.0.0.1:4322/invite/", LineAfter(r.Out, "hand this link to them"));
        Assert.Contains("set Parity__PublicUrl", r.Out);
    }

    [Theory]
    [InlineData("parity.example.com")]          // 少了 scheme
    [InlineData("ftp://parity.example.com")]
    [InlineData("https://parity.example.com/?x=1")]
    public async Task A_bad_public_url_is_exit_2_and_creates_no_invite(string url)
    {
        using var dir = new TempDir();
        var db = dir.File("server.db");
        using var env = new EnvScope().Set("PARITY_SERVER_DB", db).Set("Parity__PublicUrl", url);
        Assert.Equal(0, (await Cli.RunServerAdminAsync("create-project", "demo")).ExitCode);

        var r = await Cli.RunServerAdminAsync("create-invite", "demo", "pm@t.local");

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("Parity:PublicUrl must be an absolute http(s) URL", r.Err);
        await using var ctx = Open(db);
        Assert.Equal(0, await ctx.Invites.CountAsync());
    }

    [Fact]
    public async Task Backup_writes_a_consistent_copy_that_opens_with_the_same_data()
    {
        using var dir = new TempDir();
        var db = dir.File("server.db");
        using var env = new EnvScope().Set("PARITY_SERVER_DB", db);
        var token = LineAfter((await Cli.RunServerAdminAsync("create-project", "demo")).Out, "CI token (shown once");
        var target = dir.File("backups", "parity-2026-10-26.db");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var r = await Cli.RunServerAdminAsync("backup", target);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"backup written: {target}", r.Out);
        await using var copy = Open(target);
        Assert.Equal(ProjectToken.Hash(token), (await copy.Projects.SingleAsync()).TokenHash);
        Assert.Empty(await copy.Database.GetPendingMigrationsAsync()); // 連 migration 歷史一起備到(還原後直接能起)
    }

    [Fact]
    public async Task Backup_never_overwrites_and_never_creates_a_missing_database()
    {
        using var dir = new TempDir();
        var db = dir.File("server.db");
        using (new EnvScope().Set("PARITY_SERVER_DB", dir.File("nope.db")))
        {
            var missing = await Cli.RunServerAdminAsync("backup", dir.File("out.db"));
            Assert.Equal(2, missing.ExitCode);
            Assert.Contains("database not found", missing.Err);
            Assert.False(File.Exists(dir.File("nope.db"))); // 沒有順手建一份空的
        }

        using var env = new EnvScope().Set("PARITY_SERVER_DB", db);
        Assert.Equal(0, (await Cli.RunServerAdminAsync("create-project", "demo")).ExitCode);
        var existing = dir.Write("taken.db", "keep me");
        var r = await Cli.RunServerAdminAsync("backup", existing);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("already exists", r.Err);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
        Assert.Equal(2, (await Cli.RunServerAdminAsync("backup")).ExitCode); // 沒給檔名 = usage
    }

    [Fact]
    public async Task Admin_commands_honour_the_connection_string_the_web_host_uses()
    {
        // 反方向:部署時用 ConnectionStrings:parity(環境變數 ConnectionStrings__parity 或 appsettings.json)
        // 指定資料庫,管理指令原本完全不看它,建出來的專案落在 cwd 的 parity-server.db
        using var dir = new TempDir();
        var db = dir.File("from-config.db");
        using var env = new EnvScope().Set("PARITY_SERVER_DB", null).Set("ConnectionStrings__parity", $"Data Source={db}");

        var r = await Cli.RunServerAdminAsync("create-project", "demo");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"(db: {db})", r.Out);
        await using var ctx = Open(db);
        Assert.Equal("demo", (await ctx.Projects.SingleAsync()).Name);
    }
}
