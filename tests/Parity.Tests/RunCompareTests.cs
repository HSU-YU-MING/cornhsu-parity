using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Parity.Engine;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// 「跟上次比變了什麼」(M4.5 必補 #3):同專案同分支的前一筆、
/// 新增/修好的判定走引擎 BaselineComparer 同語意、跨分支不互相比。
/// </summary>
public class RunCompareTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private ServerDbContext _db = null!;
    private readonly Guid _userId = Guid.NewGuid();
    private Guid _projectId;
    private Guid _first, _second, _prBranch;

    private static string ReportWith(params (string Layer, string Prop, Severity Sev)[] diffs)
    {
        var nodes = diffs.Select(d => new NodeResult(d.Layer, "id", "#" + d.Layer, "auto-name", d.Sev,
            [new PropDiff(d.Prop, "12", "8", "px", 4, 2, d.Sev)])).ToList();
        var report = new FidelityReport("/", "u", "figmakey123", nodes, [],
            new ReportSummary(nodes.Count, nodes.Count, 0, nodes.Count, 0, 0, 0, 0, Severity.Serious));
        return JsonSerializer.Serialize(ReportDocument.Of([report]), ReportWire.Compact);
    }

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        await _conn.OpenAsync();
        _db = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(_conn).Options);
        await _db.Database.MigrateAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "p",
            TokenHash = ProjectToken.Hash(ProjectToken.Generate()),
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        _projectId = project.Id;
        _db.Projects.Add(project);
        _db.Users.Add(new AppUser { Id = _userId, UserName = "u@t", Email = "u@t" });
        _db.Members.Add(new ProjectMember
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            UserId = _userId,
            Role = ProjectRole.Viewer,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        var t0 = DateTimeOffset.UnixEpoch;
        // 第一筆(main):cta 的 paddingTop + title 的 fontSize
        Run Make(Guid id, DateTimeOffset at, string? branch, string raw) => new()
        {
            Id = id,
            ProjectId = project.Id,
            CreatedAt = at,
            Branch = branch,
            Score = 50,
            GateFailed = true,
            RawReportGzip = ReportBlob.Compress(raw),
        };
        _first = Guid.NewGuid(); _second = Guid.NewGuid(); _prBranch = Guid.NewGuid();
        _db.Runs.Add(Make(_first, t0, "main",
            ReportWith(("cta", "paddingTop", Severity.Serious), ("title", "fontSize", Severity.Serious))));
        // 第二筆(main):paddingTop 修好了、新增 background
        _db.Runs.Add(Make(_second, t0.AddHours(1), "main",
            ReportWith(("title", "fontSize", Severity.Serious), ("cta", "background", Severity.Critical))));
        // 另一分支插在中間——不得被當成 main 的「上一筆」
        _db.Runs.Add(Make(_prBranch, t0.AddMinutes(30), "pr-1", ReportWith()));
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Second_run_reports_new_and_fixed_vs_same_branch_previous()
    {
        var changes = await RunCompare.ChangesAsync(_db, _userId, _second);

        Assert.NotNull(changes);
        Assert.Equal(_first, changes!.PrevRunId); // 跳過 pr-1,取同分支的前一筆
        Assert.Equal("background", Assert.Single(changes.Regressions).Prop); // 新增
        Assert.Equal("paddingTop", Assert.Single(changes.Fixed).Prop);       // 修好
        Assert.Empty(changes.Worsened);
        Assert.Equal(1, changes.Unchanged); // title/fontSize 兩邊都在
    }

    [Fact]
    public async Task First_run_has_no_previous_and_strangers_get_null()
    {
        var first = await RunCompare.ChangesAsync(_db, _userId, _first);
        Assert.NotNull(first);
        Assert.Null(first!.PrevRunId); // 該分支第一筆:沒得比,不是錯誤

        Assert.Null(await RunCompare.ChangesAsync(_db, Guid.NewGuid(), _second)); // 非成員 → null(404)
    }
}
