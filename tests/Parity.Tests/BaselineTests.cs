using Parity.Engine;
using Parity.Storage;

namespace Parity.Tests;

public class BaselineComparerTests
{
    private static DiffRecord D(string layer, string prop, Severity sev, string route = "/")
        => new(route, layer, "sel", prop, sev, "expected", "actual");

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
                    new DateTime(2026, 7, 16), commit: "abc1234", branch: "main");

            await using (var store = new BaselineStore(path))
            {
                var latest = await store.GetLatestAsync();
                Assert.NotNull(latest);
                var d = Assert.Single(latest);
                Assert.Equal("btn", d.DesignLayer);
                Assert.Equal(Severity.Serious, d.Severity);
                Assert.Single(await store.HistoryAsync());
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
            Assert.Equal("new", Assert.Single(latest!).DesignLayer);
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
}
