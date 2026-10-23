using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// 永遠不會 network idle 的頁面(長輪詢、websocket、分析心跳):等 networkidle 逾時之後,
/// 頁面本身多半早就載好了——不能讓整份報告因此炸掉,要確認 load 完成後照常擷取。
/// 頁面由測試內的 Kestrel(127.0.0.1:0)提供,不連外網;沒裝 Chromium 時印出「略過」。
/// </summary>
public class NeverIdlePageTests
{
    [Fact]
    public async Task Page_with_a_request_that_never_finishes_is_still_captured_after_the_idle_timeout()
    {
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/", () => Results.Content("""
            <!doctype html><html><head><meta charset="utf-8"></head>
            <body style="margin:0"><h1>still captured</h1><script>fetch('/poll');</script></body></html>
            """, "text/html; charset=utf-8"));
        app.MapGet("/poll", async (CancellationToken aborted) =>
        {
            pollStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, aborted); // 長輪詢:伺服器不回,直到瀏覽器關頁斷線
            return Results.Ok();
        });
        await app.StartAsync();

        // 逾時壓到 1.5 秒(預設 30 秒):這條測的是逾時「之後」的路,不是等多久
        await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true, TimeoutMs: 1500));
        RenderedNode root;
        try
        {
            root = await source.CaptureAsync(new ImplRef(app.Urls.First(), 800, 600));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        Assert.True(pollStarted.Task.IsCompleted, "前提不成立:頁面沒有發出那個不會結束的請求");
        Assert.Contains(root.DescendantsAndSelf(), n => n.Text == "still captured");
    }
}
