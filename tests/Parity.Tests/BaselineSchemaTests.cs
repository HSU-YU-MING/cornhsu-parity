using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Parity.Engine;
using Parity.Storage;

namespace Parity.Tests;

/// <summary>
/// 凍結面 5(baseline SQLite schema)。parity.baseline.db 會被 commit 進使用者的 repo,
/// 再被**不同版本**的 parity 打開——表名、欄名、可空性、Severity 的存法任何一項走樣,
/// 舊 db 就讀不懂或寫不進。這裡用 SQLite 自己的 PRAGMA 看實際落地的 schema,不經 EF 的 model。
/// </summary>
public class BaselineSchemaTests
{
    private static async Task<string> CreateDbAsync(TempDir dir, params DiffRecord[] diffs)
    {
        var path = dir.File("parity.baseline.db");
        await using var store = new BaselineStore(path);
        await store.SaveAsync(diffs, new DateTime(2026, 7, 16), commit: "abc", branch: "main", score: 90);
        return path;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        return conn;
    }

    private static List<(string Name, string Type, bool NotNull, bool Pk)> Columns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        var cols = new List<(string, string, bool, bool)>();
        while (r.Read())
            cols.Add((r.GetString(1), r.GetString(2), r.GetInt64(3) == 1, r.GetInt64(5) > 0));
        return cols;
    }

    [Fact]
    public async Task Snapshots_table_has_the_frozen_columns()
    {
        using var dir = new TempDir();
        using var conn = Open(await CreateDbAsync(dir));

        Assert.Equal(
        [
            ("Id", "INTEGER", true, true),
            ("CreatedAt", "TEXT", true, false),
            ("Commit", "TEXT", false, false),
            ("Branch", "TEXT", false, false),
            ("Score", "INTEGER", false, false),
        ], Columns(conn, "Snapshots"));
    }

    [Fact]
    public async Task Diffs_table_has_the_frozen_columns_and_cascades_from_snapshots()
    {
        using var dir = new TempDir();
        using var conn = Open(await CreateDbAsync(dir));

        Assert.Equal(
        [
            ("Id", "INTEGER", true, true),
            ("SnapshotId", "INTEGER", true, false),
            ("Route", "TEXT", true, false),
            ("DesignLayer", "TEXT", true, false),
            ("Selector", "TEXT", true, false),
            ("Prop", "TEXT", true, false),
            ("Severity", "TEXT", true, false),
            ("Expected", "TEXT", true, false),
            ("Actual", "TEXT", true, false),
        ], Columns(conn, "Diffs"));

        using var fk = conn.CreateCommand();
        fk.CommandText = "PRAGMA foreign_key_list(\"Diffs\")";
        using var r = fk.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("Snapshots", r["table"]);
        Assert.Equal("SnapshotId", r["from"]);
        Assert.Equal("CASCADE", r["on_delete"]);
    }

    [Fact]
    public async Task Severity_is_stored_as_the_pascal_case_enum_name()
    {
        // 與 report.json 的小寫不同——這是既成事實的凍結形狀;改成整數或小寫,
        // 已 commit 進使用者 repo 的 db 在舊版工具裡就讀不懂(major)
        using var dir = new TempDir();
        var path = await CreateDbAsync(dir,
            new DiffRecord("/", "btn", "#b", "color", Severity.Serious, "#000", "#111"),
            new DiffRecord("/", "btn", "#b", "width", Severity.Critical, "10", "90"));
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Severity FROM Diffs ORDER BY Id";
        using var r = cmd.ExecuteReader();

        var stored = new List<string>();
        while (r.Read()) stored.Add(r.GetString(0));
        Assert.Equal(["Serious", "Critical"], stored);
    }

    [Fact]
    public void Migration_history_is_exactly_the_initial_create()
    {
        // 新增 migration 時這條一定紅——那是提醒:BaselineStore 接管舊 db 時「只把第一個
        // migration 標成已套用」的邏輯要一起檢視(標錯會讓新 migration 被跳過)
        var options = new DbContextOptionsBuilder<BaselineDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new BaselineDbContext(options);

        Assert.Equal(["20260723012242_InitialCreate"], db.Database.GetMigrations());
    }

    [Fact]
    public async Task Model_has_no_changes_that_are_missing_a_migration()
    {
        // 改了 entity / 轉換(例如 Severity 改存整數)卻沒產 migration:新 db 照 migration 建、
        // 程式照 model 讀寫,兩邊對不上要到使用者機器上才炸
        using var dir = new TempDir();
        var path = await CreateDbAsync(dir);
        var options = new DbContextOptionsBuilder<BaselineDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using var db = new BaselineDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(["20260723012242_InitialCreate"], await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task History_is_newest_first_and_capped_at_20_by_default()
    {
        // `parity baseline list` 不帶 limit,靠這個預設上限
        using var dir = new TempDir();
        await using var store = new BaselineStore(dir.File("parity.baseline.db"));
        for (var day = 1; day <= 21; day++)
            await store.SaveAsync([], new DateTime(2026, 7, day), score: day);

        var history = await store.HistoryAsync();

        Assert.Equal(20, history.Count);
        Assert.Equal(21, history[0].Score);  // 最新的在最前
        Assert.Equal(2, history[^1].Score);  // 最舊的那筆被截掉
        Assert.Equal(5, (await store.HistoryAsync(limit: 5)).Count);
    }

    [Fact]
    public async Task Latest_baseline_breaks_timestamp_ties_by_the_newer_id()
    {
        using var dir = new TempDir();
        await using var store = new BaselineStore(dir.File("parity.baseline.db"));
        var at = new DateTime(2026, 7, 16);
        await store.SaveAsync([new DiffRecord("/", "first", "s", "color", Severity.Minor, "e", "a")], at);
        await store.SaveAsync([new DiffRecord("/", "second", "s", "color", Severity.Minor, "e", "a")], at);

        var latest = await store.GetLatestAsync();

        Assert.Equal("second", Assert.Single(latest!.Diffs).DesignLayer);
    }
}

/// <summary>baseline save 在快照上標記出處(commit / branch)——讀不到時靜默略過,不能讓存檔失敗。</summary>
public class GitInfoTests
{
    private static void Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed");
    }

    [Fact]
    public void Reads_the_full_commit_and_branch_of_a_repo()
    {
        using var dir = new TempDir();
        Git(dir.Path, "init", "-q");
        Git(dir.Path, "symbolic-ref", "HEAD", "refs/heads/feature/x");
        Git(dir.Path, "-c", "user.name=t", "-c", "user.email=t@t.local", "-c", "commit.gpgsign=false",
            "commit", "--allow-empty", "-q", "-m", "x");

        var (commit, branch) = Parity.Cli.GitInfo.TryRead(dir.Path);

        Assert.Matches("^[0-9a-f]{40}$", commit); // 存完整 40 碼,顯示時才截 7 碼
        Assert.Equal("feature/x", branch);
    }

    [Fact]
    public void Outside_a_repo_both_are_null_instead_of_an_error()
    {
        using var dir = new TempDir();

        Assert.Equal((null, null), Parity.Cli.GitInfo.TryRead(dir.Path));
    }
}
