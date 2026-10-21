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
        Assert.Contains("unknown argument", ex.Message);
        Assert.Contains("--taget", ex.Message);
    }

    [Fact]
    public void Stray_positional_throws()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["foo.json"], Spec));
        Assert.Contains("unexpected argument", ex.Message);
    }

    [Fact]
    public void Value_flag_without_value_throws()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--config"], Spec));
        Assert.Contains("requires a value", ex.Message);
    }

    [Theory]
    [InlineData("--refresh")]
    [InlineData("--target")]
    [InlineData("--help")]
    public void Value_flag_followed_by_another_flag_is_missing_its_value(string next)
    {
        // `--config --refresh` 不能把 "--refresh" 吞成檔名(也不能讓 --refresh 默默失效)
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--config", next], Spec));
        Assert.Contains("--config requires a value", ex.Message);
    }

    [Theory]
    [InlineData("--config=a.json")]
    [InlineData("--refresh=true")]
    public void Inline_equals_syntax_is_not_accepted(string arg)
    {
        // 1.0 凍結審查定案:只接受「--flag value」的空格式;接受 = 寫法是日後的 minor,不是現在
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse([arg], Spec));
        Assert.Contains($"unknown argument: \"{arg}\"", ex.Message);
    }

    [Fact]
    public void Value_may_itself_start_with_a_single_dash()
    {
        // 只有「--」開頭才算下一個旗標;"-1" 這種值(例如 --gate-exit -1)要照收
        var opts = CliOptions.Parse(["--target", "-1"], Spec);
        Assert.Equal("-1", opts["--target"]);
    }

    [Fact]
    public void Bool_flag_does_not_swallow_next_token()
    {
        // --refresh 是布林:後面跟著的東西不是它的值,是多餘參數 → 報錯
        var ex = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--refresh", "junk"], Spec));
        Assert.Contains("unexpected argument", ex.Message);
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
