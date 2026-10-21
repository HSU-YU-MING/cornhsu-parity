using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// parity push 的客戶端(信封檢查見 ServerIngestTests 的 PushEnvelopeTests;伺服器端見 ServerEndpointTests)。
/// push 送出的標頭就是它跟 Parity.Server 之間的協定——CLI 跟著 NuGet 發、Server 由使用者自架,
/// 兩邊版本錯開是常態,所以每個標頭的名字與值都要釘住。假伺服器綁 127.0.0.1:0,不連外網。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class PushCommandTests
{
    private static FidelityReport Report(Severity severity, bool soft = false, int matched = 1)
        => new("/", "http://x/", "d",
            matched == 0 ? [] : [new NodeResult("a", "a", "#a", "selector", severity,
                severity == Severity.None ? [] : [new PropDiff("width", "1", "9", "px", 8, 2, severity, Soft: soft)])],
            [], new ReportSummary(1, matched, 1 - matched, severity == Severity.None ? 0 : 1,
                severity == Severity.Critical ? 1 : 0, severity == Severity.Serious ? 1 : 0,
                severity == Severity.Medium ? 1 : 0, severity == Severity.Minor ? 1 : 0, severity));

    private static string WriteReport(TempDir dir, FidelityReport report, string relative = "report.json")
        => dir.Write(relative, JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Indented));

    /// <summary>每條測試一個乾淨的 CI 環境:GitHub Actions 上這些變數都有值,不清掉結果就看在哪裡跑。</summary>
    private static EnvScope CleanEnv() => new EnvScope().Clear(EnvScope.PushVariables);

    // ---------- ResolveGate:--gate-exit 優先,否則依 config 重算 ----------

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("3", true)]
    [InlineData("-1", true)]
    public void Gate_exit_flag_wins_and_only_zero_is_a_pass(string exitCode, bool failed)
    {
        // 報告本身全綠——證明 --gate-exit 給了就不再看報告
        var doc = ReportDocument.Of([Report(Severity.None)]);

        Assert.Equal(failed, PushCommand.ResolveGate(new() { ["--gate-exit"] = exitCode }, doc));
    }

    [Fact]
    public void Non_numeric_gate_exit_is_an_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PushCommand.ResolveGate(new() { ["--gate-exit"] = "abc" }, ReportDocument.Of([])));
        Assert.Contains("--gate-exit expects the numeric exit code", ex.Message);
        Assert.Contains("abc", ex.Message);
    }

    [Theory]
    [InlineData(Severity.Serious, 1, """{ }""", true)]                                   // 預設 failOn
    [InlineData(Severity.Minor, 1, """{ }""", false)]
    [InlineData(Severity.Minor, 1, """{ "gate": { "failOn": ["minor"] } }""", true)]      // 使用者自訂 failOn
    [InlineData(Severity.None, 0, """{ }""", true)]                                        // 0 配對 = 可信度不足,也算紅
    public void Without_gate_exit_the_verdict_is_recomputed_from_the_config(
        Severity severity, int matched, string configJson, bool failed)
    {
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", configJson);

        Assert.Equal(failed, PushCommand.ResolveGate(new() { ["--config"] = config },
            ReportDocument.Of([Report(severity, matched: matched)])));
    }

    [Fact]
    public void Without_gate_exit_or_any_config_no_verdict_is_sent()
    {
        using var dir = new TempDir();
        using var cwd = new CwdScope(dir.Path);
        Assert.Null(ParityConfig.FindConfigFile(dir.Path)); // 前提

        Assert.Null(PushCommand.ResolveGate([], ReportDocument.Of([Report(Severity.Critical)])));
    }

    [Fact]
    public void Without_config_flag_the_config_is_found_from_cwd()
    {
        using var dir = new TempDir();
        dir.Write("parity.config.json", """{ "gate": { "failOn": ["critical"] } }""");
        using var cwd = new CwdScope(Directory.CreateDirectory(dir.File("sub")).FullName);

        Assert.False(PushCommand.ResolveGate([], ReportDocument.Of([Report(Severity.Serious)])));
    }

    // ---------- RunAsync:對假伺服器送一次,看實際送出的東西 ----------

    [Fact]
    public async Task Push_sends_the_raw_report_with_bearer_token_and_ci_metadata()
    {
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.Minor));
        var summary = dir.Write("summary.md", "existing line\n");
        await using var server = await FakeServer.StartAsync(201,
            """{ "id": "r1", "project": "demo", "score": 87, "gateFailed": false, "pages": 1, "url": "/runs/r1" }""");
        using var env = CleanEnv()
            .Set("PARITY_TOKEN", "tok-0123456789abcdefghij")
            .Set("GITHUB_SHA", "0123456789abcdef0123456789abcdef01234567")
            .Set("GITHUB_HEAD_REF", "feature/x")
            .Set("GITHUB_REF_NAME", "42/merge")
            .Set("GITHUB_ACTOR", "octocat")
            .Set("GITHUB_SERVER_URL", "https://github.com/")
            .Set("GITHUB_REPOSITORY", "owner/repo")
            .Set("GITHUB_STEP_SUMMARY", summary);

        var r = await Cli.RunAsync("push", "--server", server.Url + "/", "--in", reportPath, "--gate-exit", "0");

        Assert.Equal(0, r.ExitCode);
        var req = Assert.Single(server.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/api/runs", req.Path);
        Assert.Equal("Bearer tok-0123456789abcdefghij", req.Header("Authorization"));
        Assert.StartsWith("application/json", req.Header("Content-Type"));
        Assert.Equal(File.ReadAllText(reportPath), req.Body); // 原文照送,不重新序列化
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", req.Header("X-Parity-Commit"));
        Assert.Equal("feature/x", req.Header("X-Parity-Branch")); // PR 事件:來源分支優先於 merge ref
        Assert.Equal("octocat", req.Header("X-Parity-Triggered-By"));
        Assert.Equal("https://github.com/owner/repo", req.Header("X-Parity-Repo-Url"));
        Assert.Equal("pass", req.Header("X-Parity-Gate"));

        Assert.Contains($"pushed: {reportPath}", r.Out);
        Assert.Contains("  project  demo", r.Out);
        Assert.Contains("  score    87/100", r.Out);
        Assert.Contains($"  view     {server.Url}/runs/r1", r.Out);
        Assert.Equal($"existing line\n**Parity** — [demo 87/100 ✅]({server.Url}/runs/r1)\n", File.ReadAllText(summary));
    }

    [Theory]
    [InlineData("cli", "head", "ref", "cli")]
    [InlineData(null, "head", "ref", "head")]
    [InlineData(null, null, "ref", "ref")]
    [InlineData(null, null, null, null)]
    public async Task Branch_precedence_is_flag_then_head_ref_then_ref_name(
        string? flag, string? headRef, string? refName, string? expected)
    {
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.None));
        await using var server = await FakeServer.StartAsync(201, """{ "url": "/runs/x" }""");
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij")
            .Set("GITHUB_HEAD_REF", headRef).Set("GITHUB_REF_NAME", refName);

        string[] args = ["push", "--server", server.Url, "--in", reportPath, "--gate-exit", "0"];
        var r = await Cli.RunAsync(flag is null ? args : [.. args, "--branch", flag]);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(expected, Assert.Single(server.Requests).Header("X-Parity-Branch"));
    }

    [Fact]
    public async Task Outside_ci_only_auth_and_gate_headers_are_sent()
    {
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.Serious));
        var config = dir.Write("parity.config.json", "{ }");
        await using var server = await FakeServer.StartAsync(201, """{ "project": "p", "score": 0, "url": "/runs/x" }""");
        // 只有 SERVER_URL、沒有 REPOSITORY:拼不出 repo 網址就不送
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij").Set("GITHUB_SERVER_URL", "https://github.com");

        var r = await Cli.RunAsync("push", "--server", server.Url, "--in", reportPath, "--config", config,
            "--commit", "deadbeef");

        Assert.Equal(0, r.ExitCode);
        var req = Assert.Single(server.Requests);
        Assert.Equal("deadbeef", req.Header("X-Parity-Commit"));
        Assert.Null(req.Header("X-Parity-Branch"));
        Assert.Null(req.Header("X-Parity-Triggered-By"));
        Assert.Null(req.Header("X-Parity-Repo-Url"));
        Assert.Equal("fail", req.Header("X-Parity-Gate")); // config 的預設 failOn 重算:serious → 紅
    }

    [Fact]
    public async Task Job_summary_marks_a_failed_gate_with_a_cross()
    {
        // 紅燈的 PR:job summary 那一行就是 reviewer 點進儀表板的入口,圖示要跟 CI 的結果一致
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.Serious));
        var summary = dir.Write("summary.md", "");
        await using var server = await FakeServer.StartAsync(201,
            """{ "id": "r9", "project": "demo", "score": 0, "gateFailed": true, "pages": 1, "url": "/runs/r9" }""");
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij").Set("GITHUB_STEP_SUMMARY", summary);

        var r = await Cli.RunAsync("push", "--server", server.Url, "--in", reportPath, "--gate-exit", "3");

        Assert.Equal(0, r.ExitCode); // push 本身成功;紅燈是 check 那一步的事
        Assert.Equal("fail", Assert.Single(server.Requests).Header("X-Parity-Gate"));
        Assert.Equal($"**Parity** — [demo 0/100 ❌]({server.Url}/runs/r9)\n", File.ReadAllText(summary));
    }

    [Fact]
    public async Task Job_summary_follows_the_servers_verdict_when_the_cli_could_not_compute_one()
    {
        // 沒給 --gate-exit、又找不到 config:CLI 不送 X-Parity-Gate,伺服器用預設口徑(critical/serious)
        // 判 FAIL 並在回應帶 gateFailed: true。summary 原本只看 CLI 自己的 null → 一律 ✅,
        // 正是 M4.5 宣稱根治的「CI 綠、儀表板紅」
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.Serious));
        var summary = dir.Write("summary.md", "");
        await using var server = await FakeServer.StartAsync(201,
            """{ "id": "r7", "project": "demo", "score": 0, "gateFailed": true, "pages": 1, "url": "/runs/r7" }""");
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij").Set("GITHUB_STEP_SUMMARY", summary);
        using var cwd = new CwdScope(dir.Path);
        Assert.Null(ParityConfig.FindConfigFile(dir.Path)); // 前提:往上找不到任何 config

        var r = await Cli.RunAsync("push", "--server", server.Url, "--in", reportPath);

        Assert.Equal(0, r.ExitCode);
        Assert.Null(Assert.Single(server.Requests).Header("X-Parity-Gate"));
        Assert.Equal($"**Parity** — [demo 0/100 ❌]({server.Url}/runs/r7)\n", File.ReadAllText(summary));
    }

    [Fact]
    public async Task Job_summary_shows_no_verdict_when_nobody_knows_it()
    {
        // CLI 算不出、伺服器回應也沒帶 gateFailed(較舊的伺服器)→ 不要假裝通過
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.Serious));
        var summary = dir.Write("summary.md", "");
        await using var server = await FakeServer.StartAsync(201, """{ "project": "demo", "score": 0, "url": "/runs/r8" }""");
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij").Set("GITHUB_STEP_SUMMARY", summary);
        using var cwd = new CwdScope(dir.Path);

        var r = await Cli.RunAsync("push", "--server", server.Url, "--in", reportPath);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal($"**Parity** — [demo 0/100]({server.Url}/runs/r8)\n", File.ReadAllText(summary));
    }

    [Fact]
    public async Task Server_url_with_a_base_path_keeps_the_path()
    {
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.None));
        await using var server = await FakeServer.StartAsync(201, """{ "url": "/runs/x" }""");
        using var env = CleanEnv().Set("PARITY_SERVER", server.Url + "/parity/").Set("PARITY_TOKEN", "tok-0123456789abcdefghij");

        var r = await Cli.RunAsync("push", "--in", reportPath, "--gate-exit", "1"); // --server 走 PARITY_SERVER

        Assert.Equal(0, r.ExitCode);
        var req = Assert.Single(server.Requests);
        Assert.Equal("/parity/api/runs", req.Path);
        Assert.Equal("fail", req.Header("X-Parity-Gate"));
    }

    [Fact]
    public async Task Default_report_location_is_next_to_the_config()
    {
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.None), Path.Combine(".parity", "report.json"));
        var config = dir.Write("parity.config.json", "{ }");
        await using var server = await FakeServer.StartAsync(201, """{ "url": "/runs/x" }""");
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij");

        var r = await Cli.RunAsync("push", "--server", server.Url, "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(File.ReadAllText(reportPath), Assert.Single(server.Requests).Body);
    }

    [Theory]
    [InlineData(401, """{ "error": "missing or unknown token" }""", "missing or unknown token", "the token was not accepted")]
    [InlineData(413, "", "413", "larger than the server accepts")]
    [InlineData(422, """{ "error": "report schemaVersion 99 is newer" }""", "report schemaVersion 99 is newer", "422")]
    [InlineData(400, """{ "error": "body is not a Parity report" }""", "body is not a Parity report", "400 BadRequest")]
    [InlineData(500, """{ "detail": "boom" }""", """{ "detail": "boom" }""", "500 InternalServerError")] // 沒有 error 欄位 → 原文照列
    public async Task Rejections_surface_the_status_and_the_servers_reason(
        int status, string body, string expected1, string expected2)
    {
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.None));
        var summary = dir.Write("summary.md", "");
        await using var server = await FakeServer.StartAsync(status, body);
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij").Set("GITHUB_STEP_SUMMARY", summary);

        var r = await Cli.RunAsync("push", "--server", server.Url, "--in", reportPath, "--gate-exit", "0");

        Assert.Equal(2, r.ExitCode); // push 永遠不回 1 或 3
        Assert.Contains($"server returned {status} ", r.Err);
        Assert.Contains(expected1, r.Err);
        Assert.Contains(expected2, r.Err);
        Assert.Equal("", File.ReadAllText(summary)); // 失敗不寫 job summary
    }

    [Fact]
    public async Task Non_json_error_bodies_are_truncated_to_300_chars()
    {
        using var dir = new TempDir();
        var reportPath = WriteReport(dir, Report(Severity.None));
        await using var server = await FakeServer.StartAsync(502, new string('x', 1000));
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PushCommand.RunAsync(["--server", server.Url, "--in", reportPath, "--gate-exit", "0"]));

        Assert.Contains("server returned 502 BadGateway: " + new string('x', 300), ex.Message);
        Assert.DoesNotContain(new string('x', 301), ex.Message);
    }

    // ---------- 送出前就擋下(不浪費往返) ----------

    [Fact]
    public async Task Missing_server_is_an_error_that_names_both_ways_to_set_it()
    {
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PushCommand.RunAsync([]));
        Assert.Contains("missing server", ex.Message);
        Assert.Contains("PARITY_SERVER", ex.Message);
    }

    [Fact]
    public async Task Missing_token_is_an_error_that_points_at_the_env_var()
    {
        using var env = CleanEnv();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PushCommand.RunAsync(["--server", "http://127.0.0.1:9"]));
        Assert.Contains("missing token", ex.Message);
        Assert.Contains("PARITY_TOKEN", ex.Message);
    }

    [Fact]
    public async Task Missing_report_and_broken_envelopes_never_reach_the_server()
    {
        using var dir = new TempDir();
        await using var server = await FakeServer.StartAsync(201, "{}");
        using var env = CleanEnv().Set("PARITY_TOKEN", "tok-0123456789abcdefghij");

        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            PushCommand.RunAsync(["--server", server.Url, "--in", dir.File("nope.json")]));
        Assert.Contains("report not found", missing.Message);

        var broken = dir.Write("broken.json", """{ "schemaVersion": 1 }""");
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PushCommand.RunAsync(["--server", server.Url, "--in", broken]));
        Assert.Contains("is not a Parity report", bad.Message);

        Assert.Empty(server.Requests);
    }

    /// <summary>只收不判的假 Parity.Server:記下每個請求,回固定的狀態碼與 body。</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        public sealed record Captured(string Method, string Path, Dictionary<string, string> Headers, string Body)
        {
            public string? Header(string name) => Headers.GetValueOrDefault(name);
        }

        private readonly WebApplication _app;
        private readonly ConcurrentQueue<Captured> _requests = new();

        private FakeServer(WebApplication app) => _app = app;

        public string Url => _app.Urls.First().TrimEnd('/');

        public IReadOnlyList<Captured> Requests => [.. _requests];

        public static async Task<FakeServer> StartAsync(int status, string responseBody)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeServer(app);
            app.Run(async ctx =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                fake._requests.Enqueue(new Captured(ctx.Request.Method, ctx.Request.Path,
                    ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    body));
                ctx.Response.StatusCode = status;
                if (responseBody.Length > 0)
                {
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(responseBody);
                }
            });
            await app.StartAsync();
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
