using System.Net;
using System.Text;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;

namespace Parity.Tests;

/// <summary>
/// 匯出 frame 當圖片比對的參考圖(compare.images 的 Figma 來源)。不打真的 Figma:HttpClient 接假 handler。
/// 守的是:token 只送給 Figma、不送給圖片網址;匯出會用掉額度,所以抓過的不再打;失敗時講得清楚。
/// **沒有拿真實的 Figma 回應驗過**,回應格式是照官方文件寫的。
/// </summary>
public class FigmaImageExportTests
{
    private const string Token = "figd_secret-token-value";
    private const string ImageUrl = "https://figma-alpha-api.s3.us-west-2.amazonaws.com/images/abc";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static readonly DesignRef Frame = new("AbCdEf123", "10:2");

    private sealed class FakeFigma(string imagesBody, HttpStatusCode imagesStatus = HttpStatusCode.OK, HttpStatusCode downloadStatus = HttpStatusCode.OK, long? declaredLength = null) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Token)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("X-Figma-Token", out var v) ? v.Single() : null));
            if (request.RequestUri!.Host == "api.figma.com")
                return Task.FromResult(new HttpResponseMessage(imagesStatus) { Content = new StringContent(imagesBody, Encoding.UTF8, "application/json") });
            var content = new ByteArrayContent(Png);
            if (declaredLength is { } length) content.Headers.ContentLength = length;
            return Task.FromResult(new HttpResponseMessage(downloadStatus) { Content = content });
        }
    }

    private static string Images(string? url = ImageUrl)
        => url is null ? """{ "err": null, "images": { "10:2": null } }""" : $$"""{ "err": null, "images": { "10:2": "{{url}}" } }""";

    private static FigmaDesignSource Source(FakeFigma fake, string? cacheDir, bool refresh = false)
        => new(new FigmaOptions(Token, cacheDir, refresh), new HttpClient(fake));

    [Fact]
    public async Task The_frame_is_exported_at_1x_with_its_exact_bounds_and_saved_to_the_cache()
    {
        using var dir = new TempDir();
        var fake = new FakeFigma(Images());
        using var source = Source(fake, dir.Path);

        var path = await source.GetFrameImagePathAsync(Frame);

        Assert.Equal(Png, File.ReadAllBytes(path));
        Assert.StartsWith(dir.Path, path);
        var query = fake.Requests[0].Uri.Query;
        Assert.Equal("/v1/images/AbCdEf123", fake.Requests[0].Uri.AbsolutePath);
        Assert.Contains("ids=10%3A2", query);
        Assert.Contains("format=png", query);
        Assert.Contains("scale=1", query);
        Assert.Contains("use_absolute_bounds=true", query); // 圖片要剛好是 frame 的框,座標才對得上
    }

    [Fact]
    public async Task The_token_goes_to_figma_and_never_to_the_image_address()
    {
        // 圖片網址是別的網域的簽章連結:token 送過去就外流了
        using var dir = new TempDir();
        var fake = new FakeFigma(Images());
        using var source = Source(fake, dir.Path);

        await source.GetFrameImagePathAsync(Frame);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(Token, fake.Requests[0].Token);
        Assert.Equal(new Uri(ImageUrl), fake.Requests[1].Uri);
        Assert.Null(fake.Requests[1].Token);
        Assert.DoesNotContain(Token, fake.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task An_exported_frame_is_not_fetched_again_because_it_costs_quota()
    {
        using var dir = new TempDir();
        var fake = new FakeFigma(Images());
        using var source = Source(fake, dir.Path);

        var first = await source.GetFrameImagePathAsync(Frame);
        var second = await source.GetFrameImagePathAsync(Frame);

        Assert.Equal(first, second);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Refresh_fetches_it_again()
    {
        using var dir = new TempDir();
        using var cached = Source(new FakeFigma(Images()), dir.Path);
        await cached.GetFrameImagePathAsync(Frame);

        var fake = new FakeFigma(Images());
        using var source = Source(fake, dir.Path, refresh: true);
        await source.GetFrameImagePathAsync(Frame);

        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task The_image_and_the_node_json_have_separate_cache_files()
    {
        using var dir = new TempDir();
        using var source = Source(new FakeFigma(Images()), dir.Path);

        var path = await source.GetFrameImagePathAsync(Frame);

        Assert.EndsWith(".png", path);
        Assert.Contains("10-2", Path.GetFileName(path)); // ":" 在 Windows 不能當檔名
    }

    [Fact]
    public async Task A_rate_limit_explains_itself_and_does_not_leak_the_token()
    {
        using var dir = new TempDir();
        using var source = Source(new FakeFigma(Images(), HttpStatusCode.TooManyRequests), dir.Path);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetFrameImagePathAsync(Frame));

        Assert.Contains("rate-limiting", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task A_frame_figma_could_not_render_is_reported()
    {
        using var dir = new TempDir();
        using var source = Source(new FakeFigma(Images(url: null)), dir.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetFrameImagePathAsync(Frame));

        Assert.Contains("could not render", ex.Message);
    }

    [Theory]
    [InlineData("http://example.com/a.png")] // 不是 https
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task Only_https_addresses_are_downloaded(string url)
    {
        using var dir = new TempDir();
        var fake = new FakeFigma(Images(url));
        using var source = Source(fake, dir.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetFrameImagePathAsync(Frame));

        Assert.Single(fake.Requests); // 沒有去下載
    }

    [Fact]
    public async Task A_failed_download_is_not_cached()
    {
        using var dir = new TempDir();
        using var source = Source(new FakeFigma(Images(), downloadStatus: HttpStatusCode.Forbidden), dir.Path);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetFrameImagePathAsync(Frame));

        Assert.Empty(Directory.GetFiles(dir.Path, "*.png"));
    }

    [Fact]
    public async Task An_oversized_image_is_refused()
    {
        using var dir = new TempDir();
        using var source = Source(new FakeFigma(Images(), declaredLength: 65L * 1024 * 1024), dir.Path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetFrameImagePathAsync(Frame));

        Assert.Contains("larger than", ex.Message);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.png"));
    }

    [Fact]
    public async Task Exporting_needs_a_cache_directory()
    {
        using var source = Source(new FakeFigma(Images()), cacheDir: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetFrameImagePathAsync(Frame));
    }
}
