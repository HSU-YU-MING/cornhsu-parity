using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Parity.Tests;

/// <summary>
/// 需要真瀏覽器的**指令層**行為(引擎層的擷取測試見 SettleCaptureTests 等)。
/// 頁面全是暫存目錄裡的 file:// 檔,不連網。沒裝 Chromium 時印出「略過」並結束——
/// CI 的 build-test job 先跑 install-browser 再跑測試,所以在 CI 上一定會真的跑。
/// 刻意壓在少數幾條:每條都串起好幾個指令,一次驗完一整條使用者流程。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class CliBrowserTests
{
    private const string Skipped = "略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器";

    private static string Page(string body, string css = "") => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><style>body { margin: 0; } {{css}}</style></head>
        <body>{{body}}</body></html>
        """;

    private static void AssertPng(string path)
    {
        Assert.True(File.Exists(path), $"缺少截圖 {path}");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(path)[..4]);
    }

    private static JsonElement Root(string snapshotPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        return doc.RootElement.Clone();
    }

    private static (double W, double H) BoxSize(JsonElement node)
        => (node.GetProperty("box").GetProperty("width").GetDouble(), node.GetProperty("box").GetProperty("height").GetDouble());

    [Fact]
    public async Task Snapshot_viewport_defaults_screenshot_names_wrapper_backup_and_stabilize()
    {
        using var dir = new TempDir();
        dir.Write("a.html", Page("<h1>Page A</h1>"));
        // 每次載入文字都不同的區塊;id 以數字開頭,CSS.escape 會產生反斜線(#\33 d-banner)
        dir.Write("b.html", Page("""
            <h1>Page B</h1><div id="3d-banner"></div>
            <script>document.getElementById('3d-banner').textContent = Math.random().toString(36).slice(2);</script>
            """));
        var config = dir.Write("parity.config.json", """
            { "targets": [ { "route": "/a", "url": "./a.html" }, { "route": "/b", "url": "./b.html", "width": 375 } ] }
            """);
        var snapshot = dir.File("parity.snapshot.json");

        var first = await Cli.RunAsync("snapshot", "--config", config);
        if (Cli.BrowserMissing(first)) { Console.WriteLine(Skipped); return; }

        Assert.Equal(0, first.ExitCode);
        var doc = Root(snapshot);
        Assert.Equal(1, doc.GetProperty("schemaVersion").GetInt32());
        Assert.False(doc.TryGetProperty("stableIdAnchors", out _)); // 沒有隨機 id → 快照與 1.0 同形
        var root = doc.GetProperty("root");
        Assert.Equal("snapshot", root.GetProperty("id").GetString()); // 多 target:包一層,frame id = route
        var frames = root.GetProperty("children").EnumerateArray().ToList();
        Assert.Equal(["/a", "/b"], frames.Select(f => f.GetProperty("id").GetString()));
        Assert.Equal((1280, 800), BoxSize(frames[0]));  // 預設視窗(之後 check 照它開視窗)
        Assert.Equal((375, 800), BoxSize(frames[1]));   // target 自己的 width 優先,高度仍是預設
        AssertPng(dir.File("parity.snapshot.0.png"));
        AssertPng(dir.File("parity.snapshot.1.png"));
        Assert.False(File.Exists(dir.File("parity.snapshot.png")));
        Assert.False(File.Exists(dir.File(".parity", "snapshot.bak.json"))); // 第一次拍,沒有舊基準可備份

        var before = File.ReadAllText(snapshot);
        var second = await Cli.RunAsync("snapshot", "--config", config, "--target", "/a", "--width", "640", "--height", "480");

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(before, File.ReadAllText(dir.File(".parity", "snapshot.bak.json"))); // 覆寫前先備份
        var single = Root(snapshot).GetProperty("root");
        Assert.Equal("/a", single.GetProperty("id").GetString()); // 單 target:frame 直接當根
        Assert.Equal((640, 480), BoxSize(single));
        AssertPng(dir.File("parity.snapshot.png"));

        // --stabilize:連拍找出會變的區塊,給一行可以直接貼進 config 的 ignore 建議。
        // selector 帶反斜線,建議必須是合法 JSON(手拼字串的版本貼了就爆)
        var stabilize = await Cli.RunAsync("snapshot", "--config", config, "--target", "/b", "--stabilize");

        Assert.Equal(0, stabilize.ExitCode);
        Assert.Contains("/b: 1 region(s) changed across 3 captures", stabilize.Out);
        var suggestion = stabilize.Out.Split('\n').Single(l => l.TrimStart().StartsWith("\"ignore\": ["));
        using var parsed = JsonDocument.Parse("{" + suggestion.Trim() + "}");
        Assert.Equal(["#\\33 d-banner"], parsed.RootElement.GetProperty("ignore").EnumerateArray().Select(e => e.GetString()));
    }

    // ---------- check:一般 / --reverse / --baseline ----------

    private const string BoxCss = "#box { width: 200px; height: 100px; background: #336699; }";

    /// <summary>設計稿說 #box 寬 300,頁面做成 200 → 一條 critical 的 width 落差。</summary>
    private static string BoxProject(TempDir dir, string designId = "#box")
    {
        dir.Write("page.html", Page("<div id=\"box\"></div>", BoxCss));
        dir.Write("design.json", $$"""
            { "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 800, "height": 600 },
              "children": [{ "id": "{{designId}}", "name": "zz", "type": "frame",
                             "box": { "x": 0, "y": 0, "width": 300, "height": 100 },
                             "fill": { "r": 51, "g": 102, "b": 153, "a": 1 } }] }
            """);
        return dir.Write("parity.config.json",
            """{ "designFile": "design.json", "targets": [{ "route": "/", "frame": "", "url": "./page.html" }] }""");
    }

    private static List<JsonElement> Diffs(string reportPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        return doc.RootElement.GetProperty("reports")[0].GetProperty("nodes")[0].GetProperty("diffs")
            .EnumerateArray().Select(d => d.Clone()).ToList();
    }

    [Fact]
    public async Task Check_gates_on_the_diff_and_reverse_swaps_expected_and_actual_without_gating()
    {
        using var dir = new TempDir();
        var config = BoxProject(dir);

        var normal = await Cli.RunAsync("check", "--config", config);
        if (Cli.BrowserMissing(normal)) { Console.WriteLine(Skipped); return; }

        Assert.Equal(1, normal.ExitCode);
        Assert.Contains("GATE FAIL", normal.Out);
        var width = Assert.Single(Diffs(dir.File(".parity", "report.json")));
        Assert.Equal("width", width.GetProperty("prop").GetString());
        Assert.Equal("300", width.GetProperty("expected").GetString()); // 期望 = 設計稿
        Assert.Equal("200", width.GetProperty("actual").GetString());
        Assert.Equal("critical", width.GetProperty("severity").GetString());

        var reverse = await Cli.RunAsync("check", "--config", config, "--reverse",
            "--out", dir.File("rev", "report.json"), "--md", dir.File("rev", "report.md"));

        Assert.Equal(0, reverse.ExitCode); // 反向檢視永遠不把關
        var swapped = Assert.Single(Diffs(dir.File("rev", "report.json")));
        Assert.Equal("200", swapped.GetProperty("expected").GetString()); // 期望 = 現況(實作)
        Assert.Equal("300", swapped.GetProperty("actual").GetString());
        Assert.Equal("critical", swapped.GetProperty("severity").GetString()); // 嚴重度不受方向影響
        var md = File.ReadAllText(dir.File("rev", "report.md"));
        Assert.Contains("reverse mode", md);
        Assert.Contains("✅ PASS", md);
        Assert.Contains("`200` → `300`", md);
    }

    [Fact]
    public async Task Baseline_mode_falls_back_then_gates_only_new_or_worse_diffs()
    {
        using var dir = new TempDir();
        var config = BoxProject(dir);

        // 1) 還沒有 baseline:退回一般 gate(有 critical → 1),並提示要 commit db
        var fallback = await Cli.RunAsync("check", "--config", config, "--baseline");
        if (Cli.BrowserMissing(fallback)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(1, fallback.ExitCode);
        Assert.Contains("(no baseline yet)", fallback.Out);
        Assert.Contains("commit parity.baseline.db", fallback.Out);

        // 2) 存基準 → 同樣的落差不再擋
        var save = await Cli.RunAsync("baseline", "save", "--config", config);
        Assert.Equal(0, save.ExitCode);
        Assert.Contains("saved baseline #1: 1 diff(s)", save.Out);
        Assert.True(File.Exists(dir.File("parity.baseline.db")));

        var unchanged = await Cli.RunAsync("check", "--config", config, "--baseline");
        Assert.Equal(0, unchanged.ExitCode);
        Assert.Contains("no regressions vs baseline", unchanged.Out);
        Assert.Contains("unchanged 1", unchanged.Out);

        // 3) 頁面又壞一個屬性(高度)→ 只有新的那條讓 gate 紅
        dir.Write("page.html", Page("<div id=\"box\"></div>", BoxCss.Replace("height: 100px", "height: 60px")));
        var regressed = await Cli.RunAsync("check", "--config", config, "--baseline", "--md", dir.File("b.md"));
        Assert.Equal(1, regressed.ExitCode);
        Assert.Contains("new or worsened diffs vs baseline", regressed.Out);
        Assert.Contains("› height [critical]", regressed.Out);
        Assert.DoesNotContain("› width [", regressed.Out); // 基準裡已有的 width 不算新增
        Assert.Contains("🔴 new **1**", File.ReadAllText(dir.File("b.md")));
    }

    [Fact]
    public async Task Untrustworthy_matching_is_exit_3_and_is_never_saved_as_a_baseline()
    {
        using var dir = new TempDir();
        var config = BoxProject(dir, designId: "#does-not-exist"); // 0/1 配對:url/frame 指錯的典型樣子

        var check = await Cli.RunAsync("check", "--config", config);
        if (Cli.BrowserMissing(check)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(3, check.ExitCode);
        Assert.Contains("MATCH INTEGRITY TOO LOW", check.Out);

        // baseline 模式也不豁免:殘缺的現況拿去比基準,會把一切誤判成「修好」
        await using (var store = new Parity.Storage.BaselineStore(dir.File("parity.baseline.db")))
            await store.SaveAsync([new Parity.Engine.DiffRecord("/", "zz", "#box", "width", Parity.Engine.Severity.Critical, "300", "200")],
                new DateTime(2026, 7, 1));
        var baseline = await Cli.RunAsync("check", "--config", config, "--baseline");
        Assert.Equal(3, baseline.ExitCode);
        Assert.Contains("skipping baseline comparison", baseline.Out);

        var save = await Cli.RunAsync("baseline", "save", "--config", config);
        Assert.Equal(2, save.ExitCode); // 既成事實:baseline save 拒存回 2(不是 3),見盤點 3.2
        Assert.Contains("refusing to save baseline", save.Err);
        await using var after = new Parity.Storage.BaselineStore(dir.File("parity.baseline.db"));
        Assert.Single(await after.HistoryAsync()); // 沒有多存一筆
    }

    [Fact]
    public async Task Stable_id_allowlist_from_snapshot_is_used_by_check_so_random_ids_still_match()
    {
        // CLAUDE.md 地雷「穩定 id 白名單:兩端規則必須一致」的指令層驗證。
        // 那個 div 沒有文字、沒有 class,只有一個每次載入都不同的純字母 id——
        // check 端若沒拿快照裡的白名單去擷取,它的 selector 會是新的亂碼,配不上 → 配對率 < 1 → exit 3
        using var dir = new TempDir();
        dir.Write("page.html", Page("""
            <div id="app"><p>stable text</p><div></div></div>
            <script>
              document.querySelector('#app > div').id =
                Array.from({ length: 12 }, () => 'abcdefghijklmnopqrstuvwxyz'[Math.floor(Math.random() * 26)]).join('');
            </script>
            """, "#app { padding: 8px; } #app > div { width: 120px; height: 40px; background: #2563eb; }"));
        var snapConfig = dir.Write("snap.config.json", """{ "targets": [{ "route": "/", "url": "./page.html" }] }""");

        var snap = await Cli.RunAsync("snapshot", "--config", snapConfig);
        if (Cli.BrowserMissing(snap)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(0, snap.ExitCode);
        Assert.Contains("appeared in only one of two page loads", snap.Out);
        var anchors = Root(dir.File("parity.snapshot.json")).GetProperty("stableIdAnchors")
            .EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(["app"], anchors);

        var checkConfig = dir.Write("parity.config.json", """
            { "designFile": "parity.snapshot.json", "targets": [{ "route": "/", "frame": "/", "url": "./page.html" }],
              "gate": { "minMatchRate": 1 } }
            """);
        var check = await Cli.RunAsync("check", "--config", checkConfig);

        Assert.Equal(0, check.ExitCode);
        using var report = JsonDocument.Parse(File.ReadAllText(dir.File(".parity", "report.json")));
        var page = report.RootElement.GetProperty("reports")[0];
        Assert.Empty(page.GetProperty("unmatched").EnumerateArray());
        Assert.All(page.GetProperty("nodes").EnumerateArray(),
            n => Assert.Equal("selector", n.GetProperty("matchedBy").GetString()));
        Assert.Contains(page.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("selector").GetString() == "#app > div:nth-of-type(1)");
    }

    // ---------- serve:只綁 loopback + Host / Origin 防線 ----------

    [Fact]
    public async Task Serve_rejects_foreign_host_and_origin_and_listens_on_loopback_only()
    {
        // 只綁 127.0.0.1 擋不住 DNS rebinding(惡意網域解析到 127.0.0.1)與同瀏覽器的惡意分頁(CSRF);
        // serve 的中介層另外擋 Host 與 POST 的 Origin。serve 會一直跑到 Ctrl+C,所以在獨立行程裡起、測完砍掉
        using var dir = new TempDir();
        var config = BoxProject(dir);
        var port = FreeLoopbackPort();
        using var serve = ServeProcess.Start(config, port);

        if (!await serve.WaitUntilListeningAsync(TimeSpan.FromSeconds(120)))
        {
            if (serve.Stderr.Contains("install-browser", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(Skipped);
                return;
            }
            Assert.Fail($"parity serve 沒有起來。stdout:\n{serve.Stdout}\nstderr:\n{serve.Stderr}");
        }

        var baseUrl = $"http://127.0.0.1:{port}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var state = await http.GetAsync($"{baseUrl}/api/state");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Contains("\"targets\"", await state.Content.ReadAsStringAsync());

        using (var rebinding = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/state"))
        {
            rebinding.Headers.Host = "evil.example";
            var resp = await http.SendAsync(rebinding);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.Contains("only accepts requests from localhost", await resp.Content.ReadAsStringAsync());
        }

        using (var named = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/state"))
        {
            named.Headers.Host = $"localhost:{port}";
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(named)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await PostMapAsync(http, baseUrl, "http://evil.example")).StatusCode);
        // 本機 Origin 與「沒有 Origin」(curl 之類)都放行,進到端點本身的驗證(空欄位 → 400,不觸發重掃)
        Assert.Equal(HttpStatusCode.BadRequest, (await PostMapAsync(http, baseUrl, $"http://localhost:{port}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostMapAsync(http, baseUrl, origin: null)).StatusCode);

        foreach (var address in NonLoopbackIPv4())
            Assert.False(await CanConnectAsync(address, port), $"serve 不該在 {address}:{port} 上聽");
    }

    private static async Task<HttpResponseMessage> PostMapAsync(HttpClient http, string baseUrl, string? origin)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/map")
        {
            Content = new StringContent("""{ "layer": "", "selector": "" }""", Encoding.UTF8, "application/json"),
        };
        if (origin is not null) req.Headers.Add("Origin", origin);
        return await http.SendAsync(req);
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static IEnumerable<IPAddress> NonLoopbackIPv4()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct();

    private static async Task<bool> CanConnectAsync(IPAddress address, int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false; // 拒絕連線或連不到都算「沒在那裡聽」
        }
    }

    /// <summary>獨立行程的 parity serve(dotnet parity.dll,測試輸出目錄裡那一份)。</summary>
    private sealed class ServeProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly TaskCompletionSource<bool> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ServeProcess(Process process) => _process = process;

        public string Stdout { get { lock (_stdout) return _stdout.ToString(); } }

        public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

        public static ServeProcess Start(string config, int port)
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var psi = new ProcessStartInfo(string.IsNullOrEmpty(host) || !File.Exists(host) ? "dotnet" : host)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { Path.Combine(AppContext.BaseDirectory, "parity.dll"), "serve", "--config", config, "--port", $"{port}" })
                psi.ArgumentList.Add(a);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var serve = new ServeProcess(p);
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (serve._stdout) serve._stdout.AppendLine(e.Data);
                if (e.Data.Contains("report UI:")) serve._listening.TrySetResult(true);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) lock (serve._stderr) serve._stderr.AppendLine(e.Data);
            };
            p.Exited += (_, _) => serve._listening.TrySetResult(false);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return serve;
        }

        public async Task<bool> WaitUntilListeningAsync(TimeSpan timeout)
        {
            var done = await Task.WhenAny(_listening.Task, Task.Delay(timeout));
            if (done != _listening.Task) return false;
            if (!_listening.Task.Result) _process.WaitForExit(); // 已結束:等 stderr 收齊
            return _listening.Task.Result;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true); // 連同 Playwright driver 與 Chromium
                _process.WaitForExit(10_000);
            }
            catch (InvalidOperationException) { /* 從沒啟動成功 */ }
            _process.Dispose();
        }
    }
}
