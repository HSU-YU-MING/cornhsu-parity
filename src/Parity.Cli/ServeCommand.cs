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
    private static JsonSerializerOptions JsonOptions => ReportJson.Compact;

    public static async Task<int> RunAsync(string[] args, bool mapMode = false)
    {
        var opts = CliOptions.Parse(args, "--config=", "--port=", "--watch", "--open");
        if (opts.ContainsKey("--help")) return Usage.Print(mapMode ? Usage.Map : Usage.Serve);
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

        // 資安:只綁 127.0.0.1 擋不掉 DNS-rebinding / 同瀏覽器惡意分頁。
        //  - Host header 只允許 loopback(擋 rebinding:惡意網域解析到 127.0.0.1)
        //  - 變更型(POST)再驗 Origin(擋其他站的 CSRF)
        // 報告含站點結構與截圖,不能被別的頁面讀走或觸發寫入。
        app.Use(async (ctx, next) =>
        {
            if (!IsLoopbackHost(ctx.Request.Host.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Parity serve 只接受本機請求。");
                return;
            }
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                var origin = ctx.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) &&
                    !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && IsLoopbackHost(o.Host)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsync("跨來源請求被拒。");
                    return;
                }
            }
            await next();
        });

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(wwwroot) });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwroot),
            // 本機報告 UI:每次都跟伺服器核對(no-cache)。工具更新後 app.js/css/index.html
            // 不會卡在瀏覽器舊快取——檔案沒變靠 ETag 回 304(便宜),變了就拿到新版。
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });

        // ---- API ----

        app.MapGet("/api/state", () => Results.Json(new
        {
            configPath = Path.GetFullPath(configPath),
            generatedAt = state.GeneratedAt,
            gateFail = state.GateFail,
            gateReasons = state.GateReasons,
            score = state.Score,
            figmaFileKey = session.Config.FigmaFileKey,
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
        var debounceLock = new object();
        void OnChange(object _, FileSystemEventArgs e)
        {
            var full = Path.GetFullPath(e.FullPath);
            if (!files.Contains(full)) return;
            // map 檔若是「自己剛寫的」(API /api/map 已重掃過)就略過,避免同一次配對掃兩遍
            if (string.Equals(full, Path.GetFullPath(session.MapFilePath), StringComparison.OrdinalIgnoreCase)
                && session.LastMapSaveAt is { } saved
                && DateTimeOffset.UtcNow - saved < TimeSpan.FromSeconds(2))
                return;
            lock (debounceLock) // 多個檔案事件可能同時進來,別在 debounce 欄位上打架
            {
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

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

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
        public List<string> GateReasons { get; private set; } = [];
        public int Score { get; private set; } = 100;
        public DateTimeOffset GeneratedAt { get; private set; }

        public async Task RescanAsync()
        {
            await _gate.WaitAsync();
            try
            {
                Scans = await session.RunAsync();
                GateReasons = session.GateFailReasons(Scans);
                GateFail = GateReasons.Count > 0;
                Score = FidelityScore.Compute(Scans.Select(s => s.Result.Report));
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

/// <summary>
/// 共用的 --flag 解析 + 驗證。spec 寫法:「--flag=」= 吃一個值、「--flag」= 布林。
/// 未知旗標、多餘的位置參數、缺值 → 一律 CliUsageException(外層印錯誤、exit 2)。
/// 為什麼嚴格:靜默忽略會讓「查詢」變事故——`snapshot --help` 曾直接覆寫基準、
/// `--taget`(typo)曾讓「只拍一頁」變成全站重拍(dogfooding 實測回報)。
/// -h / --help 每個子指令都認得,由各指令在解析後最先檢查。
/// </summary>
internal static class CliOptions
{
    public static Dictionary<string, string?> Parse(string[] args, params string[] spec)
    {
        var result = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                result["--help"] = null;
                continue;
            }
            if (!arg.StartsWith("--"))
                throw new CliUsageException($"多餘的參數:「{arg}」(--help 看用法)");
            if (spec.Contains(arg + "="))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                    throw new CliUsageException($"參數 {arg} 需要一個值(--help 看用法)");
                result[arg] = args[++i];
            }
            else if (spec.Contains(arg))
            {
                result[arg] = null;
            }
            else
            {
                throw new CliUsageException($"未知參數:「{arg}」(--help 看用法)");
            }
        }
        return result;
    }
}

/// <summary>使用者打錯指令列參數(非程式錯誤)——訊息給人看,exit 2。</summary>
internal sealed class CliUsageException(string message) : Exception(message);
