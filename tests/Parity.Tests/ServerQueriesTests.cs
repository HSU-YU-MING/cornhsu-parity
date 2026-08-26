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
    private readonly Guid _memberId = Guid.NewGuid();   // 專案成員(viewer)
    private readonly Guid _strangerId = Guid.NewGuid(); // 非成員——什麼都不該看到

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

        // M3:讀取一律走成員資格過濾——member 看得到,stranger 看不到
        foreach (var uid in new[] { _memberId, _strangerId })
            _db.Users.Add(new AppUser { Id = uid, UserName = $"{uid}@t", Email = $"{uid}@t" });
        _db.Members.Add(new ProjectMember
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            UserId = _memberId,
            Role = ProjectRole.Viewer,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        // 三次 main run:route "/" 分數 60 → 75 → 70;route "/about" 只在最後一次出現(90)
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
                Branch = "main",
                RawReportGzip = ReportBlob.Compress("{}"),
            };
            run.Pages.Add(Page("/", scores[i]));
            if (i == 2) run.Pages.Add(Page("/about", 90));
            _db.Runs.Add(run);
        }
        // 一筆更晚的 PR 分支 run(分數 5)——分支過濾要把它隔離在 main 的視圖之外
        var pr = new Run
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CreatedAt = t0.AddHours(9),
            Score = 5,
            GateFailed = true,
            CommitSha = "prsha",
            Branch = "pr-1",
            RawReportGzip = ReportBlob.Compress("{}"),
        };
        pr.Pages.Add(Page("/", 5));
        _db.Runs.Add(pr);
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
        var cards = await Queries.OverviewAsync(_db, _memberId, branch: "main");

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
        var points = await Queries.TrendAsync(_db, _memberId, _projectId, "/", branch: "main");

        Assert.Equal([60, 75, 70], points.Select(p => p.Score)); // 舊 → 新,畫圖的方向
        Assert.Equal("sha0", points[0].CommitSha);
        Assert.True(points[0].At < points[2].At);
    }

    [Fact]
    public async Task Branch_filter_keeps_pr_runs_out_of_main_views()
    {
        // 不過濾 → PR 那筆最晚,把 "/" 的最新分數拉成 5(這正是 M4.5 要修的污染)
        var unfiltered = await Queries.OverviewAsync(_db, _memberId);
        Assert.Equal(5, unfiltered.Single(c => c.Route == "/").LastScore);

        // 過濾 main → 污染消失
        var main = await Queries.OverviewAsync(_db, _memberId, branch: "main");
        Assert.Equal(70, main.Single(c => c.Route == "/").LastScore);

        // 趨勢同語意
        Assert.Equal(4, (await Queries.TrendAsync(_db, _memberId, _projectId, "/")).Count);
        Assert.Equal(3, (await Queries.TrendAsync(_db, _memberId, _projectId, "/", branch: "main")).Count);
        Assert.Single(await Queries.TrendAsync(_db, _memberId, _projectId, "/", branch: "pr-1"));

        // 分支清單:近 → 遠
        var branches = await Queries.BranchesAsync(_db, _memberId);
        Assert.Equal(["pr-1", "main"], branches.Select(b => b.Branch));
    }

    [Fact]
    public async Task Non_members_see_nothing()
    {
        Assert.Empty(await Queries.OverviewAsync(_db, _strangerId));
        Assert.Empty(await Queries.TrendAsync(_db, _strangerId, _projectId, "/")); // 連指名專案也擋
    }

    [Fact]
    public async Task Last_owner_cannot_be_removed()
    {
        var ownerId = Guid.NewGuid();
        _db.Users.Add(new AppUser { Id = ownerId, UserName = "o@t", Email = "o@t" });
        _db.Members.Add(new ProjectMember
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            UserId = ownerId,
            Role = ProjectRole.Owner,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await _db.SaveChangesAsync();

        Assert.True(await Membership.WouldRemoveLastOwnerAsync(_db, _projectId, ownerId));  // 唯一 owner → 擋
        Assert.False(await Membership.WouldRemoveLastOwnerAsync(_db, _projectId, _memberId)); // viewer 隨時可移
        Assert.True(await Membership.IsOwnerAsync(_db, ownerId, _projectId));
        Assert.False(await Membership.IsOwnerAsync(_db, _memberId, _projectId));
    }
}

/// <summary>邀請生命週期:單次使用、過期、角色驗證、重邀覆寫角色。</summary>
public class InviteLifecycleTests
{
    [Fact]
    public async Task Invite_is_single_use_and_expires()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite(conn).Options);
        await db.Database.MigrateAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "p",
            TokenHash = ProjectToken.Hash(ProjectToken.Generate()),
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        db.Projects.Add(project);
        var user = new AppUser { Id = Guid.NewGuid(), UserName = "a@t", Email = "a@t" };
        db.Users.Add(user);

        var now = DateTimeOffset.UnixEpoch;
        var (invite, token) = Invites.Create(project.Id, "a@t", ProjectRole.Viewer, now);
        db.Invites.Add(invite);
        await db.SaveChangesAsync();

        Assert.NotNull(await Invites.FindUsableAsync(db, token, now));                       // 有效
        Assert.Null(await Invites.FindUsableAsync(db, token, now + TimeSpan.FromDays(8)));   // 過期
        Assert.Null(await Invites.FindUsableAsync(db, "not-a-real-token-aaaaaaaa", now));    // 假 token

        await Invites.AcceptAsync(db, invite, user.Id, now);
        Assert.Null(await Invites.FindUsableAsync(db, token, now)); // 單次使用:接受後即失效
        Assert.Equal(ProjectRole.Viewer,
            (await db.Members.SingleAsync(m => m.UserId == user.Id)).Role);

        // 重邀同人 → 覆寫角色而不是撞 unique index
        var (invite2, _) = Invites.Create(project.Id, "a@t", ProjectRole.Owner, now);
        db.Invites.Add(invite2);
        await db.SaveChangesAsync();
        await Invites.AcceptAsync(db, invite2, user.Id, now);
        Assert.Equal(ProjectRole.Owner,
            (await db.Members.SingleAsync(m => m.UserId == user.Id)).Role);

        Assert.Throws<ArgumentException>(() => Invites.Create(project.Id, "a@t", "admin", now)); // 未知角色
    }
}
