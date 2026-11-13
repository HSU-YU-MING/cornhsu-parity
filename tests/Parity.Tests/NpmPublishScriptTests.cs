using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Parity.Tests;

/// <summary>
/// npm 通路的發版順序(scripts/publish-npm-packages.sh):平台子套件先發、等壓縮檔真的下載得到、入口套件最後發。
/// 子套件是選用相依,入口套件先到的話 npm 不報錯、默默跳過,裝完一執行才 exit 2,npx 還會把半成品留在快取。
/// 2.3.1 的 win32-x64 就有一小時裝不起來。腳本用真的 bash、node、curl 跑,registry 是本機假站,npm 是假指令。
/// </summary>
public class NpmPublishScriptTests
{
    private const string Version = "2.3.1";
    private const string Windows = "@cornhsu/parity-win32-x64";
    private const string Linux = "@cornhsu/parity-linux-x64";

    /// <summary>本機的假 registry:可以分別決定清單裡有沒有這一版、壓縮檔下載不下載得到。</summary>
    private sealed class Registry : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public ConcurrentDictionary<string, bool> Listed { get; } = new();
        public ConcurrentDictionary<string, int> TarballStatus { get; } = new();
        public ConcurrentQueue<string> Tarballs { get; } = new();
        public ConcurrentQueue<(string Path, string Accept)> Manifests { get; } = new();

        private Registry(WebApplication app) => _app = app;

        public string Url => _app.Urls.First().TrimEnd('/');

        public static async Task<Registry> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var registry = new Registry(app);
            app.Run(async ctx =>
            {
                var raw = ctx.Request.Path.Value ?? "";
                if (raw.EndsWith(".tgz", StringComparison.Ordinal))
                {
                    var name = raw[1..raw.IndexOf("/-/", StringComparison.Ordinal)];
                    registry.Tarballs.Enqueue(name);
                    ctx.Response.StatusCode = registry.TarballStatus.GetValueOrDefault(name, StatusCodes.Status404NotFound);
                    return;
                }
                var package = Uri.UnescapeDataString(raw.TrimStart('/'));
                registry.Manifests.Enqueue((raw, ctx.Request.Headers.Accept.ToString()));
                var versions = new Dictionary<string, object>();
                if (registry.Listed.GetValueOrDefault(package))
                    versions[Version] = new { dist = new { tarball = $"{registry.Url}/{package}/-/{package.Split('/')[^1]}-{Version}.tgz" } };
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { name = package, versions }));
            });
            await app.StartAsync();
            return registry;
        }

        public void Ready(params string[] packages)
        {
            foreach (var p in packages)
            {
                Listed[p] = true;
                TarballStatus[p] = StatusCodes.Status206PartialContent;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>npm/dist 的樣子 + 假的 npm 指令(把每次呼叫記下來)。</summary>
    private sealed class Workspace : IDisposable
    {
        private readonly TempDir _dir = new();

        public Workspace(params string[] missingDirs)
        {
            var optional = new[] { Windows, Linux }.ToDictionary(n => n, _ => Version);
            _dir.Write(Path.Combine("dist", "cornhsu-parity", "package.json"),
                JsonSerializer.Serialize(new { name = "cornhsu-parity", version = Version, optionalDependencies = optional }));
            foreach (var name in optional.Keys.Where(n => !missingDirs.Contains(n)))
                _dir.Write(Path.Combine("dist", name.Split('/')[^1], "package.json"),
                    JsonSerializer.Serialize(new { name, version = Version }));

            // 測試用的 LF 複本:Windows 的工作目錄是 CRLF,Git Bash 跑不了
            _dir.Write("publish.sh", Repo.Read("scripts", "publish-npm-packages.sh").Replace("\r\n", "\n"));
            var fakeNpm = _dir.Write(Path.Combine("bin", "npm"), """
                #!/usr/bin/env bash
                echo "$*" >> "$FAKE_NPM_LOG"
                case "$1" in
                  view) grep -qxF "$2" "$FAKE_NPM_EXISTING" ;;
                  publish)
                    if grep -qF "${2##*/}" "$FAKE_NPM_PREVIOUSLY"; then
                      echo "npm error 403 You cannot publish over the previously published versions: 2.3.1." >&2
                      exit 1
                    fi
                    if grep -qF "${2##*/}" "$FAKE_NPM_BROKEN"; then
                      echo "npm error code ENEEDAUTH" >&2
                      exit 1
                    fi ;;
                esac
                """.Replace("\r\n", "\n") + "\n");
            // Linux 與 macOS 要有執行權限才叫得到;Windows 上 Git Bash 看 shebang
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fakeNpm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _dir.Write("npm.log", "");
            _dir.Write("existing.txt", "");
            _dir.Write("previously.txt", "");
            _dir.Write("broken.txt", "");
        }

        /// <summary>npm 指令逐行紀錄裡,實際發佈了哪些資料夾(依序)。</summary>
        public List<string> Published => [.. File.ReadAllLines(_dir.File("npm.log"))
            .Where(l => l.StartsWith("publish ", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, @"dist/(?<dir>[^ ]+) ").Groups["dir"].Value)];

        public void Existing(params string[] specs) => File.WriteAllText(_dir.File("existing.txt"), string.Join('\n', specs) + "\n");

        public void PreviouslyPublished(string dir) => File.WriteAllText(_dir.File("previously.txt"), dir + "\n");

        public void Broken(string dir) => File.WriteAllText(_dir.File("broken.txt"), dir + "\n");

        public (int ExitCode, string Out, string Err) Run(string phase, Registry registry, int settleSec = 0)
        {
            var psi = new ProcessStartInfo(ActionYmlTests.BashPath())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, // 腳本輸出是 UTF-8;Windows 的預設編碼會把中文變亂碼
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _dir.Path,
            };
            psi.ArgumentList.Add(Forward(_dir.File("publish.sh")));
            psi.ArgumentList.Add(phase);
            psi.ArgumentList.Add(Forward(_dir.File("dist")));
            psi.Environment["PATH"] = _dir.File("bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            psi.Environment["NPM_REGISTRY"] = registry.Url;
            psi.Environment["WAIT_TIMEOUT_MIN"] = "0"; // 第一輪還沒好就逾時:測試不等
            psi.Environment["WAIT_INTERVAL_SEC"] = "1";
            psi.Environment["WAIT_SETTLE_SEC"] = settleSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.Environment["FAKE_NPM_LOG"] = Forward(_dir.File("npm.log"));
            psi.Environment["FAKE_NPM_EXISTING"] = Forward(_dir.File("existing.txt"));
            psi.Environment["FAKE_NPM_PREVIOUSLY"] = Forward(_dir.File("previously.txt"));
            psi.Environment["FAKE_NPM_BROKEN"] = Forward(_dir.File("broken.txt"));

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(60_000), "腳本沒有在 60 秒內結束");
            return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }

        private static string Forward(string path) => path.Replace('\\', '/');

        public void Dispose() => _dir.Dispose();
    }

    [Fact]
    public async Task Wait_passes_when_every_tarball_can_be_downloaded()
    {
        await using var registry = await Registry.StartAsync();
        registry.Ready(Windows, Linux);
        using var work = new Workspace();

        var r = work.Run("wait", registry);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("2 個平台子套件", r.Out);
        // 看的是 npm install 實際讀的那份清單,名稱要編碼
        Assert.Contains(registry.Manifests, m => m.Path.Contains("%2f", StringComparison.OrdinalIgnoreCase) && m.Accept.Contains("install-v1", StringComparison.Ordinal));
        Assert.Equal(1, registry.Tarballs.Count(t => t == Windows));
    }

    [Fact]
    public async Task A_listed_version_whose_tarball_is_missing_is_not_ready()
    {
        // 2.3.1 實際的狀況:清單與 /2.3.1 都回 200,壓縮檔還 404,npm install 默默跳過。這條紅了就回到那個空窗
        await using var registry = await Registry.StartAsync();
        registry.Ready(Linux);
        registry.Listed[Windows] = true;
        using var work = new Workspace();

        var r = work.Run("wait", registry);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(Windows, r.Err);
        Assert.DoesNotContain(Linux, r.Err);
        Assert.Contains("使用者仍然裝到上一版", r.Err);
    }

    [Fact]
    public async Task A_version_that_is_not_listed_yet_is_not_ready()
    {
        await using var registry = await Registry.StartAsync();
        registry.Ready(Windows);
        using var work = new Workspace();

        var r = work.Run("wait", registry);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(Linux, r.Err);
        Assert.DoesNotContain(registry.Tarballs, t => t == Linux); // 清單沒有就不必試壓縮檔
    }

    [Fact]
    public async Task Wait_checks_twice_when_a_settling_time_is_set()
    {
        // npm 的 CDN 各地不一定同步:第一次全部好了不算數
        await using var registry = await Registry.StartAsync();
        registry.Ready(Windows, Linux);
        using var work = new Workspace();

        var r = work.Run("wait", registry, settleSec: 1);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(2, registry.Tarballs.Count(t => t == Windows));
    }

    [Fact]
    public async Task The_entry_package_is_not_published_until_every_tarball_can_be_downloaded()
    {
        await using var registry = await Registry.StartAsync();
        registry.Ready(Linux);
        registry.Listed[Windows] = true;
        using var work = new Workspace();

        var blocked = work.Run("entry", registry);

        Assert.Equal(1, blocked.ExitCode);
        Assert.Empty(work.Published);

        registry.Ready(Windows);
        var allowed = work.Run("entry", registry);

        Assert.Equal(0, allowed.ExitCode);
        Assert.Equal(["cornhsu-parity"], work.Published);
    }

    [Fact]
    public async Task Platform_publishes_every_platform_package_but_never_the_entry()
    {
        await using var registry = await Registry.StartAsync();
        using var work = new Workspace();

        var r = work.Run("platform", registry);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["parity-linux-x64", "parity-win32-x64"], work.Published.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_missing_platform_folder_publishes_nothing()
    {
        await using var registry = await Registry.StartAsync();
        using var work = new Workspace(Windows);

        var r = work.Run("platform", registry);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains(Windows, r.Err);
        Assert.Empty(work.Published);
    }

    [Fact]
    public async Task Rerunning_skips_what_is_already_published()
    {
        await using var registry = await Registry.StartAsync();
        using var work = new Workspace();
        work.Existing($"{Windows}@{Version}");

        var r = work.Run("platform", registry);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["parity-linux-x64"], work.Published);
        Assert.Contains("已存在", r.Out);
    }

    [Fact]
    public async Task Rerunning_while_the_registry_is_still_syncing_does_not_fail()
    {
        // 逾時之後的重跑多半還在同步:npm view 查不到,publish 卻說已經發過。這不算失敗,否則重跑永遠過不了
        await using var registry = await Registry.StartAsync();
        using var work = new Workspace();
        work.PreviouslyPublished("parity-win32-x64");

        var r = work.Run("platform", registry);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("已發佈過", r.Out);
    }

    [Fact]
    public async Task Any_other_publish_failure_still_fails_and_shows_why()
    {
        await using var registry = await Registry.StartAsync();
        using var work = new Workspace();
        work.Broken("parity-linux-x64");

        var r = work.Run("platform", registry);

        Assert.Equal(1, r.ExitCode);
        Assert.Contains("ENEEDAUTH", r.Err);
    }

    [Fact]
    public async Task One_broken_platform_package_does_not_stop_the_rest()
    {
        // 壞掉的是排在**前面**那一包。以前 set -e 會讓迴圈當場中止,排在後面的根本沒機會發,
        // 於是一次失敗要重跑很多趟才推得完(PolyMigrate 2.1.0 的教訓)。
        // 現在後面的照樣試,但整個階段仍然失敗 —— 入口套件絕不能在平台包不齊的情況下發出去。
        await using var registry = await Registry.StartAsync();
        using var work = new Workspace();
        work.Broken("parity-win32-x64");

        var r = work.Run("platform", registry);

        // Published 記的是「npm publish 被呼叫過哪些資料夾」,不是哪些成功。
        // 兩個都在,正是「第一個失敗之後迴圈沒有中止」的證據——舊行為只會有 win32-x64 一個。
        Assert.Equal(["parity-win32-x64", "parity-linux-x64"], work.Published);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("ENEEDAUTH", r.Err);
        Assert.Contains("parity-win32-x64", r.Err);          // 失敗清單要指名是哪一包
        Assert.DoesNotContain("cornhsu-parity", work.Published);   // 入口套件連試都不該試
    }

    [Fact]
    public void The_token_guard_runs_before_anything_is_published()
    {
        // npm token 與 provenance 互斥:走 token 認證時憑證申請會 403
        // (CA_CREATE_SIGNING_CERTIFICATE_ERROR,PolyMigrate 2.1.0 實際踩到)。更麻煩的是
        // NPM_TOKEN 這類 secret 平常休眠,一旦有人加了同名的,npm 就安靜地繞過信任發布。
        // 守門排在三個階段之前,失敗時一包都還沒出去。
        var release = Repo.Read(".github", "workflows", "release.yml");

        var guard = release.IndexOf("Refuse to publish with an npm token", StringComparison.Ordinal);
        var first = release.IndexOf("publish-npm-packages.sh platform", StringComparison.Ordinal);

        Assert.True(guard >= 0, "release.yml 少了 npm token 守門");
        Assert.True(guard < first, "守門要排在第一個發佈階段之前");
    }

    [Fact]
    public void Release_publishes_platforms_then_waits_then_publishes_the_entry()
    {
        var release = Repo.Read(".github", "workflows", "release.yml").Replace("\r\n", "\n");
        var positions = new[]
        {
            "node npm/prepare.mjs",
            "publish-npm-packages.sh platform npm/dist",
            "publish-npm-packages.sh wait npm/dist",
            "publish-npm-packages.sh entry npm/dist",
        }.Select(step => (step, at: release.IndexOf(step, StringComparison.Ordinal))).ToList();

        Assert.All(positions, p => Assert.True(p.at >= 0, $"release.yml 找不到 {p.step}"));
        Assert.Equal(positions.Select(p => p.at).Order(), positions.Select(p => p.at));
        // 一口氣發完就回到空窗:子套件還沒同步,入口套件就上架了
        Assert.DoesNotContain("npm publish", release);
    }

    [Fact]
    public void The_step_timeout_is_longer_than_the_scripts_own_timeout()
    {
        // GitHub 先砍掉的話,看不到是哪幾個平台還沒好
        var release = Repo.Read(".github", "workflows", "release.yml").Replace("\r\n", "\n");
        var script = Repo.Read("scripts", "publish-npm-packages.sh").Replace("\r\n", "\n");

        var own = int.Parse(Regex.Match(script, @"WAIT_TIMEOUT_MIN:-(?<min>\d+)").Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var settle = int.Parse(Regex.Match(script, @"WAIT_SETTLE_SEC:-(?<sec>\d+)").Groups["sec"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var step = Regex.Match(release, @"timeout-minutes: (?<min>\d+)\n\s+run: bash scripts/publish-npm-packages\.sh wait");
        Assert.True(step.Success, "release.yml 的等待步驟要有 timeout-minutes");

        Assert.True(int.Parse(step.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture) > own + settle / 60);
    }
}
