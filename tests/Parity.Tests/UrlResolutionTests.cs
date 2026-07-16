using Parity.Cli;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>target url 的解析與 scheme 辨識——尤其 cdp:(Electron)不能被當成本機相對路徑。</summary>
public class UrlResolutionTests
{
    [Theory]
    [InlineData("cdp:http://localhost:9222")]
    [InlineData("CDP:http://localhost:9222")] // 不分大小寫
    public void Attach_url_is_recognized(string url)
        => Assert.True(WebImplementationSource.IsAttachUrl(url));

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("https://example.com")]
    [InlineData("file:///C:/x.html")]
    [InlineData("./index.html")]
    public void Non_attach_url_is_not_recognized(string url)
        => Assert.False(WebImplementationSource.IsAttachUrl(url));

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("https://www.gov.uk")]
    [InlineData("file:///C:/x.html")]
    [InlineData("cdp:http://localhost:9222")] // 關鍵:不能被 new Uri(...) 折成 file 路徑
    public void Absolute_and_attach_urls_pass_through_unchanged(string url)
        => Assert.Equal(url, ScanSession.ResolveUrl(url, @"C:\proj"));

    [Fact]
    public void Relative_path_becomes_file_uri()
    {
        var resolved = ScanSession.ResolveUrl("index.html", @"C:\proj");
        Assert.StartsWith("file://", resolved);
        Assert.EndsWith("/index.html", resolved);
    }
}
