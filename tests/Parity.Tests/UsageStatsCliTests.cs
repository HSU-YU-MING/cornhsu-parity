using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// 匿名統計在**指令層**的行為(2.1.0):對離線示範實跑 check,看送出的內容逐欄符合協定 §7.2、
/// --stats-preview 什麼都不送也什麼都不改、收集站壞掉時 exit code 與 stdout 一個字都不變、
/// --reverse 與 exit 2 不送、其他指令不送。收集站一律是本機假站(CORNHSU_STATS_URL);開發版本來就不送,
/// 設了 CORNHSU_STATS_URL 才會送(協定 §3 第 5 條的例外),正好拿來測。
/// 需要真瀏覽器的幾條沒裝 Chromium 時印出「略過」並結束(同 CliBrowserTests)。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class UsageStatsCliTests
{
    private const string Skipped = "略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器";

    /// <summary>離線示範的乾淨複本(只有進版控的檔案,與 CI 同:沒有 parity.map.json,11/12 配對)。</summary>
    private static string DemoCopy(TempDir dir)
    {
        foreach (var f in new[] { "design.json", "index.html", "parity.config.json", "tokens.json" })
            File.Copy(Repo.PathOf("samples", "demo", f), dir.File(f));
        return dir.File("parity.config.json");
    }

    /// <summary>統計打開、送往假站:清掉所有統計相關的變數(含 DO_NOT_TRACK 與 CI),標記檔放進這條測試自己的目錄。</summary>
    private sealed class StatsOn : IDisposable
    {
        private readonly EnvScope _env;
        private readonly string? _previousNoticeDirectory = StatsEnvironment.NoticeDirectoryForTests;

        public StatsOn(string collectorUrl, string noticeDirectory)
        {
            _env = new EnvScope().Clear(EnvScope.StatsVariables).Set("CORNHSU_STATS_URL", collectorUrl);
            StatsEnvironment.NoticeDirectoryForTests = noticeDirectory;
        }

        public void Dispose()
        {
            StatsEnvironment.NoticeDirectoryForTests = _previousNoticeDirectory;
            _env.Dispose();
        }
    }

    /// <summary>--stats-preview 的 stderr:第一行說明、中間是 JSON、最後一行是會不會送。</summary>
    private static (JsonDocument Json, string Verdict) ParsePreview(string stderr)
    {
        var lines = stderr.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("--stats-preview: nothing is sent.", StringComparison.Ordinal));
        Assert.True(start >= 0, $"stderr 裡沒有預覽:\n{stderr}");
        return (JsonDocument.Parse(string.Join("\n", lines[(start + 1)..^1])), lines[^1]);
    }

    // ---------- 協定 §7.2 的獨立核算(不經 UsageStats,直接從 report.json 算)----------

    private static readonly double[] Length = [1, 2, 4, 8, 16, 32];
    private static readonly double[] Color = [1, 2, 5, 10, 20];
    private static readonly double[] Weight = [100, 200, 300];

    private static readonly Dictionary<string, double[]?> Families = new(StringComparer.Ordinal)
    {
        ["offsetX"] = Length,
        ["offsetY"] = Length,
        ["width"] = Length,
        ["height"] = Length,
        ["paddingTop"] = Length,
        ["paddingRight"] = Length,
        ["paddingBottom"] = Length,
        ["paddingLeft"] = Length,
        ["itemSpacing"] = Length,
        ["cornerRadius"] = Length,
        ["fontSize"] = Length,
        ["lineHeight"] = Length,
        ["letterSpacing"] = Length,
        ["color"] = Color,
        ["background"] = Color,
        ["fontWeight"] = Weight,
        ["fontFamily"] = null,
    };

    /// <summary>把送出的 data 與這次的 report.json、exit code、stdout 的分數逐欄對照。</summary>
    private static void AssertDataMatchesTheReport(JsonElement data, string reportPath, int exitCode, string stdout, string designSource)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        var reports = doc.RootElement.GetProperty("reports").EnumerateArray().ToList();
        int Sum(string field) => reports.Sum(r => r.GetProperty("summary").GetProperty(field).GetInt32());

        var score = Regex.Match(stdout, @"fidelity score: (?:\x1b\[[0-9;]*m)?(?<n>\d+)/100");
        Assert.True(score.Success, "stdout 裡找不到 fidelity score");
        Assert.Equal(int.Parse(score.Groups["n"].Value), data.GetProperty("score").GetInt32());
        Assert.Equal(exitCode != 0, data.GetProperty("gateFailed").GetBoolean());
        Assert.Equal(reports.Count, data.GetProperty("pages").GetInt32());
        Assert.Equal(designSource, data.GetProperty("designSource").GetString());
        Assert.Equal(Sum("designNodes"), data.GetProperty("designNodes").GetInt32());
        Assert.Equal(Sum("matched"), data.GetProperty("matched").GetInt32());
        foreach (var s in new[] { "critical", "serious", "medium", "minor" })
            Assert.Equal(Sum(s), data.GetProperty("severity").GetProperty(s).GetInt32());

        var expected = new Dictionary<string, (int Count, int Missing, int[] Buckets)>(StringComparer.Ordinal);
        foreach (var diff in reports.SelectMany(r => r.GetProperty("nodes").EnumerateArray())
                     .SelectMany(n => n.GetProperty("diffs").EnumerateArray()))
        {
            var prop = diff.GetProperty("prop").GetString()!;
            Assert.True(Families.TryGetValue(prop, out var edges), $"demo 產出了協定沒有的屬性 {prop}");
            var e = expected.TryGetValue(prop, out var have) ? have : (Count: 0, Missing: 0, Buckets: new int[edges is null ? 0 : edges.Length + 1]);
            e.Count++;
            if (diff.GetProperty("status").GetString() == "missing") e.Missing++;
            else if (diff.GetProperty("delta") is { ValueKind: JsonValueKind.Number } delta && edges is not null)
                e.Buckets[edges.Count(edge => Math.Abs(delta.GetDouble()) >= edge)]++;
            expected[prop] = e;
        }
        var props = data.GetProperty("props");
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), props.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var (prop, e) in expected)
        {
            var actual = props.GetProperty(prop);
            Assert.Equal(e.Count, actual.GetProperty("count").GetInt32());
            Assert.Equal(e.Missing, actual.GetProperty("missing").GetInt32());
            Assert.Equal(e.Buckets, actual.GetProperty("buckets").EnumerateArray().Select(b => b.GetInt32()));
        }
    }

    // ---------- 真瀏覽器 ----------

    [Fact]
    public async Task The_offline_demos_preview_matches_the_protocol_field_for_field_and_changes_nothing()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        var reportPath = dir.File(".parity", "report.json");

        var plain = await Cli.RunAsync("check", "--config", config); // 測試行程預設 DO_NOT_TRACK=1
        if (Cli.BrowserMissing(plain)) { Console.WriteLine(Skipped); return; }
        var plainReport = File.ReadAllText(reportPath);

        await using var stub = await StatsCollectorStub.StartAsync();
        Cli.Result preview;
        using (new StatsOn(stub.Url, dir.File("appdata")))
            preview = await Cli.RunAsync("check", "--config", config, "--stats-preview");

        // 預覽什麼都不送、什麼都不改:exit code、stdout、報告檔都跟一般的 check 一樣,也不寫標記、不印告知
        Assert.Empty(stub.Requests);
        Assert.Equal(1, plain.ExitCode);
        Assert.Equal(plain.ExitCode, preview.ExitCode);
        Assert.Equal(plain.Out, preview.Out);
        Assert.Equal(plainReport, File.ReadAllText(reportPath));
        Assert.False(Directory.Exists(dir.File("appdata", "cornhsu")));
        Assert.DoesNotContain("This notice", preview.Err);
        Assert.DoesNotContain("--stats-preview", preview.Out);

        var (json, verdict) = ParsePreview(preview.Err);
        using (json)
        {
            var root = json.RootElement;
            StatsProtocol.AssertMatches(root);
            Assert.Equal("0.0.0-dev", root.GetProperty("toolVersion").GetString()); // 開發版:沒有 build metadata
            Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other",
                root.GetProperty("os").GetString());
            Assert.False(root.GetProperty("ci").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("projectId").ValueKind);
            AssertDataMatchesTheReport(root.GetProperty("data"), reportPath, plain.ExitCode, plain.Out, "file");
            // ci.yml 斷言的那組數字(The_offline_demo_produces_exactly_the_counts_ci_asserts 守著),這裡直接看得見
            Assert.Equal(12, root.GetProperty("data").GetProperty("designNodes").GetInt32());
            Assert.Equal(11, root.GetProperty("data").GetProperty("matched").GetInt32());
        }
        Assert.Equal($"would be sent: POST {stub.Url}/v1/parity", verdict);
    }

    [Fact]
    public async Task A_check_sends_one_request_and_a_broken_collector_changes_neither_exit_code_nor_stdout()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        var reportPath = dir.File(".parity", "report.json");

        var clock = Stopwatch.StartNew();
        var plain = await Cli.RunAsync("check", "--config", config);
        var plainTime = clock.Elapsed;
        if (Cli.BrowserMissing(plain)) { Console.WriteLine(Skipped); return; }

        // 收集站正常:送一次,內容逐欄符合協定;本機第一次的說明印在 stderr,stdout 不變
        await using (var ok = await StatsCollectorStub.StartAsync())
        {
            Cli.Result sent;
            using (new StatsOn(ok.Url, dir.File("appdata")))
                sent = await Cli.RunAsync("check", "--config", config);

            Assert.Equal(plain.ExitCode, sent.ExitCode);
            Assert.Equal(plain.Out, sent.Out);
            var req = Assert.Single(ok.Requests);
            Assert.Equal("/v1/parity", req.Path);
            Assert.Null(req.Header("User-Agent"));
            using var body = JsonDocument.Parse(req.Body);
            StatsProtocol.AssertMatches(body.RootElement);
            AssertDataMatchesTheReport(body.RootElement.GetProperty("data"), reportPath, sent.ExitCode, sent.Out, "file");
            Assert.Contains(UsageStats.FirstRunNotice.Replace("\r\n", "\n"), sent.Err.Replace("\r\n", "\n"));
            Assert.True(File.Exists(dir.File("appdata", "cornhsu", "parity-stats-notice")));
        }

        // 收集站壞掉(不回應、500):照樣 exit 1、stdout 一個字不差,什麼都不印;不回應最多多花約 3 秒
        foreach (var (mode, label) in new[] { (StatsCollectorStub.Mode.Hang, "hang"), (StatsCollectorStub.Mode.Status, "500") })
        {
            await using var broken = await StatsCollectorStub.StartAsync(mode, status: 500);
            Cli.Result r;
            using (new StatsOn(broken.Url, dir.File("appdata"))) // 標記已在:不再印說明
            {
                clock.Restart();
                r = await Cli.RunAsync("check", "--config", config);
            }

            Assert.Equal(plain.ExitCode, r.ExitCode);
            Assert.Equal(plain.Out, r.Out);
            Assert.Equal("", r.Err);
            Assert.Single(broken.Requests);
            Assert.True(clock.Elapsed < plainTime + TimeSpan.FromSeconds(6), $"{label}:{clock.Elapsed.TotalSeconds:0.0} 秒,一般的 check 是 {plainTime.TotalSeconds:0.0} 秒");
        }
    }

    [Fact]
    public async Task Reverse_never_sends_and_its_preview_says_so()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        await using var stub = await StatsCollectorStub.StartAsync();
        using var on = new StatsOn(stub.Url, dir.File("appdata"));

        var reverse = await Cli.RunAsync("check", "--config", config, "--reverse");
        if (Cli.BrowserMissing(reverse)) { Console.WriteLine(Skipped); return; }
        var preview = await Cli.RunAsync("check", "--config", config, "--reverse", "--stats-preview");

        Assert.Equal(0, reverse.ExitCode);
        Assert.Equal("", reverse.Err);
        Assert.Equal(0, preview.ExitCode);
        Assert.Equal(reverse.Out, preview.Out);
        Assert.Equal("--stats-preview: nothing is sent. Would not be sent: check --reverse never sends anonymous usage statistics.",
            preview.Err.Trim());
        Assert.Empty(stub.Requests);
    }

    // ---------- 不需要瀏覽器 ----------

    [Fact]
    public async Task A_check_that_ends_in_exit_2_sends_nothing_and_prints_no_preview()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        await using var stub = await StatsCollectorStub.StartAsync();
        using var on = new StatsOn(stub.Url, dir.File("appdata"));

        var noTarget = await Cli.RunAsync("check", "--config", config, "--target", "/nope", "--stats-preview");
        var badFlag = await Cli.RunAsync("check", "--config", config, "--bogus");
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"designFile\"", "\"stats\": { \"projectId\": \"too-short\" }, \"designFile\""));
        var badId = await Cli.RunAsync("check", "--config", config);

        Assert.Equal(2, noTarget.ExitCode);
        Assert.Contains("no target with route /nope", noTarget.Err);
        Assert.DoesNotContain("--stats-preview", noTarget.Err);
        Assert.Equal(2, badFlag.ExitCode);
        Assert.Equal(2, badId.ExitCode);
        Assert.Contains("stats.projectId must be 16–64 characters", badId.Err);
        Assert.Empty(stub.Requests);
        Assert.False(Directory.Exists(dir.File("appdata", "cornhsu")));
    }

    [Fact]
    public async Task Commands_other_than_check_send_nothing()
    {
        using var dir = new TempDir();
        var config = DemoCopy(dir);
        await using var stub = await StatsCollectorStub.StartAsync();
        using var on = new StatsOn(stub.Url, dir.File("appdata"));

        var lint = await Cli.RunAsync("lint", "--config", config);
        var help = await Cli.RunAsync("--help");
        var version = await Cli.RunAsync("version");
        var bugReport = await Cli.RunAsync("bug-report", "--config", config);

        Assert.Equal(1, lint.ExitCode); // demo 的設計稿有不在 token 集合裡的值
        Assert.Equal(0, help.ExitCode);
        Assert.Equal(0, version.ExitCode);
        Assert.Equal(0, bugReport.ExitCode);
        Assert.Empty(stub.Requests);
        Assert.DoesNotContain("anonymous usage statistics sent", lint.Err + help.Err + version.Err + bugReport.Err);
    }

    [Fact]
    public async Task Help_names_the_preview_flag_and_every_way_to_turn_statistics_off()
    {
        var r = await Cli.RunAsync("check", "--help");

        Assert.Contains("--stats-preview", r.Out);
        Assert.Contains("https://stats.cornhsu.com/", r.Out);
        foreach (var off in new[] { "\"stats\": { \"share\": false }", "PARITY_SHARE_STATS=false", "CORNHSU_SHARE_STATS=false", "DO_NOT_TRACK=1" })
            Assert.Contains(off, r.Out);
    }
}
