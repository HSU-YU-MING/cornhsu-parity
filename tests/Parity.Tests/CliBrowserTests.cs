using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    [Fact]
    public async Task Snapshot_out_into_a_directory_that_does_not_exist_yet_creates_it()
    {
        // 原本寫截圖與 JSON 前不建目錄:擷取全部做完之後才 exit 2(Could not find a part of the path …s.png)。
        // check 的 --out 一直會先建目錄
        using var dir = new TempDir();
        dir.Write("a.html", Page("<h1>Page A</h1>"));
        var config = dir.Write("parity.config.json", """{ "targets": [{ "route": "/", "url": "./a.html" }] }""");
        var outPath = dir.File("baselines", "mobile", "s.json");

        var r = await Cli.RunAsync("snapshot", "--config", config, "--out", outPath);
        if (Cli.BrowserMissing(r)) { Console.WriteLine(Skipped); return; }

        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(outPath));
        AssertPng(dir.File("baselines", "mobile", "s.png"));
    }

    [Fact]
    public async Task Reference_screenshot_comes_from_the_same_page_load_as_the_frozen_tree()
    {
        // CLAUDE.md 地雷「快照的探測擷取不能拍截圖」的指令層驗證(引擎層的機制見 ScreenshotSourceTests)。
        // snapshot 會多載入頁面好幾次(隨機 id 探測 1 次、--stabilize 再連拍 2 次);截圖字典後拍覆寫先拍,
        // 任何一次探測照拍的話,參考截圖就來自另一次載入,serve 的疊框視圖對不齊。
        // #bar 每次載入寬度都不同,截圖上量到的寬度因此可以指認「這張是哪一次載入拍的」
        using var dir = new TempDir();
        dir.Write("page.html", Page("""
            <div id="bar"></div>
            <script>document.getElementById('bar').style.width = (100 + Math.floor(Math.random() * 600)) + 'px';</script>
            """, "#bar { height: 40px; background: #000; }"));
        var config = dir.Write("parity.config.json", """{ "targets": [{ "route": "/", "url": "./page.html" }] }""");

        var r = await Cli.RunAsync("snapshot", "--config", config, "--stabilize");
        if (Cli.BrowserMissing(r)) { Console.WriteLine(Skipped); return; }

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("changed across 3 captures", r.Out); // 前提:三次載入的 bar 真的不一樣寬
        static IEnumerable<JsonElement> Nodes(JsonElement n) =>
            n.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array
                ? kids.EnumerateArray().SelectMany(Nodes).Prepend(n)
                : [n];
        var bar = Nodes(Root(dir.File("parity.snapshot.json")).GetProperty("root"))
            .Single(n => n.GetProperty("id").GetString()!.EndsWith("#bar", StringComparison.Ordinal));
        var frozenWidth = (int)Math.Round(BoxSize(bar).W);

        using var shot = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(dir.File("parity.snapshot.png"));
        var shotWidth = Enumerable.Range(0, shot.Width).Count(x => shot[x, 20].R < 64); // bar 那一列的黑色像素

        Assert.Equal(frozenWidth, shotWidth);
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

    [Fact]
    public async Task The_offline_demo_produces_exactly_the_counts_ci_asserts()
    {
        // ci.yml 的離線示範除了 exit 1,還用 jq 斷言報告計數。那組數字以「沒有 parity.map.json 的 checkout」
        // 為準——這裡只複製進版控的檔案到暫存目錄實跑(使用者本機跑過 parity map 的話,原地跑會是 12/12),
        // 再拿 ci.yml 裡寫的數字比對:量測或配對一改,本機就先紅,不必等 CI
        using var dir = new TempDir();
        foreach (var f in new[] { "design.json", "index.html", "parity.config.json", "tokens.json" })
            File.Copy(Repo.PathOf("samples", "demo", f), dir.File(f));

        var r = await Cli.RunAsync("check", "--config", dir.File("parity.config.json"));
        if (Cli.BrowserMissing(r)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(1, r.ExitCode);

        var ci = Repo.Read(".github", "workflows", "ci.yml");
        int Expected(string field)
        {
            var m = Regex.Match(ci, $@"\.{field} == (?<n>\d+)");
            Assert.True(m.Success, $"ci.yml 的 jq 斷言裡找不到 .{field}");
            return int.Parse(m.Groups["n"].Value);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(dir.File(".parity", "report.json")));
        var report = Assert.Single(doc.RootElement.GetProperty("reports").EnumerateArray());
        var summary = report.GetProperty("summary");
        foreach (var field in new[] { "designNodes", "matched", "unmatched", "nodesWithDiffs", "critical", "serious", "medium", "minor" })
            Assert.True(Expected(field) == summary.GetProperty(field).GetInt32(),
                $"{field}:ci.yml 斷言 {Expected(field)},實跑 {summary.GetProperty(field).GetInt32()}");
        var soft = report.GetProperty("nodes").EnumerateArray()
            .SelectMany(n => n.GetProperty("diffs").EnumerateArray()).Count(d => d.GetProperty("soft").GetBoolean());
        Assert.Matches(new Regex($@"select\(\.soft\)\] \| length\) == {soft}\b"), ci);
    }

    // ---------- compare.extras:實作端多出來的元件(只列出、永遠不擋) ----------

    /// <summary>
    /// samples/demo/design.json 的忠實實作:12 個設計節點全部配上、零落差(示範頁刻意做壞的那幾處都照設計稿改回來,
    /// hero-badge 用 class 讓自動配對找得到)。<paramref name="banner"/> = G5:body 開頭多一條設計稿沒有的 800×200 紅色橫幅。
    /// </summary>
    private static string FaithfulDemoPage(bool banner) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><style>
          * { box-sizing: border-box; margin: 0; }
          body { width: 800px; padding: 40px; font-family: Arial, sans-serif; background: #FFFFFF; }
          h1.page-title { font-family: Arial, sans-serif; font-size: 32px; font-weight: 700; color: #111827; margin-bottom: 16px; }
          p.subtitle { font-size: 16px; color: #6B7280; margin-bottom: 32px; }
          button.cta { display: block; width: 160px; height: 48px; padding: 12px 20px; background: #2563EB; color: #FFFFFF;
                       border: none; border-radius: 8px; font-size: 16px; font-weight: 600; font-family: Arial, sans-serif;
                       margin-bottom: 40px; }
          section.feature-list { display: flex; gap: 24px; width: 720px; }
          .feature-card { width: 224px; height: 120px; padding: 16px; background: #F3F4F6; border-radius: 12px; }
          .feature-card h3 { font-size: 18px; font-weight: 600; color: #111827; }
          .hero-badge { position: absolute; top: 40px; left: 640px; width: 120px; height: 32px; background: #DCFCE7; }
        </style></head>
        <body>{{(banner ? "<div style=\"position:absolute;top:0;left:0;width:800px;height:200px;background:#FF0000\"></div>" : "")}}
          <h1 class="page-title">Parity Demo</h1>
          <p class="subtitle">Property-level design fidelity checking</p>
          <button class="cta" data-parity="cta-button"><span>Get Started</span></button>
          <section class="feature-list">
            <div class="feature-card" id="feature-card-1"><h3>Numeric comparison</h3></div>
            <div class="feature-card" id="feature-card-2"><h3>Local first</h3></div>
            <div class="feature-card" id="feature-card-3"><h3>Gates your CI</h3></div>
          </section>
          <div class="hero-badge"></div>
        </body></html>
        """;

    private static List<JsonElement> Reports(string reportPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        return doc.RootElement.GetProperty("reports").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    [Fact]
    public async Task Extras_lists_the_banner_the_design_never_had_and_changes_nothing_else()
    {
        using var dir = new TempDir();
        File.Copy(Repo.PathOf("samples", "demo", "design.json"), dir.File("design.json"));
        dir.Write("page.html", FaithfulDemoPage(banner: false));
        dir.Write("g5.html", FaithfulDemoPage(banner: true));
        string Config(string name, string compare) => dir.Write(name, $$"""
            { "designFile": "design.json", "compare": {{compare}},
              "targets": [{ "route": "/", "frame": "", "url": "./page.html" }, { "route": "/g5", "frame": "", "url": "./g5.html" }],
              "gate": { "minMatchRate": 1 } }
            """);
        var defaults = Config("default.config.json", """{ "position": "relative" }""");
        var listing = Config("list.config.json", """{ "position": "relative", "extras": "list" }""");

        // 1) 預設(沒寫 extras):與 2.2.0 相同——橫幅沒有任何提示,exit 0;報告的 extras 明確寫出 null
        var off = await Cli.RunAsync("check", "--config", defaults, "--out", dir.File("off", "report.json"));
        if (Cli.BrowserMissing(off)) { Console.WriteLine(Skipped); return; }
        Assert.True(off.ExitCode == 0, off.Out + off.Err);
        var offReports = Reports(dir.File("off", "report.json"));
        foreach (var r in offReports)
        {
            Assert.Equal(JsonValueKind.Null, r.GetProperty("extras").ValueKind);
            var summary = r.GetProperty("summary");
            Assert.Equal((12, 12, 0), (summary.GetProperty("designNodes").GetInt32(), summary.GetProperty("matched").GetInt32(),
                summary.GetProperty("nodesWithDiffs").GetInt32())); // 基準頁:全部配上、零落差
        }
        Assert.DoesNotContain("extras:", off.Out);

        // 2) "list":G5 列出紅色橫幅,仍是 exit 0;基準頁開著也是 total 0(「開了、沒有」,不是 null)
        var on = await Cli.RunAsync("check", "--config", listing, "--out", dir.File("on", "report.json"), "--md", dir.File("on", "report.md"));
        Assert.True(on.ExitCode == 0, on.Out + on.Err);
        Assert.Contains("PASS", on.Out);
        var onReports = Reports(dir.File("on", "report.json"));
        var clean = onReports[0].GetProperty("extras");
        Assert.Equal(0, clean.GetProperty("total").GetInt32());
        Assert.Equal(0, clean.GetProperty("items").GetArrayLength());
        var g5 = onReports[1].GetProperty("extras");
        Assert.Equal(1, g5.GetProperty("total").GetInt32());
        var banner = Assert.Single(g5.GetProperty("items").EnumerateArray());
        Assert.Equal("body > div:nth-of-type(1)", banner.GetProperty("selector").GetString());
        Assert.Equal("div", banner.GetProperty("tag").GetString());
        Assert.Equal((800, 200), BoxSize(banner));
        Assert.Contains("extras: 1 element(s) on the page have no node in the design (listed only, never gated)", on.Out);
        Assert.Contains("‹body > div:nth-of-type(1)› <div> 800×200 at (0, 0)", on.Out);
        var md = File.ReadAllText(dir.File("on", "report.md"));
        Assert.Contains("✅ PASS", md);
        Assert.Contains("### Extra elements (1)", md);
        Assert.Contains("| /g5 | `body > div:nth-of-type(1)` | `div` | 800×200 at (0, 0) |", md);

        // 只列出:除了 extras,兩次的報告逐欄相同(配對、落差、summary;gate 與分數都從這些來)
        foreach (var (a, b) in offReports.Zip(onReports))
            foreach (var field in new[] { "nodes", "unmatched", "summary" })
                Assert.Equal(a.GetProperty(field).GetRawText(), b.GetProperty(field).GetRawText());

        // 3) --reverse 一律不算:extras 是 null
        var reverse = await Cli.RunAsync("check", "--config", listing, "--reverse", "--out", dir.File("rev", "report.json"));
        Assert.Equal(0, reverse.ExitCode);
        Assert.All(Reports(dir.File("rev", "report.json")), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("extras").ValueKind));
        Assert.DoesNotContain("extras:", reverse.Out);
    }

    [Fact]
    public async Task A_baseline_saved_by_1x_with_soft_rows_reports_nothing_fixed_while_the_diffs_remain()
    {
        // 審查實證:用 1.x 存的 baseline(連 soft 的 page-title · fontFamily 一起存)連跑兩次 check --baseline,
        // 兩次都是「fixed 1」,PR 留言列出 Fixed,同一條落差卻還在落差表裡。現況沒變,就不該有任何「修好」
        using var dir = new TempDir();
        foreach (var f in new[] { "design.json", "index.html", "parity.config.json", "tokens.json" })
            File.Copy(Repo.PathOf("samples", "demo", f), dir.File(f));
        var config = dir.File("parity.config.json");

        var first = await Cli.RunAsync("check", "--config", config);
        if (Cli.BrowserMissing(first)) { Console.WriteLine(Skipped); return; }
        // 照 1.x 的 baseline save 存:所有落差(含 soft)攤平
        var doc = JsonSerializer.Deserialize<Parity.Engine.ReportDocument>(
            File.ReadAllText(dir.File(".parity", "report.json")), Parity.Engine.ReportWire.Compact)!;
        var all = doc.Reports.SelectMany(r => r.Nodes.SelectMany(n => n.Diffs.Select(d =>
            new Parity.Engine.DiffRecord(r.Route, n.DesignLayer, n.Selector, d.Prop, d.Severity, d.Expected, d.Actual)))).ToList();
        Assert.Contains(all, d => d.Prop == "fontFamily");
        await using (var store = new Parity.Storage.BaselineStore(dir.File("parity.baseline.db")))
            await store.SaveAsync(all, new DateTime(2026, 8, 1), score: 67);

        for (var run = 0; run < 2; run++)
        {
            var check = await Cli.RunAsync("check", "--config", config, "--baseline", "--md", dir.File($"b{run}.md"));
            Assert.Equal(0, check.ExitCode);
            Assert.Contains("fixed 0", check.Out);
            Assert.Contains("ignored 1", check.Out); // soft 列另計,baseline 的每一列都有去處
            Assert.Contains("ignored 1", File.ReadAllText(dir.File($"b{run}.md")));
            Assert.DoesNotContain("- fixed", check.Out);
            Assert.DoesNotContain("Fixed", File.ReadAllText(dir.File($"b{run}.md")));
        }
    }

    [Fact]
    public async Task A_leftover_figma_file_key_does_not_link_a_design_file_report_to_figma()
    {
        // init 範本預設就有 figmaFileKey;改走 designFile(snapshot)時沒刪掉,設計來源其實是本機檔
        // (designId 是 CSS selector),Markdown 卻拿 figmaFileKey 組出點了就壞的 figma.com 連結
        using var dir = new TempDir();
        BoxProject(dir);
        var config = dir.Write("parity.config.json", """
            { "figmaFileKey": "qRzJ3fxF1g09NczsrugiXD", "designFile": "design.json",
              "targets": [{ "route": "/", "frame": "", "url": "./page.html" }] }
            """);

        var r = await Cli.RunAsync("check", "--config", config, "--md", dir.File("report.md"));
        if (Cli.BrowserMissing(r)) { Console.WriteLine(Skipped); return; }

        Assert.Equal(1, r.ExitCode);
        var md = File.ReadAllText(dir.File("report.md"));
        Assert.Contains("| `zz` | width |", md); // 圖層名照列,只是不連
        Assert.DoesNotContain("figma.com", md);

        var rerendered = await Cli.RunAsync("report", "--config", config);
        Assert.Equal(0, rerendered.ExitCode);
        Assert.DoesNotContain("figma.com", rerendered.Out);
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
        // 信封帶方向(2.0.0):一般的是 forward、反向的是 reverse,下游(report / push / 伺服器)據此分辨
        Assert.Contains("\"direction\": \"forward\"", File.ReadAllText(dir.File(".parity", "report.json")));
        Assert.Contains("\"direction\": \"reverse\"", File.ReadAllText(dir.File("rev", "report.json")));
    }

    // ---------- gate.failOnUnmatched:元件不見可以選擇擋 PR ----------

    /// <summary>
    /// 以 samples/demo/design.json 為設計來源、全部配得上且零落差的頁面(按鈕裡是 &lt;span&gt;Get Started&lt;/span&gt;、
    /// 徽章是空的絕對定位 div)。G1 拿掉按鈕、G2 拿掉徽章。
    /// </summary>
    private static string FaithfulDemoPage(bool button = true, bool badge = true) => Page(
        $$"""
        <h1 class="page-title">Parity Demo</h1>
        <p class="subtitle">Property-level design fidelity checking</p>
        {{(button ? "<button class=\"cta-button\"><span>Get Started</span></button>" : "")}}
        <section class="feature-list">
          <div class="feature-card" id="feature-card-1"><h3>Numeric comparison</h3></div>
          <div class="feature-card" id="feature-card-2"><h3>Local first</h3></div>
          <div class="feature-card" id="feature-card-3"><h3>Gates your CI</h3></div>
        </section>
        {{(badge ? "<div class=\"hero-badge\"></div>" : "")}}
        """,
        """
        * { box-sizing: border-box; margin: 0; }
        body { width: 800px; height: 600px; padding: 40px; font-family: Arial, sans-serif; background: #FFFFFF; position: relative; }
        h1.page-title { width: 300px; font-size: 32px; font-weight: 700; line-height: 40px; color: #111827; margin-bottom: 16px; }
        p.subtitle { width: 400px; font-size: 16px; line-height: 24px; color: #6B7280; margin-bottom: 32px; }
        button.cta-button { display: block; width: 160px; height: 48px; padding: 12px 20px; background: #2563EB; color: #FFFFFF;
          border: none; border-radius: 8px; font: 600 16px/24px Arial, sans-serif; margin-bottom: 40px; }
        button.cta-button span { display: block; }
        section.feature-list { display: flex; gap: 24px; width: 720px; height: 120px; }
        .feature-card { position: relative; width: 224px; height: 120px; background: #F3F4F6; border-radius: 12px; }
        .feature-card h3 { position: absolute; left: 16px; top: 16px; width: 192px; font-size: 18px; font-weight: 600; line-height: 22px; color: #111827; }
        .hero-badge { position: absolute; top: 40px; left: 640px; width: 120px; height: 32px; background: #DCFCE7; }
        """);

    /// <summary>同一個目錄裡一頁一份設定:design.json 共用,gate 區段照測試給。</summary>
    private static string UnmatchedGateConfig(TempDir dir, string name, string html, string gate)
    {
        if (!File.Exists(dir.File("design.json")))
            File.Copy(Repo.PathOf("samples", "demo", "design.json"), dir.File("design.json"));
        dir.Write($"{name}.html", html);
        return dir.Write($"{name}.config.json", $$"""
            { "designFile": "design.json", "targets": [{ "route": "/", "frame": "", "url": "./{{name}}.html" }],
              "compare": { "position": "relative" }, "gate": {{gate}} }
            """);
    }

    private static Dictionary<string, string> UnmatchedReasons(string reportPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        return doc.RootElement.GetProperty("reports")[0].GetProperty("unmatched").EnumerateArray()
            .ToDictionary(u => u.GetProperty("designLayer").GetString()!, u => u.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task A_missing_element_gates_only_when_fail_on_unmatched_names_its_reason()
    {
        using var dir = new TempDir();
        const string severities = """{ "failOn": ["critical", "serious"] }""";

        // 前提:基準頁全部配得上、零落差——之後的 GATE FAIL 只可能來自拿掉的那個元素
        var faithful = await Cli.RunAsync("check", "--config", UnmatchedGateConfig(dir, "base", FaithfulDemoPage(), severities),
            "--out", dir.File("base.json"));
        if (Cli.BrowserMissing(faithful)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(0, faithful.ExitCode);
        Assert.Contains("matched 12/12 design nodes; 0 with diffs", faithful.Out);

        // G2:徽章沒做。預設設定照舊只列出、不擋(exit 0);failOnUnmatched: ["*"] 才擋,輸出講明是這條
        var noBadge = FaithfulDemoPage(badge: false);
        var g2 = await Cli.RunAsync("check", "--config", UnmatchedGateConfig(dir, "g2", noBadge, severities), "--out", dir.File("g2.json"));
        Assert.Equal(0, g2.ExitCode);
        Assert.Equal(new() { ["hero-badge"] = "no-anchor" }, UnmatchedReasons(dir.File("g2.json")));
        Assert.Contains("PASS\x1b[0m (fail on: critical, serious)", g2.Out); // 沒設 failOnUnmatched:那一行與以前一字不差

        var g2Star = await Cli.RunAsync("check", "--config",
            UnmatchedGateConfig(dir, "g2-star", noBadge, """{ "failOn": ["critical", "serious"], "failOnUnmatched": ["*"] }"""),
            "--out", dir.File("g2-star.json"));
        Assert.Equal(1, g2Star.ExitCode);
        Assert.Contains("GATE FAIL\x1b[0m (fail on: critical, serious; fail on unmatched: *)", g2Star.Out);
        Assert.Contains("/: 1 unmatched design node blocked by gate.failOnUnmatched (no-anchor: hero-badge)", g2Star.Out);

        // G1:按鈕沒做 → 按鈕裡的文字整頁都找不到。下方的 feature-list 會跟著上移(offsetY 落差),
        // 所以預設 failOn 本來就 exit 1、證明不了新條件——關掉嚴重度,只留 failOnUnmatched
        var noButton = FaithfulDemoPage(button: false);
        var g1 = await Cli.RunAsync("check", "--config",
            UnmatchedGateConfig(dir, "g1", noButton, """{ "failOn": [], "failOnUnmatched": ["text-not-found"] }"""),
            "--out", dir.File("g1.json"), "--md", dir.File("g1.md"));
        Assert.Equal(1, g1.ExitCode);
        Assert.Equal(new() { ["cta-button"] = "no-anchor", ["cta-label"] = "text-not-found" }, UnmatchedReasons(dir.File("g1.json")));
        Assert.Contains("GATE FAIL\x1b[0m (fail on unmatched: text-not-found)", g1.Out);
        Assert.Contains("/: 1 unmatched design node blocked by gate.failOnUnmatched (text-not-found: cta-label)", g1.Out);
        Assert.Contains("> ⚠️ /: 1 unmatched design node blocked by gate.failOnUnmatched", File.ReadAllText(dir.File("g1.md")));

        var g1Off = await Cli.RunAsync("check", "--config", UnmatchedGateConfig(dir, "g1-off", noButton, """{ "failOn": [] }"""),
            "--out", dir.File("g1-off.json"));
        Assert.Equal(0, g1Off.ExitCode); // 同一頁,只關掉 failOnUnmatched → 過:上面的 exit 1 就是新條件擋的
        Assert.Equal("text-not-found", UnmatchedReasons(dir.File("g1-off.json"))["cta-label"]);
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
        Assert.False(File.Exists(dir.File("parity.baseline.db"))); // 只有 baseline save 會建檔

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

        // 4) 頁面照設計稿修好:基準裡那條算「修好」、放行,並給 PM 看的分數走勢(基準 0 分 → 現在 100 分)
        dir.Write("page.html", Page("<div id=\"box\"></div>", BoxCss.Replace("width: 200px", "width: 300px")));
        var repaired = await Cli.RunAsync("check", "--config", config, "--baseline");
        Assert.Equal(0, repaired.ExitCode);
        Assert.Contains("fixed 1", repaired.Out);
        Assert.Contains("- fixed\x1b[0m    / ‹zz› width", repaired.Out);
        Assert.Contains("fidelity trend: baseline 0/100 → now 100/100", repaired.Out);
        Assert.Contains("↑ +100", repaired.Out);
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

    [Fact]
    public async Task An_outline_added_after_the_snapshot_is_a_soft_hint()
    {
        // 清單 #6 的驗收(論文評估快照模式:加外框 0/22)。快照時沒有外框,之後加了 2px 紅框:
        // 報 outlineWidth 軟落差,只提醒、不擋(exit 0)
        using var dir = new TempDir();
        const string css = "#card { width: 160px; height: 60px; background: #eef; }";
        dir.Write("page.html", Page("""<div id="card">Pay</div>""", css));
        var snapConfig = dir.Write("snap.config.json", """{ "targets": [{ "route": "/", "url": "./page.html" }] }""");

        var snap = await Cli.RunAsync("snapshot", "--config", snapConfig);
        if (Cli.BrowserMissing(snap)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(0, snap.ExitCode);
        Assert.Equal(2, Root(dir.File("parity.snapshot.json")).GetProperty("styleDefaultsVersion").GetInt32());

        dir.Write("page.html", Page("""<div id="card">Pay</div>""", css + " #card { outline: 2px solid red; }"));
        var checkConfig = dir.Write("parity.config.json", """
            { "designFile": "parity.snapshot.json", "targets": [{ "route": "/", "frame": "/", "url": "./page.html" }] }
            """);
        var check = await Cli.RunAsync("check", "--config", checkConfig);

        Assert.Equal(0, check.ExitCode);
        using var report = JsonDocument.Parse(File.ReadAllText(dir.File(".parity", "report.json")));
        var diffs = report.RootElement.GetProperty("reports")[0].GetProperty("nodes").EnumerateArray()
            .SelectMany(n => n.GetProperty("diffs").EnumerateArray()).ToList();
        var outline = Assert.Single(diffs, d => d.GetProperty("prop").GetString() == "outlineWidth");
        Assert.Equal(("0", "2"), (outline.GetProperty("expected").GetString(), outline.GetProperty("actual").GetString()));
        Assert.True(outline.GetProperty("soft").GetBoolean());
    }

    [Fact]
    public async Task An_accepted_gap_lets_the_check_pass_and_a_stale_entry_gets_a_note()
    {
        // 單筆落差豁免(2.5.0):設計稿 #box 寬 300、頁面 200 → critical 的 width 落差擋下(exit 1)。
        // 設定檔收下這一條、寫原因 → 過(exit 0),報告照樣列出它、標 accepted;另一條對不到東西的條目在 stderr 說一聲。
        // 頁面又改成 250:收下的是「200」,落差回來、照擋
        using var dir = new TempDir();
        var config = BoxProject(dir);
        var before = await Cli.RunAsync("check", "--config", config);
        if (Cli.BrowserMissing(before)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(1, before.ExitCode);

        dir.Write("parity.config.json", """
            { "designFile": "design.json", "targets": [{ "route": "/", "frame": "", "url": "./page.html" }],
              "accept": [
                { "layer": "zz", "prop": "width", "actual": "200", "reason": "narrower box approved in review" },
                { "layer": "Gone", "prop": "color", "reason": "old" } ] }
            """);
        var accepted = await Cli.RunAsync("check", "--config", config, "--md", dir.File("a.md"));

        Assert.Equal(0, accepted.ExitCode);
        var width = Diffs(dir).Single(d => d.Diff.GetProperty("prop").GetString() == "width").Diff;
        Assert.True(width.GetProperty("soft").GetBoolean());
        Assert.Equal("narrower box approved in review", width.GetProperty("accepted").GetString());
        Assert.Contains("[accepted: narrower box approved in review]", accepted.Out);
        Assert.Contains("note: accept[1] (Gone color) matched no gap", accepted.Err);
        Assert.DoesNotContain("accept[0]", accepted.Err);
        Assert.Contains("### Accepted gaps (1)", File.ReadAllText(dir.File("a.md")));

        dir.Write("page.html", Page("<div id=\"box\"></div>", BoxCss.Replace("width: 200px", "width: 250px")));
        var changed = await Cli.RunAsync("check", "--config", config);
        Assert.Equal(1, changed.ExitCode);
        Assert.Contains("note: accept[0] (zz width at 200) matched no gap — it is fixed or the value changed.", changed.Err);
    }

    private static List<(string Layer, JsonElement Diff)> Diffs(TempDir dir)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(dir.File(".parity", "report.json")));
        return report.RootElement.GetProperty("reports")[0].GetProperty("nodes").EnumerateArray()
            .SelectMany(n => n.GetProperty("diffs").EnumerateArray().Select(d => (n.GetProperty("designLayer").GetString()!, d.Clone())))
            .ToList();
    }

    // ---------- 頁面狀態:snapshot 與 check 套同一組步驟 ----------

    [Fact]
    public async Task Snapshot_and_check_both_run_the_target_steps()
    {
        // 2.5.0:選單要點開才看得到。快照照設定檔的步驟點開後拍;check 也點開 → 跟快照一致、過。
        // 拿掉步驟再 check:面板沒打開,快照裡的面板節點配不到 → 不會是 0
        using var dir = new TempDir();
        dir.Write("page.html", Page("""
            <button id="open" onclick="document.getElementById('panel').style.display='block'">Menu</button>
            <div id="panel" style="display:none; width:240px; height:120px; background:#eef">Panel content</div>
            """));
        const string steps = """ "steps": [ { "click": "#open" }, { "waitFor": "#panel" } ] """;
        var snap = await Cli.RunAsync("snapshot", "--config", dir.Write("snap.config.json", $$"""{ "targets": [{ "route": "/", "url": "./page.html", {{steps}} }] }"""));
        if (Cli.BrowserMissing(snap)) { Console.WriteLine(Skipped); return; }
        Assert.Equal(0, snap.ExitCode);
        Assert.Contains("Panel content", File.ReadAllText(dir.File("parity.snapshot.json")));

        var withSteps = await Cli.RunAsync("check", "--config", dir.Write("parity.config.json",
            $$"""{ "designFile": "parity.snapshot.json", "gate": { "failOnUnmatched": ["*"] }, "targets": [{ "route": "/", "frame": "/", "url": "./page.html", {{steps}} }] }"""));
        Assert.Equal(0, withSteps.ExitCode);

        var without = await Cli.RunAsync("check", "--config", dir.Write("plain.config.json",
            """{ "designFile": "parity.snapshot.json", "gate": { "failOnUnmatched": ["*"] }, "targets": [{ "route": "/", "frame": "/", "url": "./page.html" }] }"""));
        Assert.NotEqual(0, without.ExitCode);
    }

    // ---------- mcp:真的行程、真的 stdio ----------

    [Fact]
    public async Task Mcp_check_over_stdio_gives_the_same_verdict_as_the_cli_and_keeps_stdout_clean()
    {
        // 2.5.0(實驗性):agent 透過 MCP 跑 parity_check。獨立行程才測得到 stdout:掃描途中的任何 Console.WriteLine
        // 混進 stdout 都會打壞協定,所以每一行都要是 JSON-RPC。BoxProject 的 width 300 → 200 是 critical:exit 1 / fail
        using var dir = new TempDir();
        BoxProject(dir);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var psi = new ProcessStartInfo(string.IsNullOrEmpty(host) || !File.Exists(host) ? "dotnet" : host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir.Path,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "parity.dll"));
        psi.ArgumentList.Add("mcp");
        psi.Environment["DO_NOT_TRACK"] = "1";
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();

        async Task<JsonElement> Send(string line)
        {
            await p.StandardInput.WriteLineAsync(line);
            await p.StandardInput.FlushAsync();
            var reply = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.NotNull(reply);
            return JsonDocument.Parse(reply).RootElement.Clone(); // 不是 JSON 就在這裡炸:stdout 被汙染了
        }

        try
        {
            var init = await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
            Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());
            await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var check = (await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"parity_check","arguments":{}}}"""))
                .GetProperty("result");
            var s = check.GetProperty("structuredContent");
            if (check.GetProperty("isError").GetBoolean() && s.GetProperty("error").GetString()!.Contains("install-browser"))
            {
                Console.WriteLine(Skipped);
                return;
            }
            Assert.False(check.GetProperty("isError").GetBoolean(), s.ToString());
            Assert.Equal(("fail", 1), (s.GetProperty("verdict").GetString(), s.GetProperty("exitCode").GetInt32()));
            var gap = s.GetProperty("gaps").GetProperty("items")[0];
            Assert.Equal(("zz", "width", "300", "200", "critical"), (gap.GetProperty("layer").GetString(), gap.GetProperty("prop").GetString(),
                gap.GetProperty("expected").GetString(), gap.GetProperty("actual").GetString(), gap.GetProperty("severity").GetString()));
            Assert.True(File.Exists(dir.File(".parity", "report.json")));

            // 同一份報告翻頁:不重跑
            var page = await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"parity_get_report","arguments":{"prop":"width"}}}""");
            Assert.Equal(1, page.GetProperty("result").GetProperty("structuredContent").GetProperty("gaps").GetProperty("total").GetInt32());

            // CLI 對同一頁給同樣的結論
            var cli = await Cli.RunAsync("check", "--config", dir.File("parity.config.json"));
            Assert.Equal(1, cli.ExitCode);
        }
        finally
        {
            p.StandardInput.Close(); // stdin 關掉 = 結束
            if (!p.WaitForExit(30_000)) p.Kill(entireProcessTree: true);
        }
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("MCP server on stdio", await stderr);
    }

    // ---------- serve:只綁 loopback + Host / Origin 防線 ----------

    [Fact]
    public async Task Serve_rejects_foreign_host_and_origin_and_listens_on_loopback_only()
    {
        // 只綁 127.0.0.1 擋不住 DNS rebinding(惡意網域解析到 127.0.0.1)與同瀏覽器的惡意分頁(CSRF);
        // serve 的中介層另外擋 Host 與 POST 的 Origin。serve 會一直跑到 Ctrl+C,所以在獨立行程裡起、測完砍掉
        using var dir = new TempDir();
        BoxProject(dir);
        // 順便驗 UI 的 Figma 連結:設計來源是 designFile 時,殘留的 figmaFileKey 不能交給 UI 組連結
        var config = dir.Write("parity.config.json", """
            { "figmaFileKey": "qRzJ3fxF1g09NczsrugiXD", "designFile": "design.json",
              "targets": [{ "route": "/", "frame": "", "url": "./page.html" }] }
            """);
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
        using (var stateJson = JsonDocument.Parse(await state.Content.ReadAsStringAsync()))
        {
            Assert.True(stateJson.RootElement.TryGetProperty("targets", out _));
            Assert.Equal(JsonValueKind.Null, stateJson.RootElement.GetProperty("figmaFileKey").ValueKind);
        }

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

    [Fact]
    public async Task Map_save_writes_the_map_file_rescans_and_tells_the_ui_to_reload()
    {
        // `parity map` 的儲存動作(UI 選未配對圖層 → 點頁面元素 → POST /api/map):寫 parity.map.json、
        // 重掃、SSE 推 reload 讓 UI 自己刷新。用 serve 起(map 指令會順手開使用者的預設瀏覽器),API 是同一套
        using var dir = new TempDir();
        var config = BoxProject(dir);
        // 多一個設計節點「promo」:頁面上那個 aside 沒有任何錨點(沒字、沒 id、class 對不上),只能手動配
        dir.Write("page.html", Page("<div id=\"box\"></div><aside class=\"x9\"></aside>",
            BoxCss + " aside { width: 120px; height: 30px; }"));
        dir.Write("design.json", """
            { "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 800, "height": 600 },
              "children": [
                { "id": "#box", "name": "zz", "type": "frame", "box": { "x": 0, "y": 0, "width": 200, "height": 100 } },
                { "id": "1:9", "name": "promo", "type": "frame", "box": { "x": 0, "y": 100, "width": 120, "height": 30 } } ] }
            """);
        var port = FreeLoopbackPort();
        using var serve = ServeProcess.Start(config, port);
        if (!await serve.WaitUntilListeningAsync(TimeSpan.FromSeconds(120)))
        {
            if (serve.Stderr.Contains("install-browser", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(Skipped); return; }
            Assert.Fail($"parity serve 沒有起來。stdout:\n{serve.Stdout}\nstderr:\n{serve.Stderr}");
        }
        var baseUrl = $"http://127.0.0.1:{port}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        async Task<JsonElement> TargetAsync()
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{baseUrl}/api/state"));
            return doc.RootElement.GetProperty("targets")[0].Clone();
        }

        Assert.Equal("promo", Assert.Single((await TargetAsync()).GetProperty("unmatched").EnumerateArray())
            .GetProperty("designLayer").GetString()); // 前提:一開始配不到

        // 疊框視圖要的原料:截圖與兩棵樹
        var shot = await http.GetAsync($"{baseUrl}/api/screenshot/0");
        Assert.Equal("image/png", shot.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{baseUrl}/api/screenshot/1")).StatusCode);
        using (var detail = JsonDocument.Parse(await http.GetStringAsync($"{baseUrl}/api/detail/0")))
            Assert.Equal(["renderedTree", "designTree"], detail.RootElement.EnumerateObject().Select(p => p.Name));

        using var events = new StreamReader(await (await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/events"), HttpCompletionOption.ResponseHeadersRead))
            .Content.ReadAsStreamAsync());
        using var sseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(": connected", await events.ReadLineAsync(sseTimeout.Token));

        var save = await PostMapAsync(http, baseUrl, origin: null, layer: "promo", selector: "aside.x9");

        // POST 回來時重掃已經做完(端點是 寫檔 → 重掃 → 回應),所以下面三件事都不必等
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using (var map = JsonDocument.Parse(File.ReadAllText(dir.File("parity.map.json"))))
            Assert.Equal("aside.x9", map.RootElement.GetProperty("promo").GetString()); // map 檔預設落在 config 旁
        var target = await TargetAsync();
        Assert.Empty(target.GetProperty("unmatched").EnumerateArray());
        Assert.Contains(target.GetProperty("nodes").EnumerateArray(), n =>
            n.GetProperty("designLayer").GetString() == "promo" && n.GetProperty("matchedBy").GetString() == "explicit");
        string? line;
        while ((line = await events.ReadLineAsync(sseTimeout.Token)) == "") { }
        Assert.Equal("data: reload", line);
    }

    private static async Task<HttpResponseMessage> PostMapAsync(
        HttpClient http, string baseUrl, string? origin, string layer = "", string selector = "")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/map")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { layer, selector }), Encoding.UTF8, "application/json"),
        };
        if (origin is not null) req.Headers.Add("Origin", origin);
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task Serve_fails_on_a_busy_port_before_printing_the_url()
    {
        // 網址要等真的在監聽才印(map 與 --open 會立刻拿它開瀏覽器,測試也靠這一行判斷可以連了);
        // 埠被占用時要在印網址之前就以 exit 2 結束,不能先印出一個連不上的網址
        using var dir = new TempDir();
        var config = BoxProject(dir);
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            using var serve = ServeProcess.Start(config, port);

            var exit = await serve.WaitForExitAsync(TimeSpan.FromSeconds(120));
            if (serve.Stderr.Contains("install-browser", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(Skipped); return; }
            Assert.True(exit is not null, $"埠被占用時 parity serve 沒有結束。stdout:\n{serve.Stdout}\nstderr:\n{serve.Stderr}");
            Assert.Equal(2, exit);
            Assert.DoesNotContain("report UI:", serve.Stdout);
            Assert.Contains("error", serve.Stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            occupied.Stop();
        }
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

        public async Task<int?> WaitForExitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try { await _process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { return null; }
            return _process.ExitCode;
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
