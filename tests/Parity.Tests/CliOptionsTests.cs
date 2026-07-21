using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// CLI 參數驗證:靜默忽略會讓「查詢」變事故(snapshot --help 曾直接覆寫基準、
/// --taget typo 曾讓單頁重拍變全站重拍)。未知/多餘/缺值一律報錯。
/// </summary>
public class CliOptionsTests
{
    private static readonly string[] Spec = ["--config=", "--target=", "--refresh"];

    [Fact]
    public void Parses_value_and_bool_flags()
    {
        var opts = CliOptions.Parse(["--config", "a.json", "--refresh", "--target", "/x"], Spec);
        Assert.Equal("a.json", opts["--config"]);
        Assert.Equal("/x", opts["--target"]);
        Assert.True(opts.ContainsKey("--refresh"));
        Assert.Null(opts["--refresh"]);
    }

    [Fact]
    public void Unknown_flag_throws()
    {
        // typo:--taget 少個 r——不能靜默忽略讓「只拍一頁」變成全站重拍
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--taget", "/x"], Spec));
        Assert.Contains("未知參數", ex.Message);
        Assert.Contains("--taget", ex.Message);
    }

    [Fact]
    public void Stray_positional_throws()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["foo.json"], Spec));
        Assert.Contains("多餘的參數", ex.Message);
    }

    [Fact]
    public void Value_flag_without_value_throws()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--config"], Spec));
        Assert.Contains("需要一個值", ex.Message);
    }

    [Fact]
    public void Bool_flag_does_not_swallow_next_token()
    {
        // --refresh 是布林:後面跟著的東西不是它的值,是多餘參數 → 報錯
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--refresh", "junk"], Spec));
        Assert.Contains("多餘的參數", ex.Message);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_is_always_recognized(string flag)
    {
        // --help 不在 spec 也要認得——查詢用法絕不能觸發執行(更不能觸發破壞性寫入)
        var opts = CliOptions.Parse([flag], Spec);
        Assert.True(opts.ContainsKey("--help"));
    }
}
