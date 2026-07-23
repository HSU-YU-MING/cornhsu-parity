using System.Text.Json;
using Parity.Cli;
using Parity.Engine;

namespace Parity.Tests;

/// <summary>
/// report.json 的回讀:parity report 靠「寫出去的 JSON 讀得回來」——enum 字串、camelCase、
/// 巢狀 record 都不能在序列化/反序列化間漂移。
/// </summary>
public class ReportRoundTripTests
{
    [Fact]
    public void Report_json_round_trips()
    {
        var diff = new PropDiff("fontSize", "32", "30", "px", 2, 0.5, Severity.Serious);
        var node = new NodeResult("cta", "1:4", ".cta", "auto-text", Severity.Serious, [diff],
            new Parity.Engine.Model.Box(0, 0, 160, 48), new Parity.Engine.Model.Box(0, 0, 160, 48));
        var report = new FidelityReport("/", "http://x", "design.json",
            [node], [new UnmatchedNode("badge", "1:13", "no-anchor")],
            new ReportSummary(2, 1, 1, 1, 0, 1, 0, 0, Severity.Serious));

        var json = JsonSerializer.Serialize(ReportDocument.Of([report]), ReportJson.Indented);
        var back = JsonSerializer.Deserialize<ReportDocument>(json, ReportJson.Indented);

        Assert.NotNull(back);
        Assert.Equal(ReportDocument.CurrentSchemaVersion, back.SchemaVersion);
        var r = Assert.Single(back.Reports);
        var n = Assert.Single(r.Nodes);
        var d = Assert.Single(n.Diffs);
        Assert.Equal(Severity.Serious, d.Severity);
        Assert.Equal("32", d.Expected);
        Assert.Equal(0.5, d.Tolerance);
        Assert.Equal(160, n.DesignBox.W);
        Assert.Equal("no-anchor", Assert.Single(r.Unmatched).Reason);
        Assert.Equal(1, r.Summary.Matched);
    }
}
