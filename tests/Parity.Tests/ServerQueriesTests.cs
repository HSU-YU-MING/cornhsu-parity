using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>M4 讀取查詢:總覽卡的 last/prev 語意與趨勢的時間排序。</summary>
public class ServerQueriesTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private ServerDbContext _db = null!;
    private Guid _projectId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        await _conn.OpenAsync();
        _db = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite(_conn).Options);
        await _db.Database.MigrateAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "demo",
            TokenHash = ProjectToken.Hash(ProjectToken.Generate()),
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        _projectId = project.Id;
        _db.Projects.Add(project);

        // 三次 run:route "/" 分數 60 → 75 → 70;route "/about" 只在最後一次出現(90)
        var t0 = DateTimeOffset.UnixEpoch;
        for (var (i, scores) = (0, new[] { 60, 75, 70 }); i < scores.Length; i++)
        {
            var run = new Run
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                CreatedAt = t0.AddHours(i),
                Score = scores[i],
                GateFailed = scores[i] < 75,
                CommitSha = $"sha{i}",
                RawReportGzip = ReportBlob.Compress("{}"),
            };
            run.Pages.Add(Page("/", scores[i]));
            if (i == 2) run.Pages.Add(Page("/about", 90));
            _db.Runs.Add(run);
        }
        await _db.SaveChangesAsync();
    }

    private static PageResult Page(string route, int score) => new()
    {
        Id = Guid.NewGuid(),
        Route = route,
        Url = $"http://localhost{route}",
        Score = score,
        MaxSeverity = "none",
    };

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Overview_gives_last_and_prev_per_route()
    {
        var cards = await Queries.OverviewAsync(_db);

        Assert.Equal(2, cards.Count); // 專案×route 各一張
        var home = cards.Single(c => c.Route == "/");
        Assert.Equal(70, home.LastScore);       // 最新一次
        Assert.Equal(75, home.PrevScore);       // 上一次(方向箭頭的依據)
        Assert.Equal(3, home.RunCount);
        Assert.True(home.LastGateFailed);       // 70 < 75 → 最新那次是紅的

        var about = cards.Single(c => c.Route == "/about");
        Assert.Equal(90, about.LastScore);
        Assert.Null(about.PrevScore);           // 只出現過一次 → 沒有方向
        Assert.Equal(1, about.RunCount);
    }

    [Fact]
    public async Task Trend_is_oldest_to_newest()
    {
        var points = await Queries.TrendAsync(_db, _projectId, "/");

        Assert.Equal([60, 75, 70], points.Select(p => p.Score)); // 舊 → 新,畫圖的方向
        Assert.Equal("sha0", points[0].CommitSha);
        Assert.True(points[0].At < points[2].At);
    }
}
