using System.Diagnostics;

namespace Parity.Tests;

/// <summary>
/// npm 主套件的啟動腳本(npm/cornhsu-parity/bin/parity.js)自己的錯誤路徑。用真的 node 跑,
/// 把 process.platform / process.arch 換掉來模擬別的平台(CI 的三個 OS 與本機都有 node)。
/// 契約:CLI 的 exit code 是 0 通過 / 1 落差超 gate / 2 執行錯誤 / 3 可信度不足——啟動腳本自己的錯
/// 是執行錯誤,必須是 2;回 1 會被 CI 讀成「設計還原度不合格」。訊息與 CLI 本體一致用英文。
/// </summary>
public class NpmLauncherTests
{
    private static (int ExitCode, string Err) RunLauncherAs(string platform, string arch)
    {
        var launcher = Repo.PathOf("npm", "cornhsu-parity", "bin", "parity.js");
        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 暫存目錄當 cwd:確保 require.resolve 找不到任何已安裝的平台包
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(
            $"Object.defineProperty(process, 'platform', {{ value: '{platform}' }});" +
            $"Object.defineProperty(process, 'arch', {{ value: '{arch}' }});" +
            "require(process.argv[1]);");
        psi.ArgumentList.Add(launcher);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("node 起不來(PATH 上要有 node)");
        var stderr = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), "node 沒有在 30 秒內結束");
        return (p.ExitCode, stderr.GetAwaiter().GetResult());
    }

    [Fact]
    public void An_unsupported_platform_is_an_execution_error_in_english()
    {
        var (exit, err) = RunLauncherAs("aix", "ppc64");

        Assert.Equal(2, exit);
        Assert.Contains("no prebuilt build for aix ppc64", err);
        Assert.Contains("dotnet tool install -g Cornhsu.Parity", err);
        Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", err);
    }

    [Fact]
    public void A_missing_platform_package_is_an_execution_error_in_english()
    {
        // linux arm64 在平台表裡,但這台機器沒裝 @cornhsu/parity-linux-arm64(--no-optional、安裝中斷)
        var (exit, err) = RunLauncherAs("linux", "arm64");

        Assert.Equal(2, exit);
        Assert.Contains("@cornhsu/parity-linux-arm64", err);
        Assert.Contains("npm install cornhsu-parity", err);
        Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", err);
    }
}
