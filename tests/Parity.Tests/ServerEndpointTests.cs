using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
    public async Task Login_answers_unknown_email_and_wrong_password_alike_and_locks_out_after_five_failures()
    {
        await OwnerAsync(); // 接受邀請 = 建立帳號(密碼 "correct horse battery")
        async Task<(HttpStatusCode Status, JsonElement Body)> LoginAsync(HttpClient c, string email, string password)
        {
            var resp = await c.PostAsync("/api/auth/login", Json(new { email, password }));
            return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<JsonElement>());
        }

        // 1) 不存在的 email 與錯密碼:狀態碼與訊息逐字相同(訊息層面不能拿來探測誰有帳號;
        //    回應時間由下一條測試的假驗證對齊;剩下的探測管道是第 3 段的鎖定訊息)
        var unknown = await LoginAsync(Client(), "nobody@t.local", "correct horse battery");
        var wrong = await LoginAsync(Client(), "owner@t.local", "not the password");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal(unknown.Body.GetRawText(), wrong.Body.GetRawText());

        // 2) 正確登入(email 前後空白容忍):回 me 的形狀,cookie 隨即生效
        var browser = Client();
        var ok = await LoginAsync(browser, "  owner@t.local ", "correct horse battery");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("owner@t.local", ok.Body.GetProperty("email").GetString());
        var membership = Assert.Single(ok.Body.GetProperty("memberships").EnumerateArray());
        Assert.Equal(("demo", "owner"), (membership.GetProperty("project").GetString(), membership.GetProperty("role").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/auth/me")).StatusCode);

        // 3) 連錯 5 次 → 鎖定:之後連正確密碼也進不去,訊息改說「稍後再試」。
        //    注意這則訊息只有「存在的帳號」才會出現(不存在的 email 不走鎖定,永遠回 incorrect),
        //    等於另一條帳號探測管道——這裡釘的是現行行為,不是「完全不洩漏帳號」的保證。
        //    訊息要不要統一由產品決定(ROADMAP 已知盲點有留帳),決定後再改這段斷言
        for (var i = 0; i < 5; i++)
            await LoginAsync(Client(), "owner@t.local", $"guess number {i}");
        var locked = await LoginAsync(Client(), "owner@t.local", "correct horse battery");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.Status);
        Assert.Contains("too many failed attempts", locked.Body.GetProperty("error").GetString());
    }

    /// <summary>數 VerifyHashedPassword 被呼叫幾次(其餘照 Identity 預設的 PBKDF2)。</summary>
    private sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();
        public int Verifications;
        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);
        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref Verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    [Fact]
    public async Task Login_with_an_unknown_email_still_pays_for_one_password_hash_check()
    {
        // 不存在的 email 原本直接回 401、不跑密碼雜湊(PBKDF2,數十毫秒)——回得明顯比存在的帳號快,
        // 光看回應時間就能探測誰有帳號。訊息已經逐字相同(上一條),時間也要對齊:跑一次同成本的假驗證
        var hasher = new CountingHasher();
        await using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher<AppUser>>(hasher)));
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/auth/login", Json(new { email = "nobody@t.local", password = "whatever-1234" }));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(1, hasher.Verifications);
    }

    [Fact]
    public async Task Run_changes_endpoint_compares_with_the_previous_run_on_the_same_branch_for_members_only()
    {
        async Task<string> PushOnMainAsync(string report)
        {
            var resp = await PushAsync(Client(), _ciToken, branch: "main", report: report);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        var first = await PushOnMainAsync(DemoReport());   // cta 的 paddingTop serious
        await PushAsync(Client(), _ciToken, branch: "other", report: CleanReport()); // 別的分支不算「上一筆」
        var second = await PushOnMainAsync(CleanReport()); // 修好了
        var owner = await OwnerAsync();

        var firstChanges = await owner.GetFromJsonAsync<JsonElement>($"/api/runs/{first}/changes");
        Assert.Equal(JsonValueKind.Null, firstChanges.GetProperty("prevRunId").ValueKind); // 該分支第一筆

        var changes = await owner.GetFromJsonAsync<JsonElement>($"/api/runs/{second}/changes");
        Assert.Equal(first, changes.GetProperty("prevRunId").GetString());
        Assert.Empty(changes.GetProperty("regressions").EnumerateArray());
        var fixedDiff = Assert.Single(changes.GetProperty("fixed").EnumerateArray());
        Assert.Equal(("cta", "paddingTop", "serious"), (fixedDiff.GetProperty("designLayer").GetString(),
            fixedDiff.GetProperty("prop").GetString(), fixedDiff.GetProperty("severity").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/runs/{Guid.NewGuid()}/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync($"/api/runs/{second}/changes")).StatusCode);
    }

    [Fact]
    public async Task Push_auth_scheme_is_case_insensitive_but_must_be_bearer()
    {
        async Task<HttpStatusCode> PushWithAuthorizationAsync(string authorization)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/runs")
            {
                Content = new StringContent(DemoReport(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", authorization);
            return (await Client().SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await PushWithAuthorizationAsync($"bearer {_ciToken}"));
        // "Digest " 與 "Bearer " 一樣長:沒驗 scheme 的話,切掉前 7 字剛好就是合法 token
        Assert.Equal(HttpStatusCode.Unauthorized, await PushWithAuthorizationAsync($"Digest {_ciToken}"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PushWithAuthorizationAsync(_ciToken)); // 少了 scheme
        // 截短的 token 行為上被拒(查無此 token)。長度護欄只是查庫前的短路,行為與查無等價,這條不守它
        Assert.Equal(HttpStatusCode.Unauthorized, await PushWithAuthorizationAsync($"Bearer {_ciToken[..10]}"));

        // scheme 後多個空白、結尾空白也要認得(伺服器端 Trim)。這條不能走 HttpClient:它把 Authorization
        // 解析成 AuthenticationHeaderValue 再送,多餘空白在到達伺服器之前就被正規化掉了——
        // 所以直接對 TestServer 送原始 header
        var raw = await _factory.Server.SendAsync(c =>
        {
            var body = Encoding.UTF8.GetBytes(DemoReport());
            c.Request.Method = "POST";
            c.Request.Path = "/api/runs";
            c.Request.Headers.Authorization = $"Bearer   {_ciToken}  ";
            c.Request.ContentType = "application/json";
            c.Request.ContentLength = body.Length;
            c.Request.Body = new MemoryStream(body);
        });
        Assert.Equal((int)HttpStatusCode.Created, raw.Response.StatusCode);
    }

    [Fact]
    public async Task Rows_stored_per_push_do_not_grow_with_the_number_of_diffs()
    {
        // CLAUDE.md「Parity.Server.Data 刻意沒有 Diff 表」:M4 實測一次 push 的逐條落差 36,058 列、零讀者,
        // 所以只存 run + 每頁一列 + gzip 原文。這條守的是那個取捨的效果,不管表叫什麼名字——
        // 要把逐條落差加回資料庫,先回 CLAUDE.md 確認有讀者
        async Task<Dictionary<string, long>> RowCountsAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var conn = scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.GetDbConnection();
            await conn.OpenAsync();
            var tables = new List<string>();
            await using (var list = conn.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                await using var reader = await list.ExecuteReaderAsync();
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            }
            var counts = new Dictionary<string, long>();
            foreach (var table in tables)
            {
                await using var count = conn.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                counts[table] = (long)(await count.ExecuteScalarAsync())!;
            }
            return counts;
        }
        static string ReportWithDiffs(int n)
        {
            var nodes = Enumerable.Range(0, n).Select(i => new NodeResult($"n{i}", $"id{i}", $"#n{i}", "selector", Severity.Serious,
                [new PropDiff("paddingTop", "12", "8", "px", 4, 2, Severity.Serious)])).ToList();
            var report = new FidelityReport("/", "http://x/", "snap", nodes, [],
                new ReportSummary(n, n, 0, n, 0, n, 0, 0, Severity.Serious));
            return JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact);
        }

        var before = await RowCountsAsync();
        Assert.Equal(HttpStatusCode.Created, (await PushAsync(Client(), _ciToken, report: ReportWithDiffs(1))).StatusCode);
        var afterSmall = await RowCountsAsync();
        Assert.Equal(HttpStatusCode.Created, (await PushAsync(Client(), _ciToken, report: ReportWithDiffs(300))).StatusCode);
        var afterLarge = await RowCountsAsync();

        foreach (var table in before.Keys)
            Assert.True(afterLarge[table] - afterSmall[table] == afterSmall[table] - before[table],
                $"{table}:1 條落差的 push 加了 {afterSmall[table] - before[table]} 列,300 條的加了 {afterLarge[table] - afterSmall[table]} 列");
        Assert.Equal(1, afterSmall["Runs"] - before["Runs"]); // 前提:真的有落庫
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
