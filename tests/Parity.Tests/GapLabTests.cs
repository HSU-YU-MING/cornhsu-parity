using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Parity.Engine;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// 缺口實驗室:一張全配對、零落差的基準頁,加上一組「每次只改一件事」的變體,逐列鎖住
/// <c>parity check</c> 現在抓得到什麼(P 組)、抓不到什麼(G 組)。缺口清單因此不只活在文件上:
/// 補上一項能力,對應那一列就會紅——把那一列的預期值(exit code、落差、未配對原因)改成新行為,
/// 就是那項功能的驗收。
///
/// 每一列的預期值都是「只對這一頁跑 check」會得到的結果。為了不讓十幾次瀏覽器拖慢 dotnet test,
/// 同一份設定的列共用一次 check(一個瀏覽器、多個 target):落差與未配對從 report.json 逐頁讀;
/// exit code 讀 CLI 自己印的逐頁原因(GATE FAIL / MATCH INTEGRITY TOO LOW 底下的「· /route: …」),
/// 是 gate 本身的判定,測試不另外重算。配對可信度(exit 3)是整次執行的判定,一頁不過整次就是 3、
/// 其他頁的 gate 結果不會印出來——所以預期 3 的列必須自己一組(fixture 會檢查)。
///
/// 頁面全在暫存目錄,走 file://,不連網。沒裝 Chromium 時每條都印「略過」。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class GapLabTests(GapLabTests.LabRuns runs) : IClassFixture<GapLabTests.LabRuns>
{
    private const string Skipped = "略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器";

    // ---------- 頁面 ----------

    /// <summary>120×64 的純色 SVG,以 data URI 內嵌(頁面不引用任何外部檔案)。</summary>
    private static string SolidSvg(string hex) => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
        $"""<svg xmlns="http://www.w3.org/2000/svg" width="120" height="64"><rect width="120" height="64" fill="{hex}"/></svg>"""));

    private static readonly string BlueSvg = SolidSvg("#2563EB");
    private static readonly string RedSvg = SolidSvg("#FF0000");

    /// <summary>基準頁:samples/demo 的版面照設計稿做對,再加一張 hero-image。13/13 配對、零落差、exit 0(列 00)。</summary>
    private static readonly string LabPage = $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>lab</title><style>
          * { box-sizing: border-box; margin: 0; }
          body { width: 800px; height: 600px; padding: 40px; font-family: Arial, sans-serif; background: #FFFFFF; position: relative; }
          h1.page-title { font-family: Arial, sans-serif; font-size: 32px; font-weight: 700; color: #111827; height: 40px; line-height: 40px; margin-bottom: 16px; }
          p.subtitle { font-size: 16px; color: #6B7280; line-height: 24px; margin-bottom: 32px; }
          button.cta-button { display: block; width: 160px; height: 48px; padding: 12px 20px; background: #2563EB; color: #FFFFFF; border: none; border-radius: 8px; font-size: 16px; font-weight: 600; font-family: Arial, sans-serif; margin-bottom: 40px; }
          section.feature-list { display: flex; gap: 24px; width: 720px; }
          .feature-card { width: 224px; height: 120px; padding: 16px; background: #F3F4F6; border-radius: 12px; }
          .feature-card h3 { font-size: 18px; font-weight: 600; color: #111827; }
          .hero-badge { position: absolute; top: 40px; left: 640px; width: 120px; height: 32px; background: #DCFCE7; }
          img.hero-image { display: block; width: 120px; height: 64px; margin-top: 40px; }
        </style></head><body>
        <h1 class="page-title">Parity Demo</h1>
        <p class="subtitle">Property-level design fidelity checking</p>
        <button class="cta-button"><span>Get Started</span></button>
        <section class="feature-list">
          <div class="feature-card" id="feature-card-1"><h3>Numeric comparison</h3></div>
          <div class="feature-card" id="feature-card-2"><h3>Local first</h3></div>
          <div class="feature-card" id="feature-card-3"><h3>Gates your CI</h3></div>
        </section>
        <img class="hero-image" src="{{BlueSvg}}" alt="">
        <div class="hero-badge"></div>
        </body></html>
        """;

    private const string Button = """<button class="cta-button"><span>Get Started</span></button>""";
    private const string Badge = """<div class="hero-badge"></div>""";
    private const string Card1 = """<div class="feature-card" id="feature-card-1"><h3>Numeric comparison</h3></div>""";
    private const string Card3 = """<div class="feature-card" id="feature-card-3"><h3>Gates your CI</h3></div>""";

    /// <summary>
    /// G11 的頁面:一個固定高度的 flex 列,子元件靠上(align-items: flex-start)。
    /// 設計稿上同一個子元件是垂直置中(<see cref="AlignDesign"/>),兩邊差 40px。
    /// </summary>
    private const string AlignPage = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>align</title><style>
          * { box-sizing: border-box; margin: 0; }
          body { width: 800px; height: 600px; padding: 40px; background: #FFFFFF; }
          .align-row { display: flex; align-items: flex-start; width: 400px; height: 120px; background: #F3F4F6; }
          .align-chip { width: 120px; height: 40px; background: #2563EB; }
        </style></head><body>
        <div class="align-row"><div class="align-chip"></div></div>
        </body></html>
        """;

    /// <summary>
    /// G11 的設計稿,兩個 frame 畫同一個版面:子元件在 120 高的容器裡垂直置中(y = 40 + (120 − 40) / 2 = 80)。
    /// <c>3:1</c> 的容器是自動排版(layoutMode;Figma 會一起帶的 primaryAxisAlignItems / counterAxisAlignItems 也照寫);
    /// <c>4:1</c> 的容器不是自動排版——對照組。
    /// </summary>
    private const string AlignDesign = """
        { "id": "align", "name": "align", "type": "Frame", "box": { "x": 0, "y": 0, "width": 1700, "height": 600 },
          "children": [
            { "id": "3:1", "name": "align-auto", "type": "Frame", "box": { "x": 0, "y": 0, "width": 800, "height": 600 },
              "fill": { "r": 255, "g": 255, "b": 255, "a": 1 },
              "children": [
                { "id": "3:2", "name": "align-row", "type": "Frame", "box": { "x": 40, "y": 40, "width": 400, "height": 120 },
                  "fill": { "r": 243, "g": 244, "b": 246, "a": 1 },
                  "layoutMode": "HORIZONTAL", "primaryAxisAlignItems": "MIN", "counterAxisAlignItems": "CENTER",
                  "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "FIXED",
                  "children": [
                    { "id": "3:3", "name": "align-chip", "type": "Frame", "box": { "x": 40, "y": 80, "width": 120, "height": 40 },
                      "layoutSizingHorizontal": "FIXED", "layoutSizingVertical": "FIXED",
                      "fill": { "r": 37, "g": 99, "b": 235, "a": 1 }, "children": [] } ] } ] },
            { "id": "4:1", "name": "align-free", "type": "Frame", "box": { "x": 900, "y": 0, "width": 800, "height": 600 },
              "fill": { "r": 255, "g": 255, "b": 255, "a": 1 },
              "children": [
                { "id": "4:2", "name": "align-row", "type": "Frame", "box": { "x": 940, "y": 40, "width": 400, "height": 120 },
                  "fill": { "r": 243, "g": 244, "b": 246, "a": 1 },
                  "children": [
                    { "id": "4:3", "name": "align-chip", "type": "Frame", "box": { "x": 940, "y": 80, "width": 120, "height": 40 },
                      "fill": { "r": 37, "g": 99, "b": 235, "a": 1 }, "children": [] } ] } ] } ] }
        """;

    // ---------- 改法 ----------

    private static readonly Func<string, string> Same = html => html;

    /// <summary>把一條 CSS 規則附加在 &lt;style&gt; 最後(同權重,後寫的贏)。</summary>
    private static Func<string, string> Css(string rule) => html => Replace(html, "</style>", $"  {rule}\n</style>");

    private static Func<string, string> Swap(string from, string to) => html => Replace(html, from, to);

    private static Func<string, string> Remove(string element) => html => Replace(html, element, "");

    /// <summary>兩段頁面內容互換位置。</summary>
    private static Func<string, string> Exchange(string a, string b) => html =>
        Replace(Replace(Replace(html, a, "\0"), b, a), "\0", b);

    /// <summary>只換掉剛好出現一次的那段。找不到或不只一處就丟——案例沒改到頁面時要紅,不能拿基準頁去「通過」。</summary>
    private static string Replace(string html, string from, string to)
    {
        var at = html.IndexOf(from, StringComparison.Ordinal);
        if (at < 0 || html.IndexOf(from, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"gap-lab edit must hit exactly one place on the page: {from}");
        return string.Concat(html.AsSpan(0, at), to, html.AsSpan(at + from.Length));
    }

    // ---------- 表格 ----------

    /// <summary>同一組 = 同一份設定、同一次 check。</summary>
    public enum Group
    {
        /// <summary>基準頁 + 設計稿(samples/demo 的 design.json 再加 hero-image),gate 只看 failOn。</summary>
        Lab,
        /// <summary>同 Lab,gate 另加 <c>minMatchRate: 1</c>(exit 3 是整次執行的判定,自己一組)。</summary>
        StrictMatch,
        /// <summary>G11 的頁面與設計稿。</summary>
        Align,
    }

    /// <summary>
    /// 一列 = 一個頁面變體。<paramref name="Diffs"/> 寫「圖層.屬性」,<paramref name="Unmatched"/> 寫「圖層 (原因)」——
    /// 與 check 主控台的寫法一致;順序不拘。<paramref name="Frame"/> 是 target 的 frame(空 = 整份設計稿)。
    /// </summary>
    private sealed record Case(
        string Id, string What, Group Group, Func<string, string> Page,
        int Exit, string[] Diffs, string[] Unmatched, string Frame = "");

    private static readonly Case[] Table =
    [
        new("00", "基準頁", Group.Lab, Same, 0, [], []),

        // ---------- P 組:抓得到(改一個屬性 → 一條對應的落差 → exit 1)----------
        new("P1", "按鈕顏色", Group.Lab, Css("button.cta-button{background:#FF0000}"), 1, ["cta-button.background"], []),
        new("P2", "按鈕寬度", Group.Lab, Css("button.cta-button{width:120px}"), 1, ["cta-button.width"], []),
        new("P3", "徽章位置", Group.Lab, Css(".hero-badge{left:600px}"), 1, ["hero-badge.offsetX"], []),
        new("P4", "字級", Group.Lab, Css("h1.page-title{font-size:24px}"), 1, ["page-title.fontSize"], []),

        // ---------- G 組:已知缺口,鎖的是現況,不是期望行為 ----------
        // 補上對應的能力後,這些列會紅:把那一列的預期值改成新行為。

        // 按鈕整個不見:擋下來的原因卻是下方 feature-list 的 offsetY(上移 88px),不是「按鈕不見了」;
        // 不見的按鈕只在未配對清單裡,cta-label 的原因是 text-not-found;這張表沒開 gate.failOnUnmatched,所以它不擋
        new("G1", "按鈕不見", Group.Lab, Remove(Button), 1,
            ["feature-list.offsetY"], ["cta-button (no-anchor)", "cta-label (text-not-found)"]),
        // 絕對定位的徽章不見:沒有任何落差,只在未配對清單裡,gate 照樣通過
        new("G2", "徽章不見", Group.Lab, Remove(Badge), 0, [], ["hero-badge (no-anchor)"]),
        // 要擋得用 minMatchRate,但那是「配對可信度」(exit 3:結果不可信),不是「做錯了」(exit 1)
        new("G2-minMatchRate", "徽章不見,gate.minMatchRate: 1", Group.StrictMatch, Remove(Badge), 3, [], ["hero-badge (no-anchor)"]),
        // 標題文字寫錯:圖層名(class page-title)照樣配上,文字內容的落差是軟的:會列出、不擋
        new("G3", "文字寫錯", Group.Lab, Swap(">Parity Demo<", ">Totally Wrong Words<"), 0, ["page-title.text (soft)"], []),
        // 卡片 1、3 整個互換(含 id 與標題):各自照 id / 文字配回原圖層;自動排版的子元件不比相對位置,順序另外比,是軟落差
        new("G4", "順序對調", Group.Lab, Exchange(Card1, Card3), 0, ["feature-list.childOrder (soft)"], []),
        // 設計稿沒有的大紅色區塊:配對以設計端為錨,多出來的元件沒有人看
        new("G5", "多餘元件", Group.Lab,
            Swap("<body>", "<body><div style=\"position:absolute;top:0;left:0;width:800px;height:200px;background:#FF0000\"></div>"),
            0, [], []),
        // 圖片換成另一張同尺寸的:圖片內容預設不比,這張設計稿也沒有參考圖。開了 compare.images 的情況見 ImageComparisonTests
        new("G6", "圖片換錯", Group.Lab, Swap(BlueSvg, RedSvg), 0, [], []),
        // 透明度、邊框、陰影、漸層:軟落差(設計稿要有寫才比,LabDesign 補上了)
        new("G7", "樣式", Group.Lab, Css("button.cta-button{opacity:.2;border:4px solid #000;box-shadow:0 0 30px #000}"), 0,
            ["cta-button.borderWidth (soft)", "cta-button.opacity (soft)", "cta-button.shadow (soft)"], []),
        new("G7-gradient", "漸層", Group.Lab, Css("button.cta-button{background-image:linear-gradient(#ff0000,#0000ff)}"), 0,
            ["cta-button.gradient (soft)"], []),
        // Tailwind 的元素預設就帶這種看不見的陰影:不算有陰影
        new("G7-invisible", "看不見的陰影", Group.Lab, Css("button.cta-button{box-shadow:0 0 #0000,0 0 #0000}"), 0, [], []),
        // 透明的邊框也看不見(對真實 Figma 實測抓到的:Carbon 的按鈕是 border: 1px solid transparent)
        new("G7-transparent-border", "透明的邊框", Group.Lab, Css("button.cta-button{border:2px solid transparent}"), 0, [], []),
        // 藍色分量是 0 的顏色不是透明:rgb(255, 0, 0) 的結尾也是「, 0)」,判斷透明時曾經把它算進去
        new("G7-red-border", "紅色邊框", Group.Lab, Css("button.cta-button{border:2px solid #ff0000}"), 0, ["cta-button.borderWidth (soft)"], []),
        new("G7-half-transparent-border", "半透明的邊框", Group.Lab, Css("button.cta-button{border:2px solid rgba(0,0,0,.5)}"), 0, ["cta-button.borderWidth (soft)"], []),
        // 斜體、底線、大小寫轉換:軟落差
        new("G8", "文字樣式", Group.Lab, Css("h1.page-title{font-style:italic;text-decoration:underline;text-transform:uppercase}"), 0,
            ["page-title.fontStyle (soft)", "page-title.textDecoration (soft)", "page-title.textTransform (soft)"], []),
        // 文字對齊:軟落差
        new("G9", "文字對齊", Group.Lab, Css("button.cta-button{text-align:left}"), 0, ["cta-label.textAlign (soft)"], []),
        // 副標題擠成五行、下方全部推開 96px(G10_… 那條驗這件事真的發生了):文字節點既不比尺寸、也不當位置目標,
        // 其他元件的相對位置是對最近的已配對兄弟算的,跟著一起移就沒有偏移。根因另外比:行數,軟落差
        new("G10", "換行", Group.Lab, Css("p.subtitle{width:60px}"), 0, ["subtitle.lineCount (soft)"], []),
        // 設計稿垂直置中、頁面靠上,差 40px:自動排版的子元件不比相對位置,對齊另外比,是軟落差
        new("G11", "容器對齊", Group.Align, Same, 0, ["align-chip.crossAxisOffset (soft)"], [], Frame: "3:1"),
        // 主軸也一樣:設計稿靠左(MIN),頁面置中
        new("G11-main", "容器主軸對齊", Group.Align, Css(".align-row{justify-content:center}"), 0,
            ["align-chip.crossAxisOffset (soft)", "align-chip.mainAxisOffset (soft)"], [], Frame: "3:1"),
        // 對照(不是缺口):同一頁對「容器不是自動排版」的設計稿,同樣的 40px 是硬落差 offsetY
        new("G11-control", "容器對齊(對照:容器不是自動排版)", Group.Align, Same, 1, ["align-chip.offsetY"], [], Frame: "4:1"),
    ];

    public static TheoryData<string, string> Rows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var c in Table) rows.Add(c.Id, c.What);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Row(string id, string what)
    {
        if (runs.BrowserMissing) { Console.WriteLine(Skipped); return; }
        var c = Table.Single(x => x.Id == id);
        var got = runs.Of(id);

        // 一行寫完三件事:不同時,實跑那一行可以直接拿回來改表格
        Assert.True(Describe(c.Exit, c.Diffs, c.Unmatched) == Describe(got.Exit, DiffsOf(got.Report), UnmatchedOf(got.Report)),
            $"{id} {what}\n  預期 {Describe(c.Exit, c.Diffs, c.Unmatched)}\n  實跑 {Describe(got.Exit, DiffsOf(got.Report), UnmatchedOf(got.Report))}\n\n{got.Console}");
        Assert.Equal(DesignNodes(c.Group), got.Report.Summary.DesignNodes); // frame 指對了:整份設計稿都有拿來比
    }

    [Fact]
    public void G10_the_wrapped_subtitle_pushes_everything_below_down_96px()
    {
        // G10 那一列「零落差」的前提:頁面真的變了。60px 寬放不下任何一個字,副標題變成一字一行(24 → 120 高),
        // 下面每個元件都往下推 96px——量得到,只是沒有一條規則拿它來比
        if (runs.BrowserMissing) { Console.WriteLine(Skipped); return; }
        Box Rendered(string id, string layer) => runs.Of(id).Report.Nodes.Single(n => n.DesignLayer == layer).RenderedBox;

        Assert.Equal(24, Rendered("00", "subtitle").H);
        Assert.Equal(120, Rendered("G10", "subtitle").H);
        foreach (var layer in new[] { "cta-button", "cta-label", "feature-list", "feature-card-1", "hero-image" })
            Assert.True(Rendered("G10", layer).Y - Rendered("00", layer).Y == 96,
                $"{layer}: 00 y={Rendered("00", layer).Y}, G10 y={Rendered("G10", layer).Y}");
    }

    private static int DesignNodes(Group g) => g == Group.Align ? 2 : 13;

    private static string Describe(int exit, IEnumerable<string> diffs, IEnumerable<string> unmatched)
        => $"exit {exit}; diffs [{string.Join(", ", diffs.Order(StringComparer.Ordinal))}]; " +
           $"unmatched [{string.Join(", ", unmatched.Order(StringComparer.Ordinal))}]";

    private static IEnumerable<string> DiffsOf(FidelityReport r)
        => r.Nodes.SelectMany(n => n.Diffs.Select(d => $"{n.DesignLayer}.{d.Prop}" + (d.Soft ? " (soft)" : "")));

    private static IEnumerable<string> UnmatchedOf(FidelityReport r)
        => r.Unmatched.Select(u => $"{u.DesignLayer} ({u.Reason})");

    // ---------- 執行 ----------

    public sealed record Outcome(int Exit, FidelityReport Report, string Console);

    /// <summary>每組跑一次 check(一個瀏覽器、該組全部的 target),再拆回逐列的結果。</summary>
    public sealed class LabRuns : IAsyncLifetime
    {
        private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m");
        private static readonly Regex ReasonLine = new(@"^\s*·\s*(?<route>/\S+?): ", RegexOptions.Multiline);

        private readonly Dictionary<string, Outcome> _outcomes = [];

        public bool BrowserMissing { get; private set; }

        public Outcome Of(string id) => _outcomes[id];

        public async Task InitializeAsync()
        {
            foreach (var group in Table.GroupBy(c => c.Group))
            {
                var cases = group.ToList();
                if (cases.Count > 1 && cases.Any(c => c.Exit == 3))
                    throw new InvalidOperationException(
                        $"group {group.Key}: a row that expects exit 3 must be alone in its group — " +
                        "match integrity is decided for the whole run, so the other rows' gate verdicts would not be printed.");

                using var dir = new TempDir();
                var (design, page) = group.Key == Group.Align ? (AlignDesign, AlignPage) : (LabDesign(), LabPage);
                dir.Write("design.json", design);
                foreach (var c in cases) dir.Write($"{c.Id}.html", c.Page(page));
                var config = dir.Write("parity.config.json", Config(group.Key, cases));

                var r = await Cli.RunAsync("check", "--config", config);
                if (Cli.BrowserMissing(r)) { BrowserMissing = true; return; }
                if (r.ExitCode is not (0 or 1 or 3))
                    throw new InvalidOperationException($"group {group.Key}: check exited {r.ExitCode}\n{r.Out}\n{r.Err}");

                var console = Ansi.Replace(r.Out, "");
                var listed = ReasonLine.Matches(console).Select(m => m.Groups["route"].Value).ToHashSet();
                if ((r.ExitCode == 0) != (listed.Count == 0))
                    throw new InvalidOperationException(
                        $"group {group.Key}: exit {r.ExitCode} but {listed.Count} per-page reason line(s) — the console format changed?\n{console}");

                var doc = JsonSerializer.Deserialize<ReportDocument>(
                    File.ReadAllText(dir.File(".parity", "report.json")), ReportWire.Compact)!;
                foreach (var c in cases)
                {
                    var route = "/" + c.Id;
                    _outcomes[c.Id] = new Outcome(
                        listed.Contains(route) ? r.ExitCode : 0,
                        doc.Reports.Single(x => x.Route == route),
                        Regex.Match(console, $@"(?ms)^target {Regex.Escape(route)} → .*?(?=^target |^report written)").Value);
                }
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>samples/demo 的設計稿,根節點的 children 再加一張 hero-image(頁面上的 img.hero-image)。</summary>
        private static string LabDesign()
        {
            var root = JsonNode.Parse(Repo.Read("samples", "demo", "design.json"))!;
            root["children"]!.AsArray().Add(JsonNode.Parse("""
                { "id": "1:14", "name": "hero-image", "type": "Shape",
                  "box": { "x": 40, "y": 400, "width": 120, "height": 64 }, "children": [] }
                """));

            // 樣式屬性要設計稿有寫才比:Figma 來的節點都有,手寫的 demo 沒有,這裡補上(值與基準頁相同,列 00 才會零落差)
            IEnumerable<JsonNode> Walk(JsonNode n) =>
                new[] { n }.Concat((n["children"]?.AsArray() ?? []).SelectMany(c => Walk(c!)));
            var byName = Walk(root).ToDictionary(n => (string)n["name"]!);

            var title = byName["page-title"]["text"]!;
            title["fontStyle"] = "normal";
            title["textDecoration"] = "none";
            title["textTransform"] = "none";
            byName["subtitle"]["text"]!["lineHeight"] = 24;
            byName["cta-label"]["text"]!["textAlign"] = "center";
            var button = byName["cta-button"];
            button["opacity"] = 1;
            button["borderWidth"] = JsonNode.Parse("""{ "top": 0, "right": 0, "bottom": 0, "left": 0 }""");
            button["shadows"] = new JsonArray();
            return root.ToJsonString();
        }

        private static string Config(Group group, IEnumerable<Case> cases)
        {
            var targets = new JsonArray();
            foreach (var c in cases)
                targets.Add(new JsonObject { ["route"] = "/" + c.Id, ["frame"] = c.Frame, ["url"] = $"./{c.Id}.html" });
            var gate = new JsonObject { ["failOn"] = new JsonArray("critical", "serious") };
            if (group == Group.StrictMatch) gate["minMatchRate"] = 1;
            return new JsonObject
            {
                ["designFile"] = "design.json",
                ["targets"] = targets,
                ["compare"] = new JsonObject { ["position"] = "relative" },
                ["gate"] = gate,
            }.ToJsonString();
        }
    }
}
