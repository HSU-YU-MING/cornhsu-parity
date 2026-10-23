using System.Net;
using System.Text;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;

namespace Parity.Tests;

/// <summary>
/// Figma REST 來源的 HTTP 那一半(節點解析見 FigmaParserTests)。不打真的 Figma:HttpClient 接假 handler。
/// 守的是使用者看得到的三件事——token 只走標頭不進 URL、抓過的 frame 落本機快取(之後離線也能比、
/// 被限流時已抓過的照常可用)、失敗時的提示把人帶往正確方向。
/// </summary>
public class FigmaDesignSourceTests
{
    private const string Token = "figd_secret-token-value";

    private const string NodesResponse = """
        { "nodes": { "10:2": { "document": {
            "id": "10:2", "name": "Home", "type": "FRAME",
            "absoluteBoundingBox": { "x": 0, "y": 0, "width": 800, "height": 600 } } } } }
        """;

    private sealed class FakeFigma(HttpStatusCode status, string body = NodesResponse, TimeSpan? retryAfter = null) : HttpMessageHandler
    {
        public List<(Uri Uri, string? TokenHeader)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("X-Figma-Token", out var v) ? v.Single() : null));
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is { } delta) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
            return Task.FromResult(response);
        }
    }

    private static FigmaDesignSource Source(FakeFigma fake, string? cacheDir = null, bool refresh = false)
        => new(new FigmaOptions(Token, cacheDir, refresh), new HttpClient(fake));

    [Fact]
    public async Task Token_goes_in_the_header_never_in_the_url()
    {
        var fake = new FakeFigma(HttpStatusCode.OK);
        using var source = Source(fake);

        var frame = await source.GetFrameAsync(new DesignRef("AbCdEf123", "10:2"));

        Assert.Equal("Home", frame.Name);
        var (uri, header) = Assert.Single(fake.Requests);
        Assert.Equal("https://api.figma.com/v1/files/AbCdEf123/nodes?ids=10%3A2", uri.AbsoluteUri);
        Assert.Equal(Token, header);
        Assert.DoesNotContain(Token, uri.AbsoluteUri);
    }

    [Fact]
    public async Task Fetched_frames_are_cached_and_reused_offline()
    {
        using var dir = new TempDir();
        var online = new FakeFigma(HttpStatusCode.OK);
        using (var first = Source(online, dir.Path))
            await first.GetFrameAsync(new DesignRef("AbCdEf123", "10:2"));

        Assert.True(File.Exists(dir.File("figma_AbCdEf123_10-2.json"))); // ':' 換成 '-',Windows 檔名合法

        // 第二次:Figma 掛了(或被限流)也照樣拿得到——完全沒發請求
        var down = new FakeFigma(HttpStatusCode.TooManyRequests);
        using var second = Source(down, dir.Path);
        var frame = await second.GetFrameAsync(new DesignRef("AbCdEf123", "10:2"));

        Assert.Equal("Home", frame.Name);
        Assert.Empty(down.Requests);
    }

    [Fact]
    public async Task Refresh_ignores_the_cache_and_overwrites_it()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("figma_AbCdEf123_10-2.json"), NodesResponse.Replace("\"Home\"", "\"Stale\""));
        var fake = new FakeFigma(HttpStatusCode.OK);
        using var source = Source(fake, dir.Path, refresh: true);

        var frame = await source.GetFrameAsync(new DesignRef("AbCdEf123", "10:2"));

        Assert.Equal("Home", frame.Name);
        Assert.Single(fake.Requests);
        Assert.Contains("\"Home\"", File.ReadAllText(dir.File("figma_AbCdEf123_10-2.json")));
    }

    [Fact]
    public async Task Rate_limit_error_speaks_the_retry_window_and_points_at_the_cache()
    {
        using var source = Source(new FakeFigma(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromHours(48)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => source.GetFrameAsync(new DesignRef("AbCdEf123", "10:2")));

        Assert.Contains("Figma API returned 429 TooManyRequests (file AbCdEf123, node 10:2)", ex.Message);
        Assert.Contains("retry in about 2 day(s)", ex.Message);
        Assert.Contains(".parity/cache", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task Failed_requests_are_not_cached()
    {
        using var dir = new TempDir();
        using var source = Source(new FakeFigma(HttpStatusCode.Forbidden, "{}"), dir.Path);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => source.GetFrameAsync(new DesignRef("AbCdEf123", "10:2")));

        Assert.Contains("has not expired", ex.Message); // 403 的候選原因要包含 token 過期
        Assert.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Fact]
    public async Task Missing_node_in_the_response_names_the_node_and_file()
    {
        using var source = Source(new FakeFigma(HttpStatusCode.OK, """{ "nodes": { "10:2": null } }"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetFrameAsync(new DesignRef("AbCdEf123", "10:2")));

        Assert.Contains("node 10:2 is not present in the Figma response (file AbCdEf123)", ex.Message);
    }
}
