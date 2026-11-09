using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Parity.Cli;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// 登入態與頁面狀態(2.5.0,競品清單):設定檔的 <c>auth</c> 與 <c>targets[].steps</c> 怎麼驗、怎麼變成擷取參照。不開瀏覽器。
/// </summary>
public class PageSetupConfigTests
{
    private static ParityConfig LoadJson(string json)
    {
        using var dir = new TempDir();
        return ParityConfig.Load(dir.Write("parity.config.json", json));
    }

    private static ParityConfig WithSteps(string steps, string url = "http://localhost/")
        => LoadJson($$"""{ "targets": [{ "route": "/", "url": "{{url}}", "steps": {{steps}} }] }""");

    [Fact]
    public void Steps_parse_into_page_steps()
    {
        var config = WithSteps("""
            [ { "click": "#menu" }, { "hover": ".item" }, { "waitFor": ".panel" }, { "fill": "#q", "value": "shoes" },
              { "press": "Escape" }, { "press": "Enter", "in": "#q" }, { "wait": 250 } ]
            """);

        Assert.Equal(
        [
            new PageStep("click", "#menu"), new PageStep("hover", ".item"), new PageStep("waitFor", ".panel"),
            new PageStep("fill", "#q", "shoes"), new PageStep("press", null, "Escape"), new PageStep("press", "#q", "Enter"),
            new PageStep("wait", Milliseconds: 250),
        ], config.Targets[0].ParsedSteps);
    }

    [Theory]
    [InlineData("""[ "click #menu" ]""", "steps[0] must be an object")]
    [InlineData("""[ {} ]""", "steps[0] needs exactly one action")]
    [InlineData("""[ { "tap": "#a" } ]""", "needs exactly one action (click, hover, waitFor, fill, press, wait); got tap")]
    [InlineData("""[ { "click": "#a", "hover": "#b" } ]""", "needs exactly one action")]
    [InlineData("""[ { "click": "#a", "value": "x" } ]""", "has \"value\", which click does not take.")]
    [InlineData("""[ { "fill": "#q" } ]""", "needs \"value\"")]
    [InlineData("""[ { "fill": "#q", "value": 3 } ]""", "\"value\" must be a non-empty string")]
    [InlineData("""[ { "click": "" } ]""", "\"click\" must be a non-empty string")]
    [InlineData("""[ { "press": "Enter", "on": "#q" } ]""", "has \"on\", which press does not take (it takes: in).")]
    [InlineData("""[ { "wait": 10001 } ]""", "\"wait\" must be a whole number of milliseconds from 0 to 10000")]
    [InlineData("""[ { "wait": 1.5 } ]""", "\"wait\" must be a whole number")]
    [InlineData("""[ { "wait": "500" } ]""", "\"wait\" must be a whole number")]
    public void A_broken_step_is_a_load_error_that_names_it(string steps, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WithSteps(steps));
        Assert.Contains("targets[0].steps", ex.Message);
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void An_empty_fill_value_is_allowed_to_clear_a_field()
        => Assert.Equal("", WithSteps("""[ { "fill": "#q", "value": "" } ]""").Targets[0].ParsedSteps![0].Value);

    [Fact]
    public void Steps_on_a_cdp_target_are_refused()
    {
        // 接現成視窗時跑步驟等於在使用者活著的 app 上亂點
        var ex = Assert.Throws<InvalidOperationException>(() => WithSteps("""[ { "click": "#a" } ]""", "cdp:http://127.0.0.1:9222"));
        Assert.Contains("targets[0] attaches to a running app (cdp:) and also has steps", ex.Message);
    }

    [Theory]
    [InlineData("""{ "auth": null }""", "\"auth\" is null")]
    [InlineData("""{ "auth": { "storageState": " " } }""", "auth.storageState is empty")]
    [InlineData("""{ "auth": { "headers": { "Authorization": null } } }""", "auth.headers needs a header name and a string value")]
    public void A_broken_auth_section_is_a_load_error(string json, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson(json));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Page_setup_resolves_paths_and_env_values_when_it_is_used()
    {
        using var dir = new TempDir();
        var config = ParityConfig.Load(dir.Write("parity.config.json", """
            { "auth": { "storageState": ".parity/auth.json", "headers": { "X-Plain": "a", "Authorization": "env:PARITY_TEST_AUTH" } },
              "targets": [{ "route": "/", "url": "http://localhost/", "steps": [ { "fill": "#pw", "value": "env:PARITY_TEST_PW" }, { "click": "#go" } ] }] }
            """));
        Environment.SetEnvironmentVariable("PARITY_TEST_AUTH", "Bearer t0ken");
        Environment.SetEnvironmentVariable("PARITY_TEST_PW", "hunter2");
        try
        {
            var r = config.WithPageSetup(new ImplRef("http://localhost/"), config.Targets[0]);

            Assert.Equal(Path.Combine(dir.Path, ".parity", "auth.json"), r.StorageStatePath);
            Assert.Equal("Bearer t0ken", r.ExtraHeaders!["Authorization"]);
            Assert.Equal("a", r.ExtraHeaders["X-Plain"]);
            Assert.Equal([new PageStep("fill", "#pw", "hunter2"), new PageStep("click", "#go")], r.Steps);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PARITY_TEST_AUTH", null);
            Environment.SetEnvironmentVariable("PARITY_TEST_PW", null);
        }
    }

    [Fact]
    public void A_missing_environment_variable_names_the_variable_not_a_value()
    {
        var config = LoadJson("""{ "auth": { "headers": { "Authorization": "env:PARITY_TEST_UNSET_VAR" } }, "targets": [{ "url": "http://x/" }] }""");

        var ex = Assert.Throws<InvalidOperationException>(() => config.WithPageSetup(new ImplRef("http://x/"), config.Targets[0]));
        Assert.Equal("auth.headers.Authorization reads the environment variable PARITY_TEST_UNSET_VAR, which is not set.", ex.Message);
    }

    [Fact]
    public void A_cdp_target_gets_no_login_state()
    {
        var config = LoadJson("""{ "auth": { "storageState": "auth.json", "headers": { "A": "b" } }, "targets": [{ "url": "cdp:http://127.0.0.1:9222" }] }""");

        var r = config.WithPageSetup(new ImplRef("cdp:http://127.0.0.1:9222"), config.Targets[0]);

        Assert.Equal((null, null, null), (r.StorageStatePath, r.ExtraHeaders, r.Steps));
    }

    [Fact]
    public void A_fill_step_never_shows_its_value_in_a_description()
        => Assert.Equal("fill #pw", new PageStep("fill", "#pw", "hunter2").Describe());

    [Fact]
    public void The_bug_report_summary_counts_login_settings_without_their_values()
    {
        var config = LoadJson("""
            { "auth": { "storageState": "secret-path.json", "headers": { "Authorization": "Bearer abc" } },
              "targets": [{ "url": "http://x/", "steps": [ { "click": "#private-button" } ] }, { "url": "http://y/" }] }
            """);

        var lines = string.Join("\n", ConfigSummary.From(config).Lines());

        Assert.Contains("auth: storageState set, headers 1; targets with steps: 1", lines);
        Assert.DoesNotContain("secret-path", lines);
        Assert.DoesNotContain("Bearer", lines);
        Assert.DoesNotContain("private-button", lines);
    }
}

/// <summary>
/// 登入態與步驟的真實擷取:頁面由測試內的 Kestrel(127.0.0.1:0)提供,看 cookie 與標頭決定顯示什麼。沒裝 Chromium 時印出「略過」。
/// </summary>
public class PageSetupCaptureTests
{
    private const string Skipped = "略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器";

    private static async Task<WebApplication> StartSiteAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        static IResult Page(string body) => Results.Content(
            $"<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"margin:0\">{body}</body></html>", "text/html; charset=utf-8");
        app.MapGet("/", (HttpRequest r) => Page(r.Cookies["session"] == "abc" ? "<h1>signed in</h1>" : "<h1>signed out</h1>"));
        app.MapGet("/header", (HttpRequest r) => Page(r.Headers["X-Parity-Test"] == "yes" ? "<h1>header ok</h1>" : "<h1>no header</h1>"));
        // 登入頁:一載入就由伺服器種 cookie(模擬登入完成)
        app.MapGet("/login", (HttpResponse res) =>
        {
            res.Cookies.Append("session", "abc", new CookieOptions { Path = "/", HttpOnly = true });
            return Page("<h1>welcome</h1>");
        });
        app.MapGet("/menu", () => Page("""
            <button id="open" onclick="document.getElementById('panel').style.display='block'">Menu</button>
            <div id="panel" style="display:none; width:200px; height:100px">Panel content</div>
            <input id="q">
            """));
        await app.StartAsync();
        return app;
    }

    private static async Task<RenderedNode?> TryCapture(WebImplementationSource source, ImplRef r)
    {
        try { return await source.CaptureAsync(r); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)) { return null; }
    }

    private static bool Shows(RenderedNode root, string text) => root.DescendantsAndSelf().Any(n => n.Text == text);

    [Fact]
    public async Task Steps_put_the_page_in_the_state_to_measure()
    {
        await using var app = await StartSiteAsync();
        await using var source = new WebImplementationSource();
        var url = app.Urls.First() + "/menu";

        var closed = await TryCapture(source, new ImplRef(url, 800, 600));
        if (closed is null) { Console.WriteLine(Skipped); return; }
        var open = await source.CaptureAsync(new ImplRef(url, 800, 600)
        {
            Steps = [new PageStep("click", "#open"), new PageStep("waitFor", "#panel"), new PageStep("fill", "#q", "shoes"), new PageStep("wait", Milliseconds: 50)],
        });

        Assert.False(Shows(closed, "Panel content"));
        Assert.True(Shows(open, "Panel content"));
    }

    [Fact]
    public async Task A_step_that_cannot_run_fails_the_capture_and_names_the_step_but_not_the_value()
    {
        await using var app = await StartSiteAsync();
        await using var source = new WebImplementationSource();

        var ex = await Record.ExceptionAsync(() => source.CaptureAsync(new ImplRef(app.Urls.First() + "/menu", 800, 600)
        {
            Steps = [new PageStep("click", "#open"), new PageStep("fill", "#missing", "hunter2")],
        }));

        if (ex?.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase) == true) { Console.WriteLine(Skipped); return; }
        var failure = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("step 2 (fill #missing) failed", failure.Message);
        Assert.DoesNotContain("hunter2", failure.Message);
    }

    [Fact]
    public async Task Extra_headers_go_with_every_request()
    {
        await using var app = await StartSiteAsync();
        await using var source = new WebImplementationSource();
        var url = app.Urls.First() + "/header";

        var plain = await TryCapture(source, new ImplRef(url, 800, 600));
        if (plain is null) { Console.WriteLine(Skipped); return; }
        var withHeader = await source.CaptureAsync(new ImplRef(url, 800, 600) { ExtraHeaders = new Dictionary<string, string> { ["X-Parity-Test"] = "yes" } });

        Assert.True(Shows(plain, "no header"));
        Assert.True(Shows(withHeader, "header ok"));
    }

    [Fact]
    public async Task A_recorded_login_is_loaded_before_the_page()
    {
        // parity login 的流程,只是瀏覽器不顯示、使用者「立刻按下 Enter」:登入頁種下 cookie → 存成 storage state → 下次擷取帶著它
        await using var app = await StartSiteAsync();
        using var dir = new TempDir();
        var state = dir.File(".parity", "auth.json");
        await using var source = new WebImplementationSource();

        var before = await TryCapture(source, new ImplRef(app.Urls.First(), 800, 600));
        if (before is null) { Console.WriteLine(Skipped); return; }
        await source.RecordLoginAsync(app.Urls.First() + "/login", state, () => Task.CompletedTask);
        var after = await source.CaptureAsync(new ImplRef(app.Urls.First(), 800, 600) { StorageStatePath = state });

        Assert.True(Shows(before, "signed out"));
        Assert.True(Shows(after, "signed in"));
        using var saved = JsonDocument.Parse(File.ReadAllText(state));
        Assert.Contains(saved.RootElement.GetProperty("cookies").EnumerateArray(), c => c.GetProperty("name").GetString() == "session");
    }

    [Fact]
    public async Task A_missing_login_state_file_says_to_run_parity_login()
    {
        await using var source = new WebImplementationSource();
        var ex = await Record.ExceptionAsync(() => source.CaptureAsync(new ImplRef("http://127.0.0.1:1/", 800, 600) { StorageStatePath = "nope/auth.json" }));

        if (ex?.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase) == true) { Console.WriteLine(Skipped); return; }
        Assert.Contains("run `parity login`", Assert.IsType<InvalidOperationException>(ex).Message);
    }
}
