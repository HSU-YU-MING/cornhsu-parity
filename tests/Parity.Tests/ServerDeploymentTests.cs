using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// 對外部署(網頁外殼 M5)要的設定:對外網址、Data Protection 金鑰的位置,與合併伺服器的 migration。
/// </summary>
public class ServerDeploymentTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private Guid _projectId;
    private string _ownerInviteToken = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"parity-inttest-{Guid.NewGuid():N}.db");
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var project = new Project { Id = Guid.NewGuid(), Name = "demo", TokenHash = ProjectToken.Hash(ProjectToken.Generate()), CreatedAt = DateTimeOffset.UtcNow };
        _projectId = project.Id;
        db.Projects.Add(project);
        var (invite, token) = Invites.Create(project.Id, "owner@t.local", ProjectRole.Owner, DateTimeOffset.UtcNow);
        _ownerInviteToken = token;
        db.Invites.Add(invite);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { /* 殘檔在 %TEMP%,無害 */ }
        return Task.CompletedTask;
    }

    private WebApplicationFactory<ParityServerEntryPoint> Factory(params (string Key, string Value)[] settings)
        => new WebApplicationFactory<ParityServerEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:parity", $"Data Source={_dbPath}");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });

    private async Task<HttpClient> OwnerAsync(WebApplicationFactory<ParityServerEntryPoint> factory)
    {
        var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/invites/{_ownerInviteToken}/accept",
            new StringContent("""{ "password": "correct horse battery" }""", Encoding.UTF8, "application/json"))).StatusCode);
        return owner;
    }

    private async Task<string> InviteLinkAsync(HttpClient owner)
    {
        var resp = await owner.PostAsync($"/api/projects/{_projectId}/invites",
            new StringContent("""{ "email": "pm@t.local", "role": "viewer" }""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString()!;
    }

    [Fact]
    public async Task Invite_links_use_the_public_url_not_the_host_the_server_sees()
    {
        // 反向代理後面,伺服器看到的 Host 可能是內部位址(parity:8080);對外網址有設就用它
        await using var factory = Factory(("Parity:PublicUrl", "https://parity.example.com/"));

        var link = await InviteLinkAsync(await OwnerAsync(factory));

        Assert.StartsWith("https://parity.example.com/invite/", link);
    }

    [Fact]
    public async Task Without_a_public_url_invite_links_follow_the_request()
    {
        await using var factory = Factory();

        var link = await InviteLinkAsync(await OwnerAsync(factory));

        Assert.StartsWith("http://localhost/invite/", link); // TestServer 的預設 Host
    }

    [Theory]
    [InlineData("parity.example.com")]
    [InlineData("javascript:alert(1)")]
    public void A_bad_public_url_stops_the_server_at_startup_and_names_the_setting(string url)
    {
        using var factory = Factory(("Parity:PublicUrl", url));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Parity:PublicUrl", Flatten(ex));
    }

    [Fact]
    public async Task Sign_in_keys_are_written_to_the_configured_directory_and_survive_a_restart()
    {
        // 容器重新部署 = 新的家目錄 = 新的金鑰 = 所有人被登出。Parity:KeysDirectory 指到持久磁碟就不會
        var keys = Path.Combine(Path.GetTempPath(), $"parity-keys-{Guid.NewGuid():N}");
        try
        {
            string cookie;
            await using (var first = Factory(("Parity:KeysDirectory", keys)))
            {
                var client = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
                var accept = await client.PostAsync($"/api/invites/{_ownerInviteToken}/accept",
                    new StringContent("""{ "password": "correct horse battery" }""", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
                cookie = accept.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("parity.session=", StringComparison.Ordinal)).Split(';')[0];
            }
            Assert.NotEmpty(Directory.GetFiles(keys, "*.xml"));

            await using var second = Factory(("Parity:KeysDirectory", keys)); // 「重新部署」:新的 host、同一個金鑰目錄
            var again = second.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            req.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await again.SendAsync(req)).StatusCode);
        }
        finally
        {
            try { Directory.Delete(keys, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Runs_stored_before_the_merged_server_become_parity_runs()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(conn).Options);
        // 停在合併伺服器之前的最後一版,用那時的形狀塞一筆 run
        await db.GetService<IMigrator>().MigrateAsync("20261023040326_CarryIdentityLockoutIntoLoginThrottles");
        // Guid 照 EF 的 SQLite 慣例寫成大寫文字
        var projectId = Guid.NewGuid().ToString().ToUpperInvariant();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Projects (Id, Name, TokenHash, CreatedAt) VALUES ({0}, 'old', 'hash', 0)", projectId);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Runs (Id, ProjectId, CreatedAt, Score, GateFailed, RawReportGzip) VALUES ({0}, {1}, 0, 87, 0, x'00')",
            Guid.NewGuid().ToString().ToUpperInvariant(), projectId);

        await db.Database.MigrateAsync();

        var run = await db.Runs.AsNoTracking().SingleAsync();
        Assert.Equal((RunTool.Parity, (int?)87), (run.Tool, run.Score));
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        void Walk(Exception? e)
        {
            for (; e is not null; e = e.InnerException)
            {
                messages.Add(e.Message);
                if (e is AggregateException agg) foreach (var inner in agg.InnerExceptions) Walk(inner);
            }
        }
        Walk(ex);
        return string.Join(" | ", messages);
    }
}
