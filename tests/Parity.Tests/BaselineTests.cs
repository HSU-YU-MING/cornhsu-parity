using Microsoft.EntityFrameworkCore;
using Parity.Engine;
using Parity.Storage;

namespace Parity.Tests;

public class BaselineComparerTests
{
    private static DiffRecord D(string layer, string prop, Severity sev, string route = "/")
        => new(route, layer, "sel", prop, sev, "expected", "actual");

    [Fact]
    public void Soft_diffs_never_enter_the_baseline_or_the_comparison()
    {
        // 決策 #2(2.0.0):soft 落差(font-family)不擋 gate,--baseline 也一樣。1.x 把 soft 一起攤平,
        // 調整 font stack 就讓 --baseline 多一條「新增」、exit 1
        var node = new NodeResult("title", "1:2", "h1", "auto-text", Severity.Medium,
        [
            new PropDiff("fontSize", "32", "30", "px", 2, 0.5, Severity.Medium),
            new PropDiff("fontFamily", "Inter", "Arial", null, null, 0, Severity.Minor, Soft: true),
        ]);
        var report = new FidelityReport("/", "u", "d", [node], [], new ReportSummary(1, 1, 0, 1, 0, 0, 1, 1, Severity.Medium));

        var records = DiffRecord.FromReports([report]);

        Assert.Equal(["fontSize"], records.Select(r => r.Prop));
        Assert.False(BaselineComparer.Compare(records, [D("title", "fontSize", Severity.Medium) with { Selector = "h1" }]).HasRegressions);
    }

    [Fact]
    public void A_soft_row_in_an_old_baseline_is_not_fixed_while_that_diff_is_still_present()
    {
        // 審查實證:1.x 存的 baseline 有 soft 列(page-title · fontFamily)。2.0.0 第一版不把 soft 攤進現況,
        // 舊列就**每一次**比對都算「修好」——PR 留言列出「🟢 Fixed: page-title · fontFamily」,同一條落差卻還在
        // 落差表裡。Fixed 要拿全部現況落差(含 soft)的鍵來比,不寫死哪個屬性
        static FidelityReport Report(params PropDiff[] diffs)
            => new("/", "u", "d", [new NodeResult("title", "1:2", "h1", "auto-text", Severity.Medium, diffs)], [],
                new ReportSummary(1, 1, 0, 1, 0, 0, 1, 0, Severity.Medium));
        var fontSize = new PropDiff("fontSize", "32", "30", "px", 2, 0.5, Severity.Medium);
        var fontFamily = new PropDiff("fontFamily", "Inter", "Arial", null, null, 0, Severity.Minor, Soft: true);
        DiffRecord[] oldBaseline =
        [
            D("title", "fontSize", Severity.Medium) with { Selector = "h1" },
            D("title", "fontFamily", Severity.Minor) with { Selector = "h1" }, // 1.x 連 soft 一起存
        ];

        var stillThere = Report(fontSize, fontFamily);
        var same = BaselineComparer.Compare(DiffRecord.FromReports([stillThere]), oldBaseline, DiffRecord.KeysOf([stillThere]));
        Assert.Empty(same.Fixed);
        // 驗證者:baseline 有 2 列,結果卻是 fixed 0、unchanged 1——soft 那列不在任何計數裡。另計 ignored,總數對得起來
        Assert.Equal(1, same.Ignored);
        Assert.Equal(oldBaseline.Length, same.Fixed.Count + same.Worsened.Count + same.Unchanged + same.Ignored);
        Assert.False(same.HasRegressions);

        var reallyFixed = Report(fontSize); // font stack 真的改對了才算修好
        var repaired = BaselineComparer.Compare(DiffRecord.FromReports([reallyFixed]), oldBaseline, DiffRecord.KeysOf([reallyFixed]));
        Assert.Equal(["fontFamily"], repaired.Fixed.Select(d => d.Prop));
    }

    [Fact]
    public void A_non_soft_minor_diff_still_enters_the_baseline()
    {
        // 審查的 M02c:把「排除 soft」寫成「排除 minor」也能通過上面幾條——soft 落差剛好都是 minor。
        // 非 soft 的 minor(例如 1px 的 fontSize)照樣要進 baseline,否則 --baseline 永遠看不到它變嚴重
        var node = new NodeResult("title", "1:2", "h1", "auto-text", Severity.Minor,
        [
            new PropDiff("fontSize", "32", "31", "px", 1, 0.5, Severity.Minor),
            new PropDiff("fontFamily", "Inter", "Arial", null, null, 0, Severity.Minor, Soft: true),
        ]);
        var report = new FidelityReport("/", "u", "d", [node], [], new ReportSummary(1, 1, 0, 1, 0, 0, 0, 2, Severity.Minor));

        var record = Assert.Single(DiffRecord.FromReports([report]));

        Assert.Equal(("fontSize", Severity.Minor), (record.Prop, record.Severity));
    }

    [Fact]
    public void New_diff_not_in_baseline_is_regression()
    {
        var baseline = new[] { D("btn", "color", Severity.Serious) };
        var current = new[] { D("btn", "color", Severity.Serious), D("title", "fontSize", Severity.Medium) };

        var c = BaselineComparer.Compare(current, baseline);

        var reg = Assert.Single(c.Regressions);
        Assert.Equal("title", reg.DesignLayer);
        Assert.Equal(1, c.Unchanged);
        Assert.True(c.HasRegressions);
    }

    [Fact]
    public void Worse_severity_on_same_diff_is_worsened()
    {
        var baseline = new[] { D("btn", "color", Severity.Medium) };
        var current = new[] { D("btn", "color", Severity.Critical) };

        var c = BaselineComparer.Compare(current, baseline);

        Assert.Single(c.Worsened);
        Assert.Empty(c.Regressions);
        Assert.True(c.HasRegressions);
    }

    [Fact]
    public void Diff_gone_from_current_is_fixed_not_regression()
    {
        var baseline = new[] { D("btn", "color", Severity.Serious) };

        var c = BaselineComparer.Compare([], baseline);

        Assert.Single(c.Fixed);
        Assert.False(c.HasRegressions);
    }

    [Fact]
    public void Identical_sets_have_no_regression()
    {
        var set = new[] { D("btn", "color", Severity.Serious), D("t", "fontSize", Severity.Medium) };

        var c = BaselineComparer.Compare(set, set);

        Assert.False(c.HasRegressions);
        Assert.Equal(2, c.Unchanged);
    }

    [Fact]
    public void Improved_severity_is_not_a_regression()
    {
        // critical → medium:變好了,不算回歸(也不算 fixed,因為同一條還在)
        var baseline = new[] { D("btn", "color", Severity.Critical) };
        var current = new[] { D("btn", "color", Severity.Medium) };

        var c = BaselineComparer.Compare(current, baseline);

        Assert.False(c.HasRegressions);
        Assert.Empty(c.Worsened);
        Assert.Equal(1, c.Unchanged);
    }

    [Fact]
    public void Same_layer_and_prop_but_different_route_are_distinct()
    {
        var baseline = new[] { D("btn", "color", Severity.Serious, route: "/") };
        var current = new[] { D("btn", "color", Severity.Serious, route: "/pricing") };

        var c = BaselineComparer.Compare(current, baseline);

        Assert.Single(c.Regressions); // /pricing 的是新的
        Assert.Single(c.Fixed);       // / 的不見了
    }

    [Fact]
    public void Same_layer_and_prop_but_different_selector_are_distinct()
    {
        // 重複圖層名("Text"),不同元素(不同 selector)→ 不能被當成同一條
        var baseline = new[] { new DiffRecord("/", "Text", "body > h1", "color", Severity.Serious, "#fff", "#000") };
        var current = new[] { new DiffRecord("/", "Text", "body > h2", "color", Severity.Serious, "#fff", "#000") };

        var c = BaselineComparer.Compare(current, baseline);

        Assert.Single(c.Regressions); // h2 是新的
        Assert.Single(c.Fixed);       // h1 不見了
    }
}

public class BaselineStoreTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"parity-test-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Save_and_get_latest_roundtrips()
    {
        var path = TempDb();
        try
        {
            await using (var store = new BaselineStore(path))
                await store.SaveAsync(
                    [new DiffRecord("/", "btn", "sel", "color", Severity.Serious, "e", "a")],
                    new DateTime(2026, 7, 16), commit: "abc1234", branch: "main", score: 83);

            await using (var store = new BaselineStore(path))
            {
                var latest = await store.GetLatestAsync();
                Assert.NotNull(latest);
                var d = Assert.Single(latest.Diffs);
                Assert.Equal("btn", d.DesignLayer);
                Assert.Equal(Severity.Serious, d.Severity);
                Assert.Equal(83, latest.Score);
                var h = Assert.Single(await store.HistoryAsync());
                Assert.Equal(83, h.Score);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Get_latest_returns_newest_snapshot()
    {
        var path = TempDb();
        try
        {
            await using var store = new BaselineStore(path);
            await store.SaveAsync([new DiffRecord("/", "old", "s", "color", Severity.Minor, "e", "a")],
                new DateTime(2026, 7, 1));
            await store.SaveAsync([new DiffRecord("/", "new", "s", "color", Severity.Minor, "e", "a")],
                new DateTime(2026, 7, 16));

            var latest = await store.GetLatestAsync();
            Assert.Equal("new", Assert.Single(latest!.Diffs).DesignLayer);
            Assert.Null(latest.Score); // 沒給分數的快照(舊版行為)→ null,不炸
            Assert.Equal(2, (await store.HistoryAsync()).Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task No_baseline_returns_null()
    {
        var path = TempDb();
        try
        {
            await using var store = new BaselineStore(path);
            Assert.Null(await store.GetLatestAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Adopts_legacy_ensurecreated_db_without_migration_history()
    {
        var path = TempDb();
        try
        {
            // 模擬 0.9.x 之前:EnsureCreated 建的 db——有資料表,沒有 __EFMigrationsHistory
            var options = new DbContextOptionsBuilder<BaselineDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var legacy = new BaselineDbContext(options))
                legacy.Database.EnsureCreated();

            // 用 BaselineStore 開它:應無痛接管(不因 InitialCreate 撞既存表而爆),照常讀寫
            await using (var store = new BaselineStore(path))
            {
                await store.SaveAsync(
                    [new DiffRecord("/", "btn", "sel", "color", Severity.Serious, "e", "a")],
                    new DateTime(2026, 7, 22), score: 90);
                var latest = await store.GetLatestAsync();
                Assert.Equal(90, latest!.Score);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Adopts_pre_score_era_db_by_adding_the_missing_column()
    {
        // v0.13.0 三通路驗證實測抓到的(parity-action-test 的 db 是 0.2.0 建的):
        // 0.6.0 之前的 db 連 Score 欄都沒有——「legacy schema 恰好等於 InitialCreate」
        // 的接管假設只對 0.6+ 成立,標完 InitialCreate 後 EF 認定 schema 已最新,
        // 缺欄永遠不會補 → 'no such column: s.Score'。
        // 上面那條測試抓不到它:EnsureCreated 用的是「現在的」model,天生就有 Score。
        var path = TempDb();
        try
        {
            var options = new DbContextOptionsBuilder<BaselineDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var legacy = new BaselineDbContext(options))
            {
                // 0.2.0 的實際 schema(raw SQL,不經現在的 model):Snapshots 沒有 Score
                await legacy.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE "Snapshots" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Snapshots" PRIMARY KEY AUTOINCREMENT,
                        "CreatedAt" TEXT NOT NULL, "Commit" TEXT NULL, "Branch" TEXT NULL);
                    CREATE TABLE "Diffs" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Diffs" PRIMARY KEY AUTOINCREMENT,
                        "SnapshotId" INTEGER NOT NULL, "Route" TEXT NOT NULL, "DesignLayer" TEXT NOT NULL,
                        "Selector" TEXT NOT NULL, "Prop" TEXT NOT NULL, "Severity" TEXT NOT NULL,
                        "Expected" TEXT NOT NULL, "Actual" TEXT NOT NULL,
                        CONSTRAINT "FK_Diffs_Snapshots_SnapshotId" FOREIGN KEY ("SnapshotId") REFERENCES "Snapshots" ("Id") ON DELETE CASCADE);
                    CREATE INDEX "IX_Diffs_SnapshotId" ON "Diffs" ("SnapshotId");
                    INSERT INTO "Snapshots" ("CreatedAt", "Commit", "Branch") VALUES ('2026-07-01 00:00:00', 'abc123', 'main');
                    INSERT INTO "Diffs" ("SnapshotId", "Route", "DesignLayer", "Selector", "Prop", "Severity", "Expected", "Actual")
                        VALUES (1, '/', 'btn', 'sel', 'color', 'Serious', 'e', 'a');
                    """);
            }

            await using (var store = new BaselineStore(path))
            {
                var legacyLatest = await store.GetLatestAsync(); // 舊資料要讀得動(Score 為 null)
                Assert.NotNull(legacyLatest);
                Assert.Null(legacyLatest!.Score);
                Assert.Equal("btn", Assert.Single(legacyLatest.Diffs).DesignLayer);

                await store.SaveAsync(
                    [new DiffRecord("/", "btn", "sel", "color", Severity.Serious, "e", "a")],
                    new DateTime(2026, 8, 11), score: 88);
                var latest = await store.GetLatestAsync();
                Assert.Equal(88, latest!.Score); // 新資料寫得進補上的欄
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
