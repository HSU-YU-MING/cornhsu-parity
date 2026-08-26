using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// map 檔解析:與 parity.config.json 同一套寬鬆規則。config 允許註解而 map 不允許的
/// 不一致,在 2026-08-26 Codex 補測實踩過(照 config 的習慣寫註解 → JSON 解析炸),
/// 見 docs/野生實查-2026-08-11-路線B-figma方言.md B9。
/// </summary>
public class MapFileTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"parity-map-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Comments_and_trailing_commas_parse_like_the_config_file()
    {
        File.WriteAllText(_path, """
            {
              // 設計端的 Text 圖層 → demo 頁上那顆按鈕
              "Text": "#demo > button:nth-of-type(1)",
            }
            """);
        var map = ScanSession.LoadMapFile(_path);
        Assert.NotNull(map);
        Assert.Equal("#demo > button:nth-of-type(1)", map!["Text"]);
    }

    [Fact]
    public void Missing_file_means_no_manual_anchors_not_an_error()
        => Assert.Null(ScanSession.LoadMapFile(_path));
}
