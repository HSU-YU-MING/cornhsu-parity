using System.Text.Json;
using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// parity report:從既有 report.json 重生 Markdown(不重掃)。CI 上傳了 report.json artifact,
/// 本機要能重現同一份報告——讀端寬嚴、預設路徑、gate 依 config 重算,都是使用者看得到的行為。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class ReportCommandTests
{
    private static FidelityReport SampleReport(int matched = 1)
    {
        var diffs = new List<PropDiff>
        {
            new("paddingTop", "12", "8", "px", 4, 2, Severity.Serious),
            new("background", "#2563EB", "#2564EC", null, 0.3, 2, Severity.Medium),
        };
        var nodes = matched == 0
            ? new List<NodeResult>()
            : [new NodeResult("CTA", "1:2", "#cta", "auto-name", Severity.Serious, diffs)];
        return new FidelityReport("/", "http://x/", "design.json#",
            nodes, [new UnmatchedNode("Badge", "1:9", "no-anchor")],
            new ReportSummary(matched + 1, matched, 1, matched, 0, matched, matched, 0,
                matched == 0 ? Severity.None : Severity.Serious));
    }

    /// <summary>設定檔 + 預設落點的 .parity/report.json(check 寫出的位置)。</summary>
    private static string Setup(TempDir dir, string? configJson = null, FidelityReport? report = null)
    {
        dir.Write(Path.Combine(".parity", "report.json"),
            JsonSerializer.Serialize(ReportDocument.Of([report ?? SampleReport()]), ReportWire.Indented));
        return dir.Write("parity.config.json", configJson ?? """{ "targets": [] }""");
    }

    [Fact]
    public async Task Md_flag_writes_the_markdown_file_with_a_row_per_prop()
    {
        using var dir = new TempDir();
        var config = Setup(dir);
        var md = dir.File("out", "nested", "report.md"); // 目錄不存在也要建

        var r = await Cli.RunAsync("report", "--config", config, "--md", md);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains($"markdown report: {md}", r.Out);
        var text = File.ReadAllText(md);
        Assert.Contains("## Parity — design fidelity report", text);
        Assert.Contains("| `CTA` | paddingTop | `12` → `8` (Δ4) | serious | `padding-top: 12px` |", text);
        Assert.Contains("| `CTA` | background |", text);
        Assert.Contains("`Badge` — no-anchor", text);
        Assert.DoesNotContain("## Parity", r.Out); // 給了 --md 就不再印到 stdout
    }

    [Fact]
    public async Task Without_md_the_report_goes_to_stdout_from_the_default_location()
    {
        using var dir = new TempDir();
        var config = Setup(dir); // 沒給 --in → <設定檔目錄>/.parity/report.json

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("## Parity — design fidelity report", r.Out);
        Assert.Contains("paddingTop", r.Out);
    }

    [Theory]
    [InlineData("""{ "targets": [] }""", "❌ **GATE FAIL**")]                                 // 預設 failOn 含 serious
    [InlineData("""{ "targets": [], "gate": { "failOn": ["critical"] } }""", "✅ PASS")]   // gate 依 config 重算
    public async Task Gate_verdict_is_recomputed_from_the_config(string configJson, string verdict)
    {
        using var dir = new TempDir();
        var config = Setup(dir, configJson);

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(verdict, r.Out);
    }

    [Fact]
    public async Task Match_integrity_problems_show_up_as_a_warning_note()
    {
        using var dir = new TempDir();
        var config = Setup(dir, report: SampleReport(matched: 0));

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("> ⚠️ /: 0/1 design nodes matched", r.Out);
        Assert.Contains("❌ **GATE FAIL**", r.Out);
    }

    [Fact]
    public async Task Tokens_file_from_the_config_names_the_matching_token()
    {
        using var dir = new TempDir();
        dir.Write("tokens.json", """{ "space-3": "12px" }""");
        var config = Setup(dir, """{ "tokensFile": "tokens.json", "targets": [] }""");

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Contains("`padding-top: 12px(token: space-3)`", r.Out);
    }

    [Fact]
    public void Missing_input_file_is_a_file_not_found_with_a_hint()
    {
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", """{ "targets": [] }""");

        var ex = Assert.Throws<FileNotFoundException>(
            () => ReportCommand.Run(["--config", config, "--in", dir.File("nope.json")]));
        Assert.Contains("report not found", ex.Message);
        Assert.Contains("parity check", ex.Message);
    }

    [Theory]
    [InlineData("[]")] // 0.10.0 之前的裸陣列格式
    [InlineData("""{ "schemaVersion": 1, "reports": [{ "route": "/", "url": "u", "designReference": "d", "nodes": [{ "designLayer": "a", "designId": "a", "selector": "s", "matchedBy": "selector", "severity": "blocker", "diffs": [] }], "unmatched": [], "summary": { "maxSeverity": "none" } }] }""")]
    [InlineData("not json")]
    public void Unreadable_report_formats_say_so(string raw)
    {
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", """{ "targets": [] }""");
        var input = dir.Write("in.json", raw);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ReportCommand.Run(["--config", config, "--in", input]));
        Assert.Contains("unrecognized report format", ex.Message);
    }

    [Fact]
    public void A_report_that_is_just_null_is_a_parse_error_not_a_crash()
    {
        // `null` 是合法 JSON:反序列化回 null 而不丟例外,要接住並講出是哪個檔
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", """{ "targets": [] }""");
        var input = dir.Write("in.json", "null");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ReportCommand.Run(["--config", config, "--in", input]));
        Assert.Equal($"could not parse report: {input}", ex.Message);
    }

    [Fact]
    public void Envelope_without_reports_field_says_which_field_is_missing()
    {
        using var dir = new TempDir();
        var config = dir.Write("parity.config.json", """{ "targets": [] }""");
        var input = dir.Write("in.json", """{ "schemaVersion": 1 }""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ReportCommand.Run(["--config", config, "--in", input]));
        Assert.Contains("no `reports` field", ex.Message);
    }

    [Fact]
    public async Task Rerendered_markdown_links_layers_back_to_figma_like_check_md_does()
    {
        // check --md 會把圖層名連回 Figma 節點;同一份報告用 parity report 重生時原本沒有連結
        // (MarkdownReport.Render 沒收到 figmaFileKey)
        using var dir = new TempDir();
        var config = Setup(dir, """{ "figmaFileKey": "qRzJ3fxF1g09NczsrugiXD", "targets": [] }""");

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("[`CTA`](https://www.figma.com/design/qRzJ3fxF1g09NczsrugiXD?node-id=1-2)", r.Out);
        Assert.Contains("[`Badge`](https://www.figma.com/design/qRzJ3fxF1g09NczsrugiXD?node-id=1-9) — no-anchor", r.Out);
    }

    [Fact]
    public async Task A_broken_tokens_file_is_warned_about_but_does_not_stop_the_report()
    {
        // token 提示只是加值:tokensFile 壞了照樣出報告(exit 0 不變),但原本是靜靜少掉提示——要講出來
        using var dir = new TempDir();
        var config = Setup(dir, """{ "tokensFile": "tokens.json", "targets": [] }""");
        dir.Write("tokens.json", "{ not json");

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("paddingTop", r.Out);
        Assert.Contains("warning: tokensFile", r.Err);
        Assert.Contains("is not valid JSON", r.Err);
    }

    [Fact]
    public async Task Report_does_not_care_that_targets_or_tolerances_are_null()
    {
        // 指名欄位的 null 錯誤只在用到該區段時才丟——report 從沒碰過它們,行為(exit 0)不變
        using var dir = new TempDir();
        var config = Setup(dir, """{ "targets": null, "tolerances": null }""");

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("paddingTop", r.Out);
    }

    [Fact]
    public async Task A_fail_on_value_that_would_disable_the_gate_is_an_execution_error()
    {
        // failOn: ["none"] 在 1.x 讓 gate 永遠不因嚴重度失敗、靜默 PASS;2.0.0 起是設定錯誤(exit 2)
        using var dir = new TempDir();
        var config = Setup(dir, """{ "targets": [], "gate": { "failOn": ["none"] } }""");

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(2, r.ExitCode);
        Assert.Contains("gate.failOn contains \"none\"", r.Err);
        Assert.DoesNotContain("PASS", r.Out);
    }

    [Fact]
    public async Task Newer_schema_version_is_read_with_a_warning()
    {
        // 盤點 5.3:report 指令照讀比自己新的 schemaVersion(exit 0);Parity.Server 則回 422、push 在本機就擋。
        // 改成拒收(exit 2)是待決定的 semver 問題——在那之前至少要說「這份報告可能讀不全」。
        // 若決定拒收,把這條改成預期 exit 2
        using var dir = new TempDir();
        var config = Setup(dir);
        var path = dir.File(".parity", "report.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("paddingTop", r.Out);
        Assert.Contains("warning: ", r.Err);
        Assert.Contains("schemaVersion 99", r.Err);
        Assert.DoesNotContain("warning", r.Out); // stdout 是要接管線的 Markdown,警告不能混進去
    }

    [Fact]
    public async Task A_current_schema_version_reads_without_warnings()
    {
        using var dir = new TempDir();
        var config = Setup(dir);

        var r = await Cli.RunAsync("report", "--config", config);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void Without_a_config_anywhere_report_points_at_init()
    {
        using var dir = new TempDir();
        using var cwd = new CwdScope(dir.Path);
        Assert.Null(ParityConfig.FindConfigFile(dir.Path)); // 前提

        var ex = Assert.Throws<FileNotFoundException>(() => ReportCommand.Run([]));
        Assert.Contains("parity init", ex.Message);
    }

    [Fact]
    public async Task Config_is_found_from_cwd_when_not_given()
    {
        using var dir = new TempDir();
        Setup(dir);
        var sub = Directory.CreateDirectory(dir.File("a", "b")).FullName;
        using var cwd = new CwdScope(sub); // 往上找到 dir/parity.config.json,報告也跟著它的目錄

        var r = await Cli.RunAsync("report");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("paddingTop", r.Out);
    }
}
