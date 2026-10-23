using Parity.Cli;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Json;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// 凍結面 6(parity.snapshot.json)的 wire 形狀。既有測試只釘了信封的 schemaVersion 與
/// stableIdAnchors;節點欄位(type 小寫、fill 的五個欄位、padding 四邊、顯式 null)是
/// 使用者 commit 進 repo 的基準檔——「1.x 內新版永遠讀得動舊檔」的承諾就押在這個形狀上。
/// </summary>
public class SnapshotWireTests
{
    internal static DesignNode SampleTree()
    {
        var title = new DesignNode("#title", "title", DesignNodeType.Text, new Box(0, 0, 200, 32),
            new Rgba(17, 24, 39), new Typography("Inter", 24, 700, 32, 0), null, null, null, [])
        {
            Characters = "Hello",
        };
        return new DesignNode("/", "/", DesignNodeType.Frame, new Box(0, 0, 1280, 800),
            new Rgba(255, 255, 255, 0.5), null, new Insets(8, 16, 8, 16), 12, 4, [title])
        {
            LayoutMode = "VERTICAL",
            LayoutSizingHorizontal = "FIXED",
            LayoutSizingVertical = "HUG",
        };
    }

    private static string GoldenPath => Repo.PathOf("tests", "Parity.Tests", "Golden", "snapshot.v1.json");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    [Fact]
    public void Snapshot_is_byte_for_byte_the_golden_file()
    {
        var json = ReportJson.SerializeSnapshotTree(SampleTree(), ["app", "nav"]);

        Assert.Equal(Normalize(File.ReadAllText(GoldenPath)), Normalize(json));
    }

    [Fact]
    public void Node_types_are_lowercase_and_nulls_are_explicit()
    {
        var json = ReportJson.SerializeSnapshotTree(SampleTree());

        Assert.Contains("\"type\": \"frame\"", json);
        Assert.Contains("\"type\": \"text\"", json);
        Assert.Contains("\"text\": null", json);       // frame 沒有字體
        Assert.Contains("\"padding\": null", json);    // text 沒有 padding
        Assert.Contains("\"isTransparent\": false", json);
    }

    [Fact]
    public async Task Golden_file_reads_back_into_the_same_tree()
    {
        // 讀端(JsonDesignSource)與寫端各自一套設定:讀回再寫一次要逐位元回到 golden
        var back = await new JsonDesignSource().GetFrameAsync(new DesignRef(GoldenPath, ""));

        Assert.Equal(Normalize(File.ReadAllText(GoldenPath)),
            Normalize(ReportJson.SerializeSnapshotTree(back, JsonDesignSource.TryReadStableIdAnchors(GoldenPath))));
        var title = Assert.Single(back.Children);
        Assert.Equal(DesignNodeType.Text, title.Type);
        Assert.Equal(new Rgba(17, 24, 39), title.Fill);
        Assert.Equal(0.5, back.Fill!.Value.A);
        Assert.Equal(new Insets(8, 16, 8, 16), back.Padding);
        Assert.Equal("Hello", title.Characters);
        Assert.Equal("HUG", back.LayoutSizingVertical);
    }

    [Theory]
    [InlineData("Frame", "TEXT")]   // 手寫 design JSON 與舊檔的 PascalCase / 大寫
    [InlineData("frame", "text")]
    public async Task Node_type_strings_are_read_in_any_case(string frame, string text)
    {
        using var dir = new TempDir();
        var path = dir.Write("design.json", $$"""
            { "id": "/", "name": "/", "type": "{{frame}}", "box": { "x": 0, "y": 0, "w": 10, "h": 10 },
              "children": [{ "id": "t", "name": "t", "type": "{{text}}", "box": { "x": 0, "y": 0, "width": 5, "height": 5 } }] }
            """);

        var root = await new JsonDesignSource().GetFrameAsync(new DesignRef(path, ""));

        Assert.Equal(DesignNodeType.Frame, root.Type);
        Assert.Equal(DesignNodeType.Text, Assert.Single(root.Children).Type);
        Assert.Equal(new Box(0, 0, 10, 10), root.Box); // 舊的 w/h 也吃
    }

    [Fact]
    public async Task Envelope_without_a_root_is_a_clear_error()
    {
        using var dir = new TempDir();
        var path = dir.Write("snap.json", """{ "schemaVersion": 1, "stableIdAnchors": ["app"] }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new JsonDesignSource().GetFrameAsync(new DesignRef(path, "")));
        Assert.Contains("has a schemaVersion but no \"root\" node", ex.Message);
    }

    [Fact]
    public void Peeking_the_allowlist_never_throws_on_a_broken_file()
    {
        // check 在讀設計稿之前先偷看白名單;壞檔的正式錯誤由 GetFrameAsync 講,這裡只能回 null
        using var dir = new TempDir();

        Assert.Null(JsonDesignSource.TryReadStableIdAnchors(dir.Write("broken.json", "{ \"schemaVersion\": 1, ")));
        Assert.Null(JsonDesignSource.TryReadStableIdAnchors(dir.Write("noanchors.json", """{ "schemaVersion": 1, "root": { "id": "/" } }""")));
    }

    [Fact]
    public async Task Frame_lookup_by_route_inside_a_multi_target_snapshot()
    {
        // 多 target 的快照:包裝根節點 id = "snapshot",各 frame 的 id = route(對 config 的 target.frame)
        var wrapped = new DesignNode("snapshot", "snapshot", DesignNodeType.Frame, default, null, null, null, null, null,
            [SampleTree(), SampleTree() with { Id = "/pricing", Name = "/pricing" }]);
        using var dir = new TempDir();
        var path = dir.Write("parity.snapshot.json", ReportJson.SerializeSnapshotTree(wrapped));

        var frame = await new JsonDesignSource().GetFrameAsync(new DesignRef(path, "/pricing"));

        Assert.Equal("/pricing", frame.Id);
        Assert.Equal("#title", Assert.Single(frame.Children).Id);
    }
}
