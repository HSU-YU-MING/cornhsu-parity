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

    // ── push 協定(parity push ↔ POST /api/runs):CLI 隨 NuGet 發、Server 自架,版本錯開是常態 ──

    private static string CleanReport()
    {
        var report = new FidelityReport("/ok", "http://x/ok", "snap",
            [new NodeResult("a", "a", "#a", "selector", Severity.None, [])], [],
            new ReportSummary(1, 1, 0, 0, 0, 0, 0, 0, Severity.None));
        return JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact);
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var owner = Client();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/invites/{_ownerInviteToken}/accept",
            Json(new { password = "correct horse battery" }))).StatusCode);
        return owner;
    }

    [Fact]
    public async Task Push_ack_has_six_fields_and_every_metadata_header_reaches_the_run_detail()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/runs")
        {
            Content = new StringContent(DemoReport(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ciToken);
        req.Headers.Add("X-Parity-Commit", "0123456789abcdef0123456789abcdef01234567");
        req.Headers.Add("X-Parity-Branch", "feature/x");
        req.Headers.Add("X-Parity-Triggered-By", "octocat");
        req.Headers.Add("X-Parity-Repo-Url", "https://github.com/owner/repo");
        req.Headers.Add("X-Parity-Gate", "pass");

        var resp = await Client().SendAsync(req);

        // parity push 讀的就是這六個欄位(印 project/score/url、寫 job summary)
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var ack = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["id", "project", "score", "gateFailed", "pages", "url"], ack.EnumerateObject().Select(p => p.Name));
        var id = ack.GetProperty("id").GetString();
        Assert.Equal("demo", ack.GetProperty("project").GetString());
        Assert.Equal(0, ack.GetProperty("score").GetInt32()); // 唯一節點有 serious → 0/1 忠實
        Assert.False(ack.GetProperty("gateFailed").GetBoolean()); // X-Parity-Gate: pass 蓋過報告內容
        Assert.Equal(1, ack.GetProperty("pages").GetInt32());
        Assert.Equal($"/runs/{id}", ack.GetProperty("url").GetString());

        var detail = await (await OwnerAsync()).GetFromJsonAsync<JsonElement>($"/api/runs/{id}");
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", detail.GetProperty("commitSha").GetString());
        Assert.Equal("feature/x", detail.GetProperty("branch").GetString());
        Assert.Equal("octocat", detail.GetProperty("triggeredBy").GetString());
        Assert.Equal("https://github.com/owner/repo", detail.GetProperty("repoUrl").GetString());
        Assert.False(detail.GetProperty("gateFailed").GetBoolean());
    }

    [Fact]
    public async Task Push_without_metadata_headers_stores_nulls()
    {
        var resp = await PushAsync(Client(), _ciToken);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var detail = await (await OwnerAsync()).GetFromJsonAsync<JsonElement>($"/api/runs/{id}");
        foreach (var field in new[] { "commitSha", "branch", "triggeredBy", "repoUrl" })
            Assert.Equal(JsonValueKind.Null, detail.GetProperty(field).ValueKind);
    }

    [Theory]
    [InlineData("FAIL", false)]  // 只認小寫 pass/fail;其他值 = 沒給 → 伺服器預設口徑(critical/serious)
    [InlineData("1", false)]     // 把 exit code 直接塞進標頭不算數(那是 --gate-exit 的事)
    [InlineData("PASS", true)]
    [InlineData("0", true)]
    [InlineData("maybe", true)]
    public async Task Unrecognized_gate_header_values_are_treated_as_absent(string gate, bool reportHasSerious)
    {
        var resp = await PushAsync(Client(), _ciToken, gate: gate, report: reportHasSerious ? DemoReport() : CleanReport());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(reportHasSerious, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gateFailed").GetBoolean());
    }

    [Fact]
    public async Task Newer_schema_version_is_422_not_400()
    {
        var resp = await PushAsync(Client(), _ciToken,
            report: DemoReport().Replace("\"schemaVersion\":1", "\"schemaVersion\":99"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("schemaVersion 99 is newer than this server supports (1)", error);
    }

    [Fact]
    public async Task Raw_report_download_returns_the_pushed_bytes_to_members_only()
    {
        var raw = DemoReport();
        var push = await PushAsync(Client(), _ciToken, report: raw);
        var id = (await push.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var owner = await OwnerAsync();
        var download = await owner.GetAsync($"/api/runs/{id}/report");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/json", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(raw, await download.Content.ReadAsStringAsync()); // gzip 存、原樣還

        // 別的專案的成員:看不到這筆 run(不是 403,是當作不存在)
        string strangerInvite;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            var other = new Project { Id = Guid.NewGuid(), Name = "other", TokenHash = ProjectToken.Hash(ProjectToken.Generate()), CreatedAt = DateTimeOffset.UtcNow };
            db.Projects.Add(other);
            var (invite, token) = Invites.Create(other.Id, "stranger@t.local", ProjectRole.Owner, DateTimeOffset.UtcNow);
            db.Invites.Add(invite);
            await db.SaveChangesAsync();
            strangerInvite = token;
        }
        var stranger = Client();
        Assert.Equal(HttpStatusCode.OK, (await stranger.PostAsync($"/api/invites/{strangerInvite}/accept",
            Json(new { password = "stranger pass phrase" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/runs/{id}/report")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync($"/api/runs/{id}/report")).StatusCode);
    }

    [Fact]
    public async Task Invite_acceptance_and_membership_edge_cases()
    {
        var owner = await OwnerAsync();
        async Task<string> InviteAsync(string email, string role)
        {
            var resp = await owner.PostAsync($"/api/projects/{_projectId}/invites", Json(new { email, role }));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var link = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString()!;
            return link[(link.LastIndexOf('/') + 1)..];
        }

        // 1) 角色只有 owner/member/viewer
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/projects/{_projectId}/invites",
            Json(new { email = "x@t.local", role = "admin" }))).StatusCode);

        // 2) 密碼太短 → 400,邀請不被消耗;改用夠長的密碼才成立
        var memberInvite = await InviteAsync("dev@t.local", "member");
        var member = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsync($"/api/invites/{memberInvite}/accept",
            Json(new { password = "short" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync($"/api/invites/{memberInvite}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsync($"/api/invites/{memberInvite}/accept",
            Json(new { password = "a long enough pass" }))).StatusCode);

        // 3) 已有帳號的人被重邀:要用原密碼證明是本人
        var reInvite = await InviteAsync("dev@t.local", "viewer");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().PostAsync($"/api/invites/{reInvite}/accept",
            Json(new { password = "somebody else's guess" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsync($"/api/invites/{reInvite}/accept",
            Json(new { password = "a long enough pass" }))).StatusCode);

        // 4) Owner 移除成員:204、留帳,之後那個人讀不到這個專案的任何東西
        await PushAsync(Client(), _ciToken);
        Assert.Single((await member.GetFromJsonAsync<JsonElement>("/api/runs")).EnumerateArray());
        var members = await owner.GetFromJsonAsync<JsonElement>($"/api/projects/{_projectId}/members");
        var devId = members.GetProperty("members").EnumerateArray()
            .First(m => m.GetProperty("email").GetString() == "dev@t.local").GetProperty("userId").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{_projectId}/members/{devId}")).StatusCode);
        Assert.Empty((await member.GetFromJsonAsync<JsonElement>("/api/runs")).EnumerateArray());
        var audit = await owner.GetFromJsonAsync<JsonElement>($"/api/projects/{_projectId}/audit");
        Assert.Contains(audit.EnumerateArray(), a =>
            a.GetProperty("action").GetString() == "member-removed" && a.GetProperty("detail").GetString() == "dev@t.local");
    }

    [Fact]
    public async Task Rejected_pushes_store_nothing()
    {
        await PushAsync(Client(), "wrong-token-wrong-token");
        await PushAsync(Client(), _ciToken, report: "not json");
        await PushAsync(Client(), _ciToken, report: DemoReport().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Runs.CountAsync());
    }
}
