using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Parity.Cli;
using Parity.Engine;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// 真的 parity push 對真的 Parity.Server(Kestrel,綁 127.0.0.1 的動態埠)。
/// 兩邊各自的單元測試都綠,不代表兩邊對得上——標頭名、gate 口徑、錯誤狀態碼的提示,
/// 只有接起來跑才驗得到。TestServer 不經 Kestrel,所以 20MB 上限(413)也只有這裡測得到。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class ServerPushIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<ParityServerEntryPoint> _factory = null!;
    private string _dbPath = null!;
    private string _ciToken = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"parity-kestrel-{Guid.NewGuid():N}.db");
        // 埠要用 urls 設定給:Program 在沒有 urls 時會自己 UseUrls(127.0.0.1:4322),
        // 那會蓋過 UseKestrel(0)、綁到固定埠——跟本機正在跑的 Parity.Server 或另一個測試行程搶埠
        _factory = new WebApplicationFactory<ParityServerEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:parity", $"Data Source={_dbPath}");
            b.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        });
        _factory.UseKestrel();
        _factory.StartServer();
        _baseUrl = _factory.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        _ciToken = ProjectToken.Generate();
        db.Projects.Add(new Project
        {
            Id = Guid.NewGuid(),
            Name = "demo",
            TokenHash = ProjectToken.Hash(_ciToken),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { /* 殘檔在 %TEMP%,無害 */ }
    }

    private static string ReportJson(Severity severity)
    {
        var report = new FidelityReport("/", "http://x/", "snap",
            [new NodeResult("cta", "id1", "#cta", "auto-name", severity,
                [new PropDiff("paddingTop", "12", "8", "px", 4, 2, severity)])],
            [], new ReportSummary(1, 1, 0, 1, 0, severity == Severity.Serious ? 1 : 0, 0,
                severity == Severity.Minor ? 1 : 0, severity));
        return JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Indented);
    }

    [Fact]
    public async Task Cli_push_lands_in_the_server_with_its_metadata_and_gate_verdict()
    {
        using var dir = new TempDir();
        var reportPath = dir.Write("report.json", ReportJson(Severity.Minor));
        // 自訂 failOn 含 minor:伺服器預設口徑會判綠,CLI 依 config 重算判紅——儀表板必須跟 CI 一致
        var config = dir.Write("parity.config.json", """{ "gate": { "failOn": ["minor"] } }""");
        using var env = new EnvScope().Clear(EnvScope.PushVariables)
            .Set("PARITY_TOKEN", _ciToken)
            .Set("GITHUB_SHA", "0123456789abcdef0123456789abcdef01234567")
            .Set("GITHUB_REF_NAME", "main")
            .Set("GITHUB_ACTOR", "octocat")
            .Set("GITHUB_SERVER_URL", "https://github.com")
            .Set("GITHUB_REPOSITORY", "owner/repo");

        var exit = await PushCommand.RunAsync(["--server", _baseUrl, "--in", reportPath, "--config", config]);

        Assert.Equal(0, exit);
        using var scope = _factory.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Runs.SingleAsync();
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", run.CommitSha);
        Assert.Equal("main", run.Branch);
        Assert.Equal("octocat", run.TriggeredBy);
        Assert.Equal("https://github.com/owner/repo", run.RepoUrl);
        Assert.True(run.GateFailed);
        Assert.Equal(File.ReadAllText(reportPath), ReportBlob.Decompress(run.RawReportGzip));
    }

    [Fact]
    public async Task Cli_push_with_a_rotated_or_wrong_token_explains_the_401()
    {
        using var dir = new TempDir();
        var reportPath = dir.Write("report.json", ReportJson(Severity.Minor));
        using var env = new EnvScope().Clear(EnvScope.PushVariables).Set("PARITY_TOKEN", "wrong-token-wrong-token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PushCommand.RunAsync(["--server", _baseUrl, "--in", reportPath, "--gate-exit", "0"]));

        Assert.Contains("server returned 401", ex.Message);
        Assert.Contains("missing or unknown token", ex.Message);   // 伺服器的 error 欄位
        Assert.Contains("check PARITY_TOKEN", ex.Message);         // CLI 的提示
    }

    [Fact]
    public async Task Cli_push_of_a_report_over_20_MB_explains_the_413_every_time()
    {
        // 下面那條是測試端自己設 Expect: 100-continue;這條驗 CLI 本身。少了它,Kestrel 依 Content-Length
        // 回 413 後在上傳途中關連線,客戶端約一半拿到 "An error occurred while sending the request."
        // (稽核 probe 18 次中 9 次;本機單獨跑時較少見,40 次內就撞到),看不到 413 的提示。
        // 競態是機率性的,所以連推 40 次,每次都要拿到 413(小報告不送 Expect,這裡刻意做成 21 MB)
        var report = new FidelityReport("/", "http://x/", new string('x', 21 * 1024 * 1024), [], [],
            new ReportSummary(0, 0, 0, 0, 0, 0, 0, 0, Severity.None));
        using var dir = new TempDir();
        var reportPath = dir.Write("report.json", JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact));
        using var env = new EnvScope().Clear(EnvScope.PushVariables).Set("PARITY_TOKEN", _ciToken);

        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PushCommand.RunAsync(["--server", _baseUrl, "--in", reportPath, "--gate-exit", "0"]));
            Assert.Contains("server returned 413", ex.Message);
            Assert.Contains("larger than the server accepts", ex.Message);
        }
    }

    [Fact]
    public async Task Bodies_over_20_MB_are_rejected_with_413()
    {
        // 20MB 是 Kestrel 的 MaxRequestBodySize 護欄(大站接近上限時 413 要給出路)
        var body = new string(' ', 20 * 1024 * 1024 + 1);
        // Expect100ContinueTimeout 預設只有 1 秒:伺服器 1 秒內沒回應(冷啟動、慢的 CI runner),
        // 客戶端照樣開始送本文,下面那個 RST 競態就回來了。拉長到 60 秒,讓客戶端一定先等伺服器回應
        using var http = new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(60) });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/runs")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ciToken);
        // Expect: 100-continue:客戶端等伺服器先回應(不受預設 1 秒逾時影響),Kestrel 依 Content-Length
        // 先回 413,本文不會送出。少了它,客戶端還在上傳 20MB 時 Kestrel 就回 413 並關連線(RST),
        // HttpClient 時而讀到 413、時而拋 HttpRequestException(整套跑、伺服器回得快時最常撞到)。
        // 改用 ResponseHeadersRead 也救不了
        req.Headers.ExpectContinue = true;

        using var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }
}
