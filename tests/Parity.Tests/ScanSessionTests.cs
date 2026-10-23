using Parity.Cli;
using Parity.Engine.DesignSources.Figma;
using Parity.Engine.DesignSources.Image;
using Parity.Engine.DesignSources.Json;

namespace Parity.Tests;

/// <summary>
/// check / serve / map 共用的掃描會話裡,不需要瀏覽器的那幾件事:parity map 怎麼寫 map 檔
/// (使用者會 commit 它,盤點建議歸入面 1)、設計來源的優先序、--watch 盯哪些檔。
/// 建 ScanSession 不會開瀏覽器——瀏覽器是第一次擷取時才起。
/// </summary>
public class ScanSessionTests
{
    private const string Design = """{ "id": "/", "name": "/", "type": "frame", "box": { "x": 0, "y": 0, "width": 10, "height": 10 } }""";

    private static string Config(TempDir dir, string extra = "")
    {
        dir.Write("design.json", Design);
        return dir.Write("parity.config.json",
            $$"""{ "designFile": "design.json", {{extra}} "targets": [{ "route": "/", "url": "./page.html" }, { "route": "/remote", "url": "https://example.com/" }] }""");
    }

    [Fact]
    public async Task Map_file_defaults_next_to_the_config_and_honours_mapFile()
    {
        using var dir = new TempDir();
        await using (var plain = new ScanSession(Config(dir)))
            Assert.Equal(dir.File("parity.map.json"), plain.MapFilePath);

        using var dir2 = new TempDir();
        await using var custom = new ScanSession(Config(dir2, "\"mapFile\": \"maps/manual.json\","));
        Assert.Equal(Path.Combine(dir2.Path, "maps/manual.json"), custom.MapFilePath);
    }

    [Fact]
    public async Task Saving_a_mapping_keeps_other_entries_and_overwrites_the_same_layer()
    {
        using var dir = new TempDir();
        await using var session = new ScanSession(Config(dir));
        File.WriteAllText(session.MapFilePath, """
            {
              // 手寫的對應
              "Hero": "#hero",
              "CTA": "button.old",
            }
            """);

        session.SaveMapping("CTA", "main > button:nth-of-type(1)");
        session.SaveMapping("Badge", "#badge");

        Assert.Equal(new Dictionary<string, string>
        {
            ["Hero"] = "#hero",
            ["CTA"] = "main > button:nth-of-type(1)",
            ["Badge"] = "#badge",
        }, ScanSession.LoadMapFile(session.MapFilePath));
        Assert.NotNull(session.LastMapSaveAt); // watch 模式靠它略過「自己剛寫的檔」
    }

    [Fact]
    public async Task Saved_map_file_stays_human_readable()
    {
        // 使用者會 commit、會手改的檔:selector 裡的 > 與非 ASCII 圖層名不能被寫成 > / \uXXXX
        using var dir = new TempDir();
        await using var session = new ScanSession(Config(dir));

        session.SaveMapping("主視覺", "main > section:nth-of-type(1)");

        var text = File.ReadAllText(session.MapFilePath);
        Assert.Contains("\"主視覺\": \"main > section:nth-of-type(1)\"", text);
        Assert.Contains("\n", text); // 縮排,一行一筆,diff 好讀
    }

    [Fact]
    public async Task Watch_mode_watches_the_config_map_design_and_local_pages_only()
    {
        using var dir = new TempDir();
        await using var session = new ScanSession(Config(dir));

        Assert.Equal(
            [dir.File("parity.config.json"), dir.File("parity.map.json"), dir.File("design.json"), dir.File("page.html")],
            session.WatchableFiles()); // https:// 的 target 不是本機檔,不盯
    }

    [Fact]
    public void Design_source_follows_image_then_file_then_figma()
    {
        // designFile 與 figmaFileKey 並存時 designFile 優先(盤點 2.1);designImage 必搭 designFile 且再優先
        var image = new ParityConfig { DesignImage = "shot.png", DesignFile = "a.json", FigmaFileKey = "k", BaseDirectory = "." };
        var file = new ParityConfig { DesignFile = "a.json", FigmaFileKey = "k", BaseDirectory = "." };
        var figma = new ParityConfig { FigmaFileKey = "k", DesignToken = "figd_literal", BaseDirectory = "." };

        Assert.IsType<ImageDesignSource>(ScanSession.CreateDesignSource(image, refresh: false));
        Assert.IsType<JsonDesignSource>(ScanSession.CreateDesignSource(file, refresh: false));
        using var source = Assert.IsType<FigmaDesignSource>(ScanSession.CreateDesignSource(figma, refresh: false));
    }

    [Fact]
    public async Task Relative_target_urls_resolve_against_the_config_directory()
    {
        // 相對 url 轉 file:// 的基準是設定檔目錄,不是 cwd(CI 從 repo 根跑、設定檔在子目錄時才不會指歪)
        using var dir = new TempDir();
        await using var session = new ScanSession(Config(dir));

        Assert.Equal(new Uri(dir.File("page.html")).AbsoluteUri,
            ScanSession.ResolveUrl(session.Config.Targets[0].Url, session.Config.BaseDirectory));
    }
}
