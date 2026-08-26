using Parity.Cli;
using Parity.Engine.DesignSources.Json;
using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 純字母隨機 id 的根治(F2 最後一塊,ROADMAP「連拍實測」設計):
/// looksRandom 的字元啟發法分不出 rkxvdnnzty 與 navigation,連拍兩次分得出——
/// 只出現在單邊的 id 是每次載入重新生成的。快照存「實測穩定的 id 白名單」,
/// check 擷取用同一份名單,兩邊 selector 生成規則才一致(只在拍照端避開是不夠的)。
/// </summary>
public class StableIdAllowlistTests
{
    // ── ProbeStableIds:連拍比對的純函式 ──────────────────────────────

    [Fact]
    public void Ids_on_both_loads_are_stable_single_side_ids_are_not()
    {
        var first = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered("#app", domId: "app", box: new Box(0, 0, 800, 500)),
            Rendered("#rkxvdnnzty", domId: "rkxvdnnzty", box: new Box(0, 500, 800, 50)),
        ]);
        var second = Rendered("body", box: new Box(0, 0, 800, 600), children:
        [
            Rendered("#app", domId: "app", box: new Box(0, 0, 800, 500)),
            Rendered("#qwortyplex", domId: "qwortyplex", box: new Box(0, 500, 800, 50)),
        ]);

        var (stable, unstableCount) = SnapshotBuilder.ProbeStableIds(first, second);

        Assert.Equal(["app"], stable);
        Assert.Equal(2, unstableCount); // 兩串亂碼各只出現一邊
    }

    [Fact]
    public void All_ids_stable_means_no_allowlist_is_needed()
    {
        var tree = Rendered("body", box: new Box(0, 0, 800, 600), children:
            Rendered("#nav", domId: "nav", box: new Box(0, 0, 800, 60)));

        var (stable, unstableCount) = SnapshotBuilder.ProbeStableIds(tree, tree);

        Assert.Equal(["nav"], stable);
        Assert.Equal(0, unstableCount); // 0 = 此頁沒有隨機 id → 快照不存白名單,行為與 1.0 相同
    }

    // ── 快照信封的存取(選填欄位,舊檔零破壞) ──────────────────────────

    [Fact]
    public void Allowlist_round_trips_through_the_snapshot_envelope()
    {
        var root = SnapshotBuilder.ToFrame(
            Rendered("body", box: new Box(0, 0, 800, 600)), "/");
        var path = WriteTemp(ReportJson.SerializeSnapshotTree(root, ["app", "nav"]));
        try
        {
            Assert.Equal(["app", "nav"], JsonDesignSource.TryReadStableIdAnchors(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Snapshots_without_random_ids_and_old_files_read_back_as_null()
    {
        var root = SnapshotBuilder.ToFrame(
            Rendered("body", box: new Box(0, 0, 800, 600)), "/");

        var noAllowlist = WriteTemp(ReportJson.SerializeSnapshotTree(root)); // 新檔、無名單
        var bareTree = WriteTemp("""{ "id": "/", "name": "/", "type": "Frame" }"""); // 信封前的舊檔
        try
        {
            // 名單不該存在時,連 key 都不出現——沒有隨機 id 的站,快照與 1.0 同形
            Assert.DoesNotContain("stableIdAnchors", File.ReadAllText(noAllowlist));
            Assert.Null(JsonDesignSource.TryReadStableIdAnchors(noAllowlist));
            Assert.Null(JsonDesignSource.TryReadStableIdAnchors(bareTree));
            Assert.Null(JsonDesignSource.TryReadStableIdAnchors(
                Path.Combine(Path.GetTempPath(), "parity-does-not-exist.json")));
        }
        finally { File.Delete(noAllowlist); File.Delete(bareTree); }
    }

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"parity-stableid-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}

/// <summary>
/// 真瀏覽器端:白名單如何改變 selector 錨點。與 RandomIdSelectorTests 成對——
/// 那邊驗「數字混雜的隨機 id 由字元啟發法擋下」,這邊驗「純字母隨機 id 啟發法放行
/// (documented gap),白名單擋下」。
/// </summary>
public class StableIdAllowlistCaptureTests : IDisposable
{
    // id 是純字母亂碼且每次載入都不同——looksRandom(要求數字)看不出來
    private const string Html = """
        <!doctype html>
        <html><head><meta charset="utf-8"><style>body { margin: 0; }</style></head>
        <body>
          <div id="app">
            <p>stable text</p>
            <span>randomly labelled</span>
          </div>
          <script>
            document.querySelector('span').id =
              Array.from({ length: 12 }, () =>
                'abcdefghijklmnopqrstuvwxyz'[Math.floor(Math.random() * 26)]).join('');
          </script>
        </body></html>
        """;

    private readonly string _htmlPath;

    public StableIdAllowlistCaptureTests()
    {
        _htmlPath = Path.Combine(Path.GetTempPath(), $"parity-stableid-{Guid.NewGuid():N}.html");
        File.WriteAllText(_htmlPath, Html);
    }

    public void Dispose()
    {
        if (File.Exists(_htmlPath)) File.Delete(_htmlPath);
    }

    [Fact]
    public async Task 純字母隨機id_啟發法放行_白名單擋下_路徑跨載入穩定()
    {
        await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));
        var implRef = new ImplRef(Url: new Uri(_htmlPath).AbsoluteUri, ViewportWidth: 800, ViewportHeight: 600);

        RenderedNode bare, first, second;
        try
        {
            // 無白名單:純字母亂碼 id 會進 selector(這正是字元啟發法的已知極限)
            bare = await source.CaptureAsync(implRef);

            var withAllowlist = implRef with { AllowedIdAnchors = ["app"] };
            first = await source.CaptureAsync(withAllowlist);
            second = await source.CaptureAsync(withAllowlist); // 重新載入 → id 換一串
        }
        catch (Exception ex) when (BrowserMissing(ex))
        {
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        var bareSpan = bare.DescendantsAndSelf().Single(n => n.Tag == "span");
        Assert.Contains("#" + bareSpan.DomId, bareSpan.Selector); // 啟發法真的放行(前提成立)

        var span1 = first.DescendantsAndSelf().Single(n => n.Tag == "span");
        var span2 = second.DescendantsAndSelf().Single(n => n.Tag == "span");
        Assert.DoesNotContain(span1.DomId!, span1.Selector); // 白名單擋下:亂碼不進 selector
        Assert.Equal(span1.Selector, span2.Selector);        // 結構路徑跨載入穩定 → 配對成立

        var p = first.DescendantsAndSelf().Single(n => n.Tag == "p");
        Assert.StartsWith("#app", p.Selector); // 名單內的穩定 id 照常當錨點
    }

    private static bool BrowserMissing(Exception ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
}
