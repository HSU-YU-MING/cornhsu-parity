using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>
/// parity serve(M3):本機報告 UI。
/// Kestrel 只綁 127.0.0.1(規畫書資安要求:報告含站點結構,不能被區網掃到);
/// --watch 盯本機檔案改動自動重跑;SSE 推播讓 UI 即時更新;
/// parity map 走同一個 UI(#map),點選配對寫回 parity.map.json 後自動重跑。
/// </summary>
internal static class ServeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args, bool mapMode = false)
    {
        var opts = CliOptions.Parse(args);
        var configPath = opts.GetValueOrDefault("--config")
            ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("找不到 parity.config.json(可用 `parity init` 產生範本)。");
        var port = int.TryParse(opts.GetValueOrDefault("--port"), out var p) ? p : 4321;
        var watch = opts.ContainsKey("--watch");

        await using var session = new ScanSession(configPath, captureScreenshots: true);
        var state = new ServeState(session);

        Console.WriteLine($"\x1b[1mParity serve\x1b[0m — 設定:{configPath}");
        Console.WriteLine("首次掃描中…");
        await state.RescanAsync();
        Console.WriteLine($"完成:{state.StatusLine()}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(System.Net.IPAddress.Loopback, port)); // 只綁 loopback,絕不 0.0.0.0

        var app = builder.Build();

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(wwwroot) });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(wwwroot) });

        // ---- API ----

        app.MapGet("/api/state", () => Results.Json(new
        {
            configPath = Path.GetFullPath(configPath),
            generatedAt = state.GeneratedAt,
            gateFail = state.GateFail,
            failOn = session.Config.Gate.FailOn,
            watch,
            targets = state.Scans.Select((s, i) => new
            {
                route = s.Target.Route,
                url = s.Result.Report.Url,
                summary = s.Result.Report.Summary,
                nodes = s.Result.Report.Nodes,
                unmatched = s.Result.Report.Unmatched,
                origin = new { x = s.Result.RenderedOrigin.X, y = s.Result.RenderedOrigin.Y },
                screenshot = s.Screenshot is null ? null : $"/api/screenshot/{i}",
            }),
        }, JsonOptions));

        app.MapGet("/api/screenshot/{index:int}", (int index) =>
            index >= 0 && index < state.Scans.Count && state.Scans[index].Screenshot is { } png
                ? Results.Bytes(png, "image/png")
                : Results.NotFound());

        // 配對 hit-test 用:實作端整棵樹(含 selector 與座標框)
        app.MapGet("/api/detail/{index:int}", (int index) =>
            index >= 0 && index < state.Scans.Count
                ? Results.Json(new
                {
                    renderedTree = state.Scans[index].Result.RenderedTree,
                    designTree = state.Scans[index].Result.DesignTree,
                }, JsonOptions)
                : Results.NotFound());

        app.MapPost("/api/rerun", async () =>
        {
            await state.RescanAsync();
            Console.WriteLine($"重新掃描:{state.StatusLine()}");
            return Results.Json(new { ok = true }, JsonOptions);
        });

        // parity map 的儲存動作:寫 map 檔 → 重跑 → SSE 通知
        app.MapPost("/api/map", async (MapRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Layer) || string.IsNullOrWhiteSpace(req.Selector))
                return Results.BadRequest(new { error = "layer 與 selector 都必填" });
            session.SaveMapping(req.Layer, req.Selector);
            await state.RescanAsync();
            Console.WriteLine($"已配對 \x1b[1m{req.Layer}\x1b[0m → {req.Selector},{state.StatusLine()}");
            return Results.Json(new { ok = true }, JsonOptions);
        });

        // SSE:掃描完成推 "reload",UI 重抓 /api/state
        app.MapGet("/api/events", async (HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var (id, reader) = state.Subscribe();
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                await foreach (var msg in reader.ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync($"data: {msg}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* 客戶端斷線 */ }
            finally { state.Unsubscribe(id); }
        });

        // ---- watch:本機檔案改動 → debounce 重跑 ----
        var watchers = new List<FileSystemWatcher>();
        if (watch)
            watchers = StartWatchers(session, state);

        var url = $"http://127.0.0.1:{port}/{(mapMode ? "#map" : "")}";
        Console.WriteLine($"\n報告 UI:\x1b[1m{url}\x1b[0m{(watch ? "(watch 模式)" : "")}");
        Console.WriteLine("Ctrl+C 結束。");

        if (mapMode || opts.ContainsKey("--open"))
            TryOpenBrowser(url);

        try
        {
            await app.RunAsync();
        }
        finally
        {
            foreach (var w in watchers) w.Dispose();
        }
        return 0;
    }

    private static List<FileSystemWatcher> StartWatchers(ScanSession session, ServeState state)
    {
        var files = session.WatchableFiles()
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Timer? debounce = null;
        void OnChange(object _, FileSystemEventArgs e)
        {
            if (!files.Contains(Path.GetFullPath(e.FullPath))) return;
            debounce?.Dispose();
            debounce = new Timer(async _ =>
            {
                try
                {
                    await state.RescanAsync();
                    Console.WriteLine($"偵測到 {Path.GetFileName(e.FullPath)} 變更,已重掃:{state.StatusLine()}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"\x1b[31m重掃失敗:{ex.Message}\x1b[0m");
                }
            }, null, 400, Timeout.Infinite);
        }

        var watchers = new List<FileSystemWatcher>();
        foreach (var dir in files.Select(Path.GetDirectoryName).Where(d => d is not null).Distinct()!)
        {
            var w = new FileSystemWatcher(dir!)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            w.Changed += OnChange;
            w.Created += OnChange;
            w.Renamed += (s, e) => OnChange(s, e);
            watchers.Add(w);
        }
        return watchers;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 開不了瀏覽器不致命,URL 已印在終端機
        }
    }

    private sealed record MapRequest(string Layer, string Selector);

    /// <summary>serve 的可變狀態:最新掃描結果 + SSE 訂閱者。</summary>
    private sealed class ServeState(ScanSession session)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<Guid, Channel<string>> _subscribers = [];

        public List<TargetScan> Scans { get; private set; } = [];
        public bool GateFail { get; private set; }
        public DateTimeOffset GeneratedAt { get; private set; }

        public async Task RescanAsync()
        {
            await _gate.WaitAsync();
            try
            {
                Scans = await session.RunAsync();
                GateFail = session.ShouldFail(Scans);
                GeneratedAt = DateTimeOffset.Now;
            }
            finally { _gate.Release(); }
            Broadcast("reload");
        }

        public string StatusLine()
        {
            var diffs = Scans.Sum(s => s.Result.Report.Summary.NodesWithDiffs);
            var unmatched = Scans.Sum(s => s.Result.Report.Summary.Unmatched);
            return $"{Scans.Count} 個 target、{diffs} 個節點有落差、{unmatched} 個未配對 → {(GateFail ? "GATE FAIL" : "PASS")}";
        }

        public (Guid, ChannelReader<string>) Subscribe()
        {
            var ch = Channel.CreateUnbounded<string>();
            var id = Guid.NewGuid();
            lock (_subscribers) _subscribers[id] = ch;
            return (id, ch.Reader);
        }

        public void Unsubscribe(Guid id)
        {
            lock (_subscribers) _subscribers.Remove(id);
        }

        private void Broadcast(string msg)
        {
            lock (_subscribers)
                foreach (var ch in _subscribers.Values)
                    ch.Writer.TryWrite(msg);
        }
    }
}

/// <summary>共用的 --flag 解析(check / serve / map)。</summary>
internal static class CliOptions
{
    public static Dictionary<string, string?> Parse(string[] args)
    {
        var result = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            result[args[i]] = hasValue ? args[++i] : null;
        }
        return result;
    }
}
