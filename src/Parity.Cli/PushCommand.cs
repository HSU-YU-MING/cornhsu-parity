using System.Text;
using System.Text.Json;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>
/// parity push:把既有的 report.json 送上 Parity.Server(網頁外殼規畫書 5.1)。
/// 不重掃、不碰瀏覽器——它只是把 check 已經產出的東西搬上去;掃描永遠發生在本機/CI
/// (伺服器端的鐵則「雲端不跑瀏覽器」在 CLI 這側的對應就是:push 只認檔案)。
/// token 走 PARITY_TOKEN 環境變數(CI secret),--token 旗標僅供本機試用——
/// 旗標值會進 shell 歷史與 CI log,正式流程不要用。
/// </summary>
internal static class PushCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = CliOptions.Parse(args,
            "--config=", "--in=", "--server=", "--token=", "--commit=", "--branch=", "--gate-exit=");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Push);

        var server = opts.GetValueOrDefault("--server")
            ?? Environment.GetEnvironmentVariable("PARITY_SERVER")
            ?? throw new InvalidOperationException(
                "missing server: pass --server <url> (or set the PARITY_SERVER environment variable).");
        var token = opts.GetValueOrDefault("--token")
            ?? Environment.GetEnvironmentVariable("PARITY_TOKEN")
            ?? throw new InvalidOperationException(
                "missing token: set the PARITY_TOKEN environment variable (a project token from the server's " +
                "create-project command). Prefer the env var over --token — flags end up in shell history and CI logs.");

        var reportPath = ResolveReportPath(opts);
        if (!File.Exists(reportPath))
            throw new FileNotFoundException(
                $"report not found: {reportPath} (run `parity check` first, or point --in at a report.json).", reportPath);

        // 送出前先驗信封:壞檔案在本機就講清楚,不用浪費一趟往返讓伺服器回 400
        var raw = await File.ReadAllTextAsync(reportPath);
        var doc = ValidateEnvelope(raw, reportPath);

        // gate 結果由 push 端算好帶上去,伺服器不重判——否則使用者自訂 failOn 或 baseline 模式時,
        // 會出現「CI 綠燈、儀表板紅字」的口徑分裂。優先序:
        //   --gate-exit(CI/Action 傳入 check 的真實 exit code,含 baseline 語意)
        //   → 依 config 的 failOn 重算(與非 baseline 的 check 同口徑)
        var gateFailed = ResolveGate(opts, doc);

        // commit/branch:旗標 → CI 環境變數(GitHub Actions)→ 不帶(伺服器欄位可空)
        var commit = opts.GetValueOrDefault("--commit") ?? Environment.GetEnvironmentVariable("GITHUB_SHA");
        var branch = opts.GetValueOrDefault("--branch")
            ?? Environment.GetEnvironmentVariable("GITHUB_HEAD_REF") // PR 事件:來源分支
            ?? Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
        var actor = Environment.GetEnvironmentVariable("GITHUB_ACTOR");
        var repoUrl = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL") is { } srv
            && Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") is { } repo
            ? $"{srv.TrimEnd('/')}/{repo}" : null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(server.TrimEnd('/') + "/"), "api/runs"))
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (commit is not null) request.Headers.Add("X-Parity-Commit", commit);
        if (branch is not null) request.Headers.Add("X-Parity-Branch", branch);
        if (actor is not null) request.Headers.Add("X-Parity-Triggered-By", actor);
        if (repoUrl is not null) request.Headers.Add("X-Parity-Repo-Url", repoUrl);
        if (gateFailed is not null) request.Headers.Add("X-Parity-Gate", gateFailed.Value ? "fail" : "pass");

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            var ack = JsonSerializer.Deserialize<JsonElement>(body);
            var viewUrl = $"{server.TrimEnd('/')}{Str(ack, "url")}";
            Console.WriteLine($"pushed: {reportPath}");
            Console.WriteLine($"  project  {Str(ack, "project")}");
            Console.WriteLine($"  score    {Str(ack, "score")}/100");
            Console.WriteLine($"  view     {viewUrl}");

            // GitHub Actions:把儀表板連結寫進 job summary——紅燈 PR 到「PM 看得懂的那頁」
            // 之間不再靠人肉找(M4.5 必補 #4)
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { } summaryPath)
                await File.AppendAllTextAsync(summaryPath,
                    $"**Parity** — [{Str(ack, "project")} {Str(ack, "score")}/100 " +
                    $"{(gateFailed == true ? "❌" : "✅")}]({viewUrl})\n");
            return 0;
        }

        var hint = (int)response.StatusCode switch
        {
            401 => "the token was not accepted — check PARITY_TOKEN against the server's project token.",
            422 => "", // 伺服器的 schema 版本訊息本身已講清楚
            413 => "the report is larger than the server accepts.",
            _ => "",
        };
        throw new InvalidOperationException(
            $"server returned {(int)response.StatusCode} {response.StatusCode}: {ExtractError(body)} {hint}".TrimEnd());
    }

    /// <summary>--in → config 旁的 .parity/report.json(check 的預設落點)→ cwd 的 .parity/report.json。</summary>
    private static string ResolveReportPath(Dictionary<string, string?> opts)
    {
        if (opts.GetValueOrDefault("--in") is { } given) return Path.GetFullPath(given);
        var configPath = opts.GetValueOrDefault("--config") ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory());
        var baseDir = configPath is not null ? Path.GetDirectoryName(Path.GetFullPath(configPath))! : ".";
        return Path.GetFullPath(Path.Combine(baseDir, ".parity", "report.json"));
    }

    /// <summary>本機端的信封檢查:是報告、且版本不比 CLI 自己新(那種檔案這顆 CLI 根本讀不懂)。</summary>
    internal static ReportDocument ValidateEnvelope(string raw, string path)
    {
        ReportDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ReportDocument>(raw, ReportWire.Compact);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid report JSON: {ex.Message}");
        }
        if (doc?.Reports is null)
            throw new InvalidOperationException(
                $"{path} is not a Parity report (expected the {{ schemaVersion, reports }} envelope that `parity check` writes).");
        return doc;
    }

    /// <summary>gate 結果:--gate-exit(真實 exit code,含 baseline 語意)優先,否則照 config 的 failOn 重算。</summary>
    internal static bool? ResolveGate(Dictionary<string, string?> opts, ReportDocument doc)
    {
        if (opts.GetValueOrDefault("--gate-exit") is { } exitStr)
        {
            if (!int.TryParse(exitStr, out var code))
                throw new InvalidOperationException($"--gate-exit expects the numeric exit code of `parity check` (got: {exitStr}).");
            return code != 0; // 0 = pass;1(落差)/3(可信度)都算紅
        }
        var configPath = opts.GetValueOrDefault("--config") ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory());
        if (configPath is null) return null; // 沒 config 可依 → 不帶,讓伺服器用預設口徑
        var config = ParityConfig.Load(configPath);
        return config.GateFailReasons(doc.Reports).Count > 0
            || config.MatchIntegrityFailures(doc.Reports).Count > 0;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) ? v.ToString() : "?";

    private static string ExtractError(string body)
    {
        try
        {
            var e = JsonSerializer.Deserialize<JsonElement>(body);
            if (e.TryGetProperty("error", out var msg)) return msg.GetString() ?? body;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}
