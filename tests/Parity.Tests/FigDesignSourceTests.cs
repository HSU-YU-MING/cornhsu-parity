using System.Text.Json.Nodes;
using Parity.Cli;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Fig;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// .fig 設計來源(2.5.0,競品清單「不走 REST API 的 Figma 來源」)。檔案由 <see cref="FigTestFiles"/> 照 Kiwi 規則產生:
/// 驗的是容器、解碼、座標、auto-layout、文字、實例展開與覆寫。欄位名跟真的 Figma 檔一致與否,另外拿真檔校準。
/// </summary>
public class FigDesignSourceTests
{
    private static string Write(TempDir dir, byte[] bytes, string name = "design.fig") => dir.Write(name, "") is var p ? WriteBytes(p, bytes) : p;

    private static string WriteBytes(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static JsonObject Rest(string path, string node = "1:2") => new FigDesignSource().GetRestJson(path, node);

    // ---------- Kiwi ----------

    [Theory]
    [InlineData(0f)]
    [InlineData(0.1f)]
    [InlineData(-2.5f)]
    [InlineData(12345.678f)]
    [InlineData(1e-30f)]
    public void Kiwi_floats_round_trip(float value)
    {
        var bytes = new KiwiWriter().Float(value).ToArray();
        var read = new KiwiReader(bytes).ReadVarFloat();
        Assert.Equal(value, read); // 很小的正規數照樣保留
    }

    [Fact]
    public void Kiwi_integers_and_strings_round_trip()
    {
        var bytes = new KiwiWriter().VarUint(0).VarUint(300).VarUint(uint.MaxValue).VarInt(-1).VarInt(-300).VarInt(int.MaxValue).String("中文 ✓").String("").ToArray();
        var r = new KiwiReader(bytes);

        Assert.Equal((0u, 300u, uint.MaxValue), (r.ReadVarUint(), r.ReadVarUint(), r.ReadVarUint()));
        Assert.Equal((-1, -300, int.MaxValue), (r.ReadVarInt(), r.ReadVarInt(), r.ReadVarInt()));
        Assert.Equal(("中文 ✓", ""), (r.ReadString(), r.ReadString()));
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void A_message_decodes_by_the_schema_it_carries()
    {
        var schema = KiwiSchema.Decode(FigTestFiles.Schema());
        var message = schema.DecodeMessage(FigTestFiles.Message(), "Message");

        var nodes = message["nodeChanges"]!.AsArray();
        Assert.Equal(20, nodes.Count);
        var button = nodes.Single(n => (string?)n!["name"] == "Button")!;
        Assert.Equal("RECTANGLE", (string?)button["type"]); // enum → 成員名稱
        Assert.Equal(8, (double)button["cornerRadius"]!);
        Assert.Equal(3u, (uint)button["vectorData"]!["byteLength"]!); // byte 陣列只留長度
        Assert.Equal(0.1450980, (double)button["fillPaints"]![0]!["color"]!["r"]!, 6);
    }

    [Fact]
    public void Truncated_kiwi_data_is_a_format_error_not_a_crash()
    {
        var schema = KiwiSchema.Decode(FigTestFiles.Schema());
        var message = FigTestFiles.Message();

        Assert.Throws<KiwiFormatException>(() => schema.DecodeMessage(message[..(message.Length / 2)], "Message"));
    }

    // ---------- 容器 ----------

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Zipped_or_raw_files_with_zstd_or_deflate_all_read(bool zipped, bool zstd)
    {
        using var dir = new TempDir();
        var canvas = FigTestFiles.Canvas(zstd: zstd);
        var path = Write(dir, zipped ? FigTestFiles.Zip(canvas) : canvas);

        Assert.Equal("Home", (string?)Rest(path)["name"]);
    }

    [Fact]
    public void A_file_that_is_not_a_fig_says_how_to_make_one()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<InvalidOperationException>(() => Rest(Write(dir, "{\"not\":\"fig\"}"u8.ToArray())));
        Assert.Contains("File → Save local copy", ex.Message);
    }

    [Fact]
    public void A_zip_without_canvas_fig_is_named_as_such()
    {
        using var dir = new TempDir();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("other.txt");
        var ex = Assert.Throws<InvalidOperationException>(() => Rest(Write(dir, ms.ToArray())));
        Assert.Contains("has no canvas.fig inside", ex.Message);
    }

    [Fact]
    public void An_old_format_version_is_refused()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<InvalidOperationException>(() => Rest(Write(dir, FigTestFiles.Canvas(version: 10))));
        Assert.Contains("format version 10, older than Parity can read", ex.Message);
    }

    [Fact]
    public void A_broken_file_points_at_the_api_or_a_snapshot_and_names_a_newer_version()
    {
        using var dir = new TempDir();
        var good = FigTestFiles.Canvas(version: 150);
        var broken = good[..^20];
        var ex = Assert.Throws<InvalidOperationException>(() => Rest(Write(dir, broken)));
        Assert.Contains("could not read this .fig file", ex.Message);
        Assert.Contains("newer than the newest Parity was tested with", ex.Message);
        Assert.Contains("figmaFileKey", ex.Message);
    }

    // ---------- 節點 → REST 形狀 ----------

    [Fact]
    public void Boxes_are_absolute_children_follow_their_position_and_hidden_nodes_are_marked()
    {
        using var dir = new TempDir();
        var home = Rest(Write(dir, FigTestFiles.Canvas()));

        Assert.Equal((100.0, 50.0, 400.0, 300.0), BoxOf(home));
        var kids = home["children"]!.AsArray();
        Assert.Equal(["Title", "Hidden", "Button", "Card", "Styled", "Unfilled"], kids.Select(k => (string?)k!["name"])); // parentIndex.position 排序
        Assert.Equal((116.0, 74.0, 200.0, 32.0), BoxOf(kids[0]!));
        Assert.False((bool)kids[1]!["visible"]!);
    }

    [Fact]
    public void Node_ids_take_either_figma_link_form()
    {
        using var dir = new TempDir();
        var path = Write(dir, FigTestFiles.Canvas());
        Assert.Equal("1:2", (string?)Rest(path, "1-2")["id"]);
    }

    [Fact]
    public void An_unknown_node_lists_the_frames_in_the_file()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<InvalidOperationException>(() => Rest(Write(dir, FigTestFiles.Canvas()), "9:9"));
        Assert.Contains("Page 1 / Home (1:2)", ex.Message);
        Assert.Contains("Components / Card (1:10)", ex.Message);
    }

    [Fact]
    public async Task The_design_tree_matches_what_the_api_route_would_parse()
    {
        // 交給 FigmaNodeParser:auto-layout、文字、填色、圓角都走 REST 那一條的規則
        using var dir = new TempDir();
        var root = await new FigDesignSource().GetFrameAsync(new DesignRef(Write(dir, FigTestFiles.Canvas()), "1:2"));

        Assert.Equal(("1:2", "Home", DesignNodeType.Frame), (root.Id, root.Name, root.Type));
        Assert.Equal(new Insets(24, 16, 24, 16), root.Padding);
        Assert.Equal((12.0, "VERTICAL"), (root.ItemSpacing, root.LayoutMode));
        Assert.Equal(new Rgba(255, 255, 255), root.Fill);

        var title = root.Children[0];
        Assert.Equal(DesignNodeType.Text, title.Type);
        Assert.Equal("Title", title.Characters);
        Assert.Equal(("Inter", 24.0, 600.0, 32.0, -0.48), (title.Text!.FontFamily, title.Text.FontSize, title.Text.FontWeight, title.Text.LineHeight, title.Text.LetterSpacing));

        Assert.Equal(["Title", "Button", "Card", "Styled", "Unfilled"], root.Children.Select(c => c.Name)); // 隱藏的節點不進設計樹
        var button = root.Children[1];
        Assert.Equal((new Rgba(0x25, 0x63, 0xEB), 8.0), (button.Fill, button.CornerRadius));
    }

    [Fact]
    public async Task An_instance_shows_its_component_with_overrides_applied()
    {
        // 實例的子節點不在實例底下:從元件複製、套覆寫。id 照 REST 的寫法(I實例;子節點)
        using var dir = new TempDir();
        var root = await new FigDesignSource().GetFrameAsync(new DesignRef(Write(dir, FigTestFiles.Canvas()), "1:2"));

        var card = root.Children[2];
        Assert.Equal(DesignNodeType.Instance, card.Type);
        Assert.Equal(["Body", "Icon"], card.Children.Select(c => c.Name));
        // 覆寫的路徑只有一段(1:11),中間隔著元件裡的 frame「Body」:照樣套得上
        var label = Assert.Single(card.Children[0].Children);
        Assert.Equal(("I1:20;1:11", "Label", "Buy"), (label.Id, label.Name, label.Characters));
        Assert.Equal(new Box(124, 260, 100, 20), label.Box); // 元件裡的位置疊到實例的位置上
        Assert.Equal(400.0, label.Text!.FontWeight);
    }

    [Fact]
    public async Task An_instance_takes_what_it_does_not_set_from_its_component()
    {
        // 實例自己沒存填色、排版:跟元件一樣(白底、水平、space-between)。沒存的右/下內距是 0
        using var dir = new TempDir();
        var root = await new FigDesignSource().GetFrameAsync(new DesignRef(Write(dir, FigTestFiles.Canvas()), "1:2"));

        var card = root.Children[2];
        Assert.Equal((new Rgba(255, 255, 255), "HORIZONTAL", "SPACE_BETWEEN"), (card.Fill, card.LayoutMode, card.PrimaryAxisAlignItems));
        Assert.Equal(new Insets(0, 0, 0, 0), card.Padding);
    }

    [Fact]
    public async Task A_swapped_nested_instance_shows_the_new_component()
    {
        // instance swap:元件裡的 Icon 在這個實例被換成 Tag,內容跟著換
        using var dir = new TempDir();
        var root = await new FigDesignSource().GetFrameAsync(new DesignRef(Write(dir, FigTestFiles.Canvas()), "1:2"));

        var word = Assert.Single(root.Children[2].Children[1].Children);
        Assert.Equal(("I1:20;1:13;1:31", "Tag"), (word.Id, word.Characters));
    }

    [Fact]
    public async Task Fill_styles_win_over_the_cached_fill_and_an_empty_style_means_no_fill()
    {
        // 真檔校準:節點存的填色是快取;引用的樣式才是 Figma 畫出來的顏色。樣式沒有任何填色 = 沒有填色
        using var dir = new TempDir();
        var root = await new FigDesignSource().GetFrameAsync(new DesignRef(Write(dir, FigTestFiles.Canvas()), "1:2"));

        Assert.Equal(new Rgba(255, 0, 0), root.Children[3].Fill);
        Assert.Null(root.Children[4].Fill);
    }

    [Theory]
    [InlineData("Thin", 100)]
    [InlineData("Extra Light", 200)]
    [InlineData("Light Italic", 300)]
    [InlineData("Regular", 400)]
    [InlineData("Medium", 500)]
    [InlineData("SemiBold", 600)]
    [InlineData("Semi Bold", 600)]
    [InlineData("Bold", 700)]
    [InlineData("Extra-Bold", 800)]
    [InlineData("Black", 900)]
    [InlineData("Condensed", 400)]
    public void Font_style_names_map_to_weights(string style, double weight)
        => Assert.Equal(weight, FigDocument.WeightOf(style));

    // ---------- 設定 ----------

    [Fact]
    public void A_fig_design_file_is_figma_data_for_links_stats_and_bug_reports()
    {
        using var dir = new TempDir();
        var config = ParityConfig.Load(dir.Write("parity.config.json", """
            { "designFile": "design.FIG", "figmaFileKey": "abc", "targets": [{ "route": "/", "frame": "1:2", "url": "x" }] }
            """));

        Assert.True(config.DesignFileIsFig);
        Assert.Equal("abc", config.FigmaLinkFileKey); // 節點 id 跟 Figma 一樣:連結照樣連得回去
        Assert.Equal("figma", UsageStats.DesignSourceOf(config));
        Assert.Contains("design source: Figma .fig file (designFile)", string.Join("\n", ConfigSummary.From(config).Lines()));
        Assert.IsType<FigDesignSource>(ScanSession.CreateDesignSource(config, refresh: false));
    }

    private static (double, double, double, double) BoxOf(JsonNode n)
    {
        var b = n["absoluteBoundingBox"]!;
        return ((double)b["x"]!, (double)b["y"]!, (double)b["width"]!, (double)b["height"]!);
    }
}
