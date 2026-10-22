using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// 主控台輸出裡來自 repo 內容的字串(設計稿的圖層名、selector、設計值、設定檔的 route…)。
/// 它們帶換行時,換行後的內容落在行首——在 GitHub Actions 裡就成了 runner 會解析的 workflow command
/// (`::warning …`、`::add-mask::`)。gate 繞不過,但不該讓 repo 內容決定 runner 收到什麼指令:
/// 控制字元一律跳脫成字面(`\n`)。console 輸出不屬於凍結的六面。
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class ConsoleTextTests
{
    private static bool AnyLineStartsAWorkflowCommand(string output)
        => output.Replace("\r\n", "\n").Split('\n', '\r').Any(l => l.TrimStart().StartsWith("::", StringComparison.Ordinal));

    [Theory]
    [InlineData("plain layer", "plain layer")]
    [InlineData("card\n::warning::x", "card\\n::warning::x")]
    [InlineData("a\r\nb", "a\\r\\nb")]
    [InlineData("bell\u0007esc\u001b[31m", "bell\\u0007esc\\u001b[31m")]
    [InlineData("tab\tstays", "tab\tstays")]
    public void Control_characters_are_escaped_and_everything_else_is_untouched(string input, string expected)
        => Assert.Equal(expected, ConsoleText.Safe(input));

    [Fact]
    public void Check_output_never_starts_a_line_with_what_the_design_file_says()
    {
        var node = new NodeResult("hero\n::warning::injected", "1:2", "#hero\n::error::sel", "auto-name", Severity.Serious,
            [new PropDiff("fontFamily", "Inter\n::add-mask::x", "Arial", null, null, 0, Severity.Minor, Soft: true)]);
        var report = new FidelityReport("/\n::notice::route", "file:///x", "d#", [node],
            [new UnmatchedNode("badge\n::warning::u", "1:9", "no-anchor")],
            new ReportSummary(2, 1, 1, 1, 0, 0, 0, 1, Severity.Serious));
        var previous = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { CheckCommand.PrintReport(report); }
        finally { Console.SetOut(previous); }

        Assert.False(AnyLineStartsAWorkflowCommand(captured.ToString()), captured.ToString());
        Assert.Contains("hero\\n::warning::injected", captured.ToString());
        Assert.Contains("badge\\n::warning::u", captured.ToString());
    }

    [Fact]
    public async Task Lint_output_never_starts_a_line_with_what_the_design_file_says()
    {
        using var dir = new TempDir();
        dir.Write("tokens.json", """{ "brand": "#2563EB" }""");
        dir.Write("design.json", """
            { "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 400, "height": 300 },
              "children": [
                { "id": "1", "name": "card\n::warning title=x::injected", "type": "frame",
                  "box": { "x": 0, "y": 0, "width": 100, "height": 50 }, "fill": { "r": 255, "g": 0, "b": 0, "a": 1 } }
              ] }
            """);
        var config = dir.Write("parity.config.json",
            """{ "designFile": "design.json", "tokensFile": "tokens.json", "targets": [{ "route": "/" }] }""");

        var r = await Cli.RunAsync("lint", "--config", config);

        Assert.Equal(1, r.ExitCode);
        Assert.False(AnyLineStartsAWorkflowCommand(r.Out), r.Out);
        Assert.Contains("card\\n::warning title=x::injected", r.Out);
    }
}
