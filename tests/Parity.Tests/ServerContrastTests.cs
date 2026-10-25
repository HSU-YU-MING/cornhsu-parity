using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Parity.Engine;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// 合併伺服器的 XamlContrast 那一半:`xamlcontrast --json` 的報告 → Run + ContrastResult。
/// 樣本 Golden/xamlcontrast.v6.json 不是手寫的——是 XamlContrast 1.0(main 18c79d8)對它自己的
/// samples/demo 實跑的輸出(刻意做壞的示範:fail 3 / ok 3 / decorative 1 / 壓掉 1)。
/// 伺服器沒有引用 XamlContrast 的組件,這份樣本就是兩邊契約的對照組。
/// </summary>
public class ServerContrastIngestTests
{
    internal static string Demo() => Repo.Read("tests", "Parity.Tests", "Golden", "xamlcontrast.v6.json");

    /// <summary>改樣本的一個欄位(JSON 路徑用 / 分隔),其餘原樣。</summary>
    internal static string DemoWith(string path, JsonNode? value)
    {
        var root = JsonNode.Parse(Demo())!;
        var parts = path.Split('/');
        var parent = parts[..^1].Aggregate(root, (node, key) => node[key]!);
        parent[parts[^1]] = value;
        return root.ToJsonString();
    }

    internal static string DemoWithout(string path)
    {
        var root = JsonNode.Parse(Demo())!;
        var parts = path.Split('/');
        var parent = parts[..^1].Aggregate(root, (node, key) => node[key]!);
        parent.AsObject().Remove(parts[^1]);
        return root.ToJsonString();
    }

    private static readonly RunMetadata NoMeta = new(null, null, null);

    [Fact]
    public void The_demo_report_maps_to_a_contrast_run_with_its_summary_counts()
    {
        var raw = Demo();
        var run = ContrastIngest.Parse(raw, new RunMetadata("abc123", "main", "cornhsu"), DateTimeOffset.UnixEpoch);

        Assert.Equal(RunTool.XamlContrast, run.Tool);
        Assert.Null(run.Score);  // XamlContrast 沒有分數,不編一個
        Assert.Empty(run.Pages);
        Assert.True(run.GateFailed); // 報告上 exitCode 1
        Assert.Equal(("abc123", "main", "cornhsu"), (run.CommitSha, run.Branch, run.TriggeredBy));
        var c = run.Contrast!;
        Assert.Equal((3, 0, 3, 1), (c.Fail, c.Warn, c.Ok, c.Decorative));
        Assert.Equal((1, 8, 100.0, 0), (c.Files, c.Pairs, c.Coverage, c.Unresolved));
        Assert.Equal((1, 0), (c.Suppressed, c.ParseErrors));
        Assert.Equal(("pair", false, "warn"), (c.PaletteMode, c.PaletteDegraded, c.FailOn));
        Assert.Equal((1, "0.0.0-dev"), (c.ExitCode, c.ToolVersion));
        Assert.Equal(raw, ReportBlob.Decompress(run.RawReportGzip)); // 原文逐位元留著,詳情頁從這裡讀
    }

    [Theory]
    [InlineData(true, 0, true)]    // push 端帶來的判定優先(含 baseline 模式下 CLI 的真實結果)
    [InlineData(false, 1, false)]
    [InlineData(null, 0, false)]   // 沒標頭 → 報告上 CLI 自己的判定
    [InlineData(null, 1, true)]
    [InlineData(null, 3, true)]    // 3 = 結果不可信,CI 上也是紅的
    public void Gate_comes_from_the_push_then_from_the_reports_exit_code(bool? pushed, int exitCode, bool expected)
    {
        var run = ContrastIngest.Parse(DemoWith("exitCode", exitCode), NoMeta with { GateFailed = pushed }, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, run.GateFailed);
    }

    [Theory]
    [InlineData("warn", 0, 2, true)]  // 經函式庫產生、沒有 exitCode 的報告:照它自己的 failOn 判
    [InlineData("fail", 0, 2, false)]
    [InlineData("fail", 1, 0, true)]
    [InlineData("warn", 0, 0, false)]
    public void Without_an_exit_code_the_gate_follows_the_reports_fail_on(string failOn, int fail, int warn, bool expected)
    {
        var root = JsonNode.Parse(DemoWith("exitCode", null))!;
        root["summary"]!["failOn"] = failOn;
        root["summary"]!["counts"]!["fail"] = fail;
        root["summary"]!["counts"]!["warn"] = warn;

        var run = ContrastIngest.Parse(root.ToJsonString(), NoMeta, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, run.GateFailed);
        Assert.Null(run.Contrast!.ExitCode);
    }

    [Fact]
    public void A_newer_schema_version_is_unsupported_not_bad_data()
    {
        var ex = Assert.Throws<UnsupportedSchemaException>(() =>
            ContrastIngest.Parse(DemoWith("schemaVersion", 7), NoMeta, DateTimeOffset.UnixEpoch));

        Assert.Contains("schemaVersion 7 is newer than this server supports (6)", ex.Message);
    }

    [Fact]
    public void A_pre_1_0_report_is_unsupported_and_says_to_upgrade()
    {
        var ex = Assert.Throws<UnsupportedSchemaException>(() =>
            ContrastIngest.Parse(DemoWith("schemaVersion", 5), NoMeta, DateTimeOffset.UnixEpoch));

        Assert.Contains("0.x", ex.Message);
        Assert.Contains("Upgrade Cornhsu.XamlContrast to 1.x", ex.Message);
    }

    [Theory]
    [InlineData("summary/counts")]
    [InlineData("summary/coverage")]
    [InlineData("summary/failOn")]
    [InlineData("findings")]
    [InlineData("unresolvedSites")]
    [InlineData("exitCode")] // 1.0 起每個欄位一律寫出(沒有值是 null,不是沒有 key)
    public void A_missing_field_is_bad_data(string path)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContrastIngest.Parse(DemoWithout(path), NoMeta, DateTimeOffset.UnixEpoch));

        Assert.Contains("not a valid XamlContrast report", ex.Message);
    }

    [Fact]
    public void A_count_written_as_a_string_is_bad_data()
    {
        var root = JsonNode.Parse(Demo())!;
        root["summary"]!["counts"]!["fail"] = "3";

        Assert.Throws<InvalidOperationException>(() =>
            ContrastIngest.Parse(root.ToJsonString(), NoMeta, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "summary": {}, "findings": [] }""")] // 沒有 schemaVersion
    public void Garbage_is_rejected_with_a_clear_message(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContrastIngest.Parse(raw, NoMeta, DateTimeOffset.UnixEpoch));

        Assert.Contains("not an XamlContrast report", ex.Message);
    }

    [Fact]
    public void A_parity_report_sent_as_xamlcontrast_points_to_parity_push()
    {
        var parity = JsonSerializer.Serialize(ReportDocument.Of([]), ReportWire.Compact);

        var ex = Assert.Throws<InvalidOperationException>(() => ContrastIngest.Parse(parity, NoMeta, DateTimeOffset.UnixEpoch));

        Assert.Contains("looks like a Parity report", ex.Message);
    }

    [Fact]
    public void An_xamlcontrast_report_sent_without_the_tool_header_points_to_xamlcontrast_push()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunIngest.Parse(Demo(), NoMeta, DateTimeOffset.UnixEpoch));

        Assert.Contains("looks like an XamlContrast report", ex.Message);
        Assert.Contains("xamlcontrast --push", ex.Message);
    }
}

/// <summary>
/// 端點層:同一個 POST /api/runs 收兩種工具(X-Parity-Tool 分流),讀取端各走各的,
/// 而且 XamlContrast 的 run 不會滲進 parity 的總覽、趨勢與「跟上次比」。
/// </summary>
public class ServerContrastEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<ParityServerEntryPoint> _factory = null!;
    private string _dbPath = null!;
    private string _ciToken = null!;
    private string _ownerInviteToken = null!;
    private Guid _projectId;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"parity-inttest-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<ParityServerEntryPoint>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:parity", $"Data Source={_dbPath}"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        _ciToken = ProjectToken.Generate();
        var project = new Project { Id = Guid.NewGuid(), Name = "desktop", TokenHash = ProjectToken.Hash(_ciToken), CreatedAt = DateTimeOffset.UtcNow };
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { /* 殘檔在 %TEMP%,無害 */ }
    }

    private HttpClient Client() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private async Task<HttpClient> OwnerAsync()
    {
        var owner = Client();
        var accept = await owner.PostAsync($"/api/invites/{_ownerInviteToken}/accept",
            new StringContent("""{ "password": "correct horse battery" }""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        return owner;
    }

    private async Task<HttpResponseMessage> PushAsync(string report, string? tool, string? branch = "main", string? gate = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/runs")
        {
            Content = new StringContent(report, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ciToken);
        if (tool is not null) req.Headers.Add("X-Parity-Tool", tool);
        if (branch is not null) req.Headers.Add("X-Parity-Branch", branch);
        if (gate is not null) req.Headers.Add("X-Parity-Gate", gate);
        return await Client().SendAsync(req);
    }

    private async Task<string> PushContrastAsync(int fail, int warn, string branch = "main")
    {
        var root = JsonNode.Parse(ServerContrastIngestTests.Demo())!;
        root["summary"]!["counts"]!["fail"] = fail;
        root["summary"]!["counts"]!["warn"] = warn;
        var resp = await PushAsync(root.ToJsonString(), RunTool.XamlContrast, branch);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static string ParityReport(Severity severity)
    {
        var diffs = severity == Severity.None ? new List<PropDiff>() : [new PropDiff("paddingTop", "12", "8", "px", 4, 2, severity)];
        var report = new FidelityReport("/", "http://x/", "snap",
            [new NodeResult("cta", "id1", "#cta", "auto-name", severity, diffs)], [],
            new ReportSummary(1, 1, 0, diffs.Count, 0, severity == Severity.Serious ? 1 : 0, 0, 0, severity));
        return JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact);
    }

    [Fact]
    public async Task An_xamlcontrast_push_is_acknowledged_with_its_own_fields()
    {
        var resp = await PushAsync(ServerContrastIngestTests.Demo(), RunTool.XamlContrast, gate: "pass");

        // xamlcontrast --push 讀的就是這八個欄位;parity 那一組(六個)由 ServerEndpointTests 釘住
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var ack = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["id", "project", "tool", "gateFailed", "fail", "warn", "coverage", "url"],
            ack.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("desktop", "xamlcontrast"), (ack.GetProperty("project").GetString(), ack.GetProperty("tool").GetString()));
        Assert.False(ack.GetProperty("gateFailed").GetBoolean()); // X-Parity-Gate: pass 蓋過報告的 exitCode 1
        Assert.Equal((3, 0, 100.0), (ack.GetProperty("fail").GetInt32(), ack.GetProperty("warn").GetInt32(), ack.GetProperty("coverage").GetDouble()));
        Assert.Equal($"/runs/{ack.GetProperty("id").GetString()}", ack.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("XamlContrast")] // 只認小寫——跟 X-Parity-Gate 一樣不猜
    [InlineData("eslint")]
    [InlineData("")]
    public async Task An_unknown_tool_is_400_stores_nothing_and_does_not_echo_the_value(string tool)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/runs")
        {
            Content = new StringContent(ServerContrastIngestTests.Demo(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ciToken);
        req.Headers.TryAddWithoutValidation("X-Parity-Tool", tool);
        var resp = await Client().SendAsync(req);

        if (tool.Length == 0)
        {
            // 空值 = 沒帶 → parity;XamlContrast 的報告當 parity 解 → 400,訊息指路
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("xamlcontrast --push", await resp.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("unknown X-Parity-Tool", body);
            Assert.DoesNotContain(tool, body);
        }
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Runs.CountAsync());
    }

    [Fact]
    public async Task Unsupported_schema_versions_are_422()
    {
        var newer = await PushAsync(ServerContrastIngestTests.DemoWith("schemaVersion", 7), RunTool.XamlContrast);
        var older = await PushAsync(ServerContrastIngestTests.DemoWith("schemaVersion", 5), RunTool.XamlContrast);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, newer.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, older.StatusCode);
    }

    [Fact]
    public async Task The_run_list_and_detail_say_which_tool_and_carry_the_contrast_summary()
    {
        var contrastId = await PushContrastAsync(fail: 2, warn: 5);
        Assert.Equal(HttpStatusCode.Created, (await PushAsync(ParityReport(Severity.None), tool: null)).StatusCode);
        var owner = await OwnerAsync();

        var runs = (await owner.GetFromJsonAsync<JsonElement>("/api/runs")).EnumerateArray().ToList();
        var parity = runs.Single(r => r.GetProperty("tool").GetString() == RunTool.Parity);
        var contrast = runs.Single(r => r.GetProperty("tool").GetString() == RunTool.XamlContrast);
        Assert.Equal(JsonValueKind.Null, parity.GetProperty("contrast").ValueKind);
        Assert.Equal(100, parity.GetProperty("score").GetInt32());
        Assert.Equal(JsonValueKind.Null, contrast.GetProperty("score").ValueKind);
        Assert.Equal((2, 5), (contrast.GetProperty("contrast").GetProperty("fail").GetInt32(),
            contrast.GetProperty("contrast").GetProperty("warn").GetInt32()));

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/runs/{contrastId}");
        Assert.Equal("xamlcontrast", detail.GetProperty("tool").GetString());
        var summary = detail.GetProperty("contrast");
        Assert.Equal((8, "pair", "warn"), (summary.GetProperty("pairs").GetInt32(),
            summary.GetProperty("paletteMode").GetString(), summary.GetProperty("failOn").GetString()));
        Assert.False(detail.TryGetProperty("pages", out _)); // parity 的頁面清單不出現在 XamlContrast 的 run 上

        // 逐筆 findings 從原文讀:/report 回的就是推上來的那份
        var raw = await owner.GetStringAsync($"/api/runs/{contrastId}/report");
        Assert.Equal(7, JsonNode.Parse(raw)!["findings"]!.AsArray().Count);
    }

    [Fact]
    public async Task Contrast_overview_and_trend_follow_the_branch_and_membership()
    {
        await PushContrastAsync(fail: 4, warn: 1);
        await PushContrastAsync(fail: 9, warn: 9, branch: "feature/x"); // PR 分支不混進 main
        var latest = await PushContrastAsync(fail: 2, warn: 3);
        var owner = await OwnerAsync();

        var card = Assert.Single((await owner.GetFromJsonAsync<JsonElement>("/api/contrast/overview?branch=main")).EnumerateArray());
        Assert.Equal(("desktop", 2, 3), (card.GetProperty("project").GetString(), card.GetProperty("fail").GetInt32(), card.GetProperty("warn").GetInt32()));
        Assert.Equal((4, 1, 2), (card.GetProperty("prevFail").GetInt32(), card.GetProperty("prevWarn").GetInt32(), card.GetProperty("runCount").GetInt32()));

        var trend = (await owner.GetFromJsonAsync<JsonElement>($"/api/contrast/trend?project={_projectId}&branch=main")).EnumerateArray().ToList();
        Assert.Equal([4, 2], trend.Select(p => p.GetProperty("fail").GetInt32())); // 舊 → 新
        Assert.Equal(latest, trend[^1].GetProperty("runId").GetString());

        // 不分支:三筆都算
        Assert.Equal(3, (await owner.GetFromJsonAsync<JsonElement>($"/api/contrast/trend?project={_projectId}")).GetArrayLength());
        // 未登入 401;parity 的總覽不受 XamlContrast 的 run 影響(這個專案沒有 parity 的 run)
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync("/api/contrast/overview")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/overview?branch=main")).EnumerateArray());
    }

    [Fact]
    public async Task Changes_skip_xamlcontrast_runs_between_two_parity_runs()
    {
        // 同分支:parity(有落差)→ xamlcontrast → parity(修好了)。「上一筆」要跳過中間那筆,
        // 拿它的原文當 parity 報告解會 500
        var first = await PushAsync(ParityReport(Severity.Serious), tool: null);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var contrastId = await PushContrastAsync(fail: 1, warn: 0);
        var second = await PushAsync(ParityReport(Severity.None), tool: null);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var owner = await OwnerAsync();

        var changes = await owner.GetFromJsonAsync<JsonElement>($"/api/runs/{secondId}/changes");
        Assert.Equal(firstId, changes.GetProperty("prevRunId").GetString());
        Assert.Single(changes.GetProperty("fixed").EnumerateArray());
        // XamlContrast 的 run 還沒有逐筆比對:404,不是一個假裝「這是第一筆」的空比對
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/runs/{contrastId}/changes")).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_xamlcontrast_run_removes_its_summary_row()
    {
        var id = await PushContrastAsync(fail: 1, warn: 1);
        var owner = await OwnerAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/runs/{id}")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        Assert.Equal((0, 0), (await db.Runs.CountAsync(), await db.ContrastResults.CountAsync()));
    }

    [Fact]
    public async Task Rows_stored_per_contrast_push_do_not_grow_with_the_number_of_findings()
    {
        // 與 parity 同一個取捨(CLAUDE.md「刻意沒有 Diff 表」):逐筆 findings 只在原文裡
        static string WithFindings(int n)
        {
            var root = JsonNode.Parse(ServerContrastIngestTests.Demo())!;
            var template = root["findings"]![1]!.ToJsonString();
            root["findings"] = new JsonArray(Enumerable.Range(0, n).Select(_ => JsonNode.Parse(template)).ToArray());
            return root.ToJsonString();
        }
        async Task<(int Runs, int Contrast, int Pages)> CountAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            return (await db.Runs.CountAsync(), await db.ContrastResults.CountAsync(), await db.PageResults.CountAsync());
        }

        Assert.Equal(HttpStatusCode.Created, (await PushAsync(WithFindings(1), RunTool.XamlContrast)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PushAsync(WithFindings(500), RunTool.XamlContrast)).StatusCode);

        Assert.Equal((2, 2, 0), await CountAsync());
    }
}
