using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Parity.Engine;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// 端點層整合測試(M4.6 必補 #1):M3 當時用 curl 手跑的認證劇本,搬進 CI 住下——
/// cookie 設定、RequireAuthorization 接線、Owner 檢查在哪個端點上,從此有防回歸的護欄。
/// 真實 HTTP 管線(WebApplicationFactory),SQLite 落在暫存檔,測完即刪。
/// </summary>
public class ServerEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<ParityServerEntryPoint> _factory = null!;
    private string _dbPath = null!;
    private string _ciToken = null!;
    private string _ownerInviteToken = null!;
    private Guid _projectId;

    private static string DemoReport()
    {
        var report = new FidelityReport("/", "http://x/", "snap",
            [new NodeResult("cta", "id1", "#cta", "auto-name", Severity.Serious,
                [new PropDiff("paddingTop", "12", "8", "px", 4, 2, Severity.Serious)])],
            [],
            new ReportSummary(1, 1, 0, 1, 0, 1, 0, 0, Severity.Serious));
        return JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact);
    }

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"parity-inttest-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<ParityServerEntryPoint>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:parity", $"Data Source={_dbPath}"));

        // 種子:專案 + CI token + Owner 邀請(照 create-project / create-invite 的路)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        _ciToken = ProjectToken.Generate();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "demo",
            TokenHash = ProjectToken.Hash(_ciToken),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _projectId = project.Id;
        db.Projects.Add(project);
        var (invite, inviteToken) = Invites.Create(project.Id, "owner@t.local", ProjectRole.Owner, DateTimeOffset.UtcNow);
        _ownerInviteToken = inviteToken;
        db.Invites.Add(invite);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // 連線池不清,暫存 db 檔刪不掉
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { /* 殘檔在 %TEMP%,無害 */ }
    }

    private HttpClient Client() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static StringContent Json(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> PushAsync(HttpClient anon, string token,
        string? branch = null, string? gate = null, string? report = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/runs")
        {
            Content = new StringContent(report ?? DemoReport(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (branch is not null) req.Headers.Add("X-Parity-Branch", branch);
        if (gate is not null) req.Headers.Add("X-Parity-Gate", gate);
        return await anon.SendAsync(req);
    }

    [Fact]
    public async Task Auth_scenario_end_to_end()
    {
        var anon = Client();

        // 0) 健康檢查匿名可用
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/healthz")).StatusCode);

        // 1) 未登入讀取 → 401(RequireAuthorization 有接上)
        foreach (var url in new[] { "/api/runs", "/api/overview", "/api/branches", "/api/auth/me" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);

        // 2) 邀請預覽 → 接受(設密碼、cookie 直接生效)
        var preview = await anon.GetAsync($"/api/invites/{_ownerInviteToken}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        var owner = Client();
        var accept = await owner.PostAsync($"/api/invites/{_ownerInviteToken}/accept",
            Json(new { password = "correct horse battery" }));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/auth/me")).StatusCode);

        // 3) 邀請單次使用 → 404
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/invites/{_ownerInviteToken}")).StatusCode);

        // 4) CI push 與登入無關;X-Parity-Gate 蓋過伺服器預設口徑
        Assert.Equal(HttpStatusCode.Created, (await PushAsync(anon, _ciToken, branch: "main")).StatusCode);
        var passPush = await PushAsync(anon, _ciToken, branch: "main", gate: "pass");
        Assert.Equal(HttpStatusCode.Created, passPush.StatusCode);
        var runs = await owner.GetFromJsonAsync<JsonElement>("/api/runs");
        Assert.False(runs[0].GetProperty("gateFailed").GetBoolean()); // 報告有 serious 仍是 pass:push 端說了算
        Assert.True(runs[1].GetProperty("gateFailed").GetBoolean());

        // 5) 錯 token 的 push → 401;壞報告 → 400
        Assert.Equal(HttpStatusCode.Unauthorized, (await PushAsync(anon, "wrong-token-wrong-token")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PushAsync(anon, _ciToken, report: """{ "not": "a report" }""")).StatusCode);

        // 6) Owner 發 viewer 邀請 → viewer 接受
        var inviteResp = await owner.PostAsync($"/api/projects/{_projectId}/invites",
            Json(new { email = "pm@t.local", role = "viewer" }));
        Assert.Equal(HttpStatusCode.OK, inviteResp.StatusCode);
        var link = (await inviteResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString()!;
        var viewerToken = link[(link.LastIndexOf('/') + 1)..];

        var viewer = Client();
        Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsync($"/api/invites/{viewerToken}/accept",
            Json(new { password = "viewer pass phrase" }))).StatusCode);

        // 7) viewer:讀得到資料、摸不到管理端點(403)
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/projects/{_projectId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/projects/{_projectId}/token/rotate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/projects/{_projectId}/audit")).StatusCode);

        // 8) 錯密碼登入 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().PostAsync("/api/auth/login",
            Json(new { email = "owner@t.local", password = "wrong" }))).StatusCode);

        // 9) 唯一 Owner 不可自移(400)
        var members = await owner.GetFromJsonAsync<JsonElement>($"/api/projects/{_projectId}/members");
        var ownerUserId = members.GetProperty("members").EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "owner").GetProperty("userId").GetString();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.DeleteAsync($"/api/projects/{_projectId}/members/{ownerUserId}")).StatusCode);

        // 10) viewer 不能刪 run;Owner 可以;刪掉就真的不見
        var runId = runs[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/runs/{runId}")).StatusCode);

        // 11) token 換發:舊 401、新 201
        var rotate = await owner.PostAsync($"/api/projects/{_projectId}/token/rotate", null);
        var newCi = (await rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await PushAsync(anon, _ciToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PushAsync(anon, newCi)).StatusCode);

        // 12) 稽核紀錄:上面的動作都留了帳(開放字彙)
        var audit = await owner.GetFromJsonAsync<JsonElement>($"/api/projects/{_projectId}/audit");
        var actions = audit.EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        foreach (var expected in new[] { "invite-created", "invite-accepted", "token-rotated", "run-deleted" })
            Assert.Contains(expected, actions);

        // 13) 登出後回到 401
        Assert.Equal(HttpStatusCode.NoContent, (await viewer.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/overview")).StatusCode);
    }
}
