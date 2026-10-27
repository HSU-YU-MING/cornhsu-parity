using System.Runtime.CompilerServices;
using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// 整個測試行程的匿名統計預設(2.1.0):**研究資料不能混進維護者自己的測試**。
/// 開發版(0.0.0-dev)本來就不送,但 release.yml 用真版號建置後照樣跑這些測試,CliBrowserTests 又跑真的 check——
/// 所以三道一起上,不靠任何一條測試記得:
///   1. DO_NOT_TRACK=1:預設什麼都不送。要測送出的測試自己用 EnvScope 清掉(EnvScope.StatsVariables),
///      並用 CORNHSU_STATS_URL 指到本機的假收集站;EnvScope 結束時還原成 1。
///   2. 只准送往 loopback:哪條測試忘了設 CORNHSU_STATS_URL,也送不到 stats.cornhsu.com。
///   3. 「本機第一次」的標記檔改放暫存目錄:行程內跑 CLI 的測試不能寫進使用者真正的設定目錄。
/// </summary>
internal static class TestProcessDefaults
{
#pragma warning disable CA2255 // 測試組件不是函式庫:這就是要在任何測試跑之前、對整個行程生效
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("DO_NOT_TRACK", "1");
        UsageStats.EndpointFilterForTests = uri => uri.IsLoopback;
        StatsEnvironment.NoticeDirectoryForTests =
            Path.Combine(Path.GetTempPath(), $"parity-test-appdata-{Environment.ProcessId}");
    }
}
