using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 清單 #8:文字的相對關係(relation)。文字刻意不比位移,改比「誰在誰上面、誰在誰左邊、誰包住誰」;
/// 明確反過來、或完全跑出容器才報,軟落差。只移動、關係沒變的抓不到(Galen 論文:這種漏報超過 30%)。
/// </summary>
public class TextRelationTests
{
    private static readonly DiffEngine Engine = new(new Tolerances());

    /// <summary>自由擺放的卡片(沒有自動排版、沒有內距),裡面兩個子節點;頁面上它們的框由參數決定。</summary>
    private static Dictionary<string, List<PropDiff>> Card(
        Box titleOnPage, Box bodyOnPage, DesignNodeType bodyType = DesignNodeType.Text, Insets? padding = null, string? layout = null)
    {
        var title = Design("t", "Title", DesignNodeType.Text, box: new Box(20, 20, 200, 24), characters: "Title");
        var body = Design("b", "Body", bodyType, box: new Box(20, 60, 200, 24), characters: bodyType == DesignNodeType.Text ? "Body" : null);
        var card = Design("card", "Card", box: new Box(0, 0, 300, 120), padding: padding, layoutMode: layout, children: [title, body]);
        var root = Design("root", "root", box: new Box(0, 0, 800, 600), children: card);

        var rTitle = Rendered("#t", "h3", text: "Title", box: titleOnPage);
        var rBody = Rendered("#b", "p", text: "Body", box: bodyOnPage);
        var rCard = Rendered("#card", box: new Box(0, 0, 300, 120), children: [rTitle, rBody]);
        var pairs = new List<NodePair> { new(card, rCard, "auto-name"), new(title, rTitle, "auto-text"), new(body, rBody, "auto-text") };
        return Engine.StructuralDiffs(root, Rendered("body", box: new Box(0, 0, 800, 600), children: rCard), pairs);
    }

    private static readonly Box TitleOk = new(20, 20, 200, 24);
    private static readonly Box BodyOk = new(20, 60, 200, 24);

    [Fact]
    public void A_text_moved_past_its_sibling_is_reported_on_the_one_that_moved()
    {
        // Body 被往上移到 Title 上面:Title 沒動,所以報在 Body 身上
        var result = Card(TitleOk, new Box(20, -20, 200, 24));

        var diff = Assert.Single(result.SelectMany(r => r.Value));
        Assert.Equal(("relation", "below Title", "above Title", true), (diff.Prop, diff.Expected, diff.Actual, diff.Soft));
        Assert.True(result.ContainsKey("b"));
    }

    [Fact]
    public void Left_and_right_flip_too()
    {
        // 設計稿並排(Title 在左),頁面上 Title 跑到右邊
        var title = Design("t", "Title", DesignNodeType.Text, box: new Box(0, 0, 100, 24), characters: "Title");
        var price = Design("p", "Price", DesignNodeType.Text, box: new Box(150, 0, 80, 24), characters: "$9");
        var row = Design("row", "Row", box: new Box(0, 0, 300, 24), children: [title, price]);
        var rTitle = Rendered("#t", text: "Title", box: new Box(200, 0, 100, 24));
        var rPrice = Rendered("#p", text: "$9", box: new Box(150, 0, 40, 24));
        var rRow = Rendered("#row", box: new Box(0, 0, 300, 24), children: [rTitle, rPrice]);
        var pairs = new List<NodePair> { new(row, rRow, "auto-name"), new(title, rTitle, "auto-text"), new(price, rPrice, "auto-text") };

        var diff = Assert.Single(Engine.StructuralDiffs(Design("root", "root", box: new Box(0, 0, 800, 600), children: row),
            Rendered("body", children: rRow), pairs).SelectMany(r => r.Value));

        Assert.Equal(("left of Price", "right of Price"), (diff.Expected, diff.Actual));
    }

    [Fact]
    public void A_move_that_keeps_the_relation_or_only_overlaps_is_not_reported()
    {
        // 往下移 20px 但還在 Title 下面:關係沒變,抓不到(已知限制);移到跟 Title 重疊:不是明確反過來,也不報
        Assert.Empty(Card(TitleOk, new Box(40, 80, 200, 24)).SelectMany(r => r.Value));
        Assert.Empty(Card(TitleOk, new Box(20, 30, 200, 24)).SelectMany(r => r.Value));
    }

    [Theory]
    [InlineData("HORIZONTAL")]
    [InlineData(null)]
    public void Containers_already_covered_by_child_order_are_left_to_it(string? layout)
    {
        // 自動排版或有內距的容器已經比 childOrder,relation 不重複報
        var padding = layout is null ? new Insets(20, 20, 20, 20) : (Insets?)null;
        var result = Card(TitleOk, new Box(20, -20, 200, 24), padding: padding, layout: layout);

        Assert.DoesNotContain(result.SelectMany(r => r.Value), d => d.Prop == "relation");
    }

    [Fact]
    public void Two_boxes_without_text_are_left_to_the_offset_comparison()
    {
        // 非文字的兄弟已經比相對位置(offsetX/offsetY),不重複報
        var result = Card(TitleOk, new Box(20, -20, 200, 24), bodyType: DesignNodeType.Frame);

        Assert.Contains(result.SelectMany(r => r.Value), d => d.Prop == "relation"); // Title 是文字,這一對照樣比
        var title = Design("t", "A", box: new Box(20, 20, 200, 24));
        var body = Design("b", "B", box: new Box(20, 60, 200, 24));
        var card = Design("card", "Card", box: new Box(0, 0, 300, 120), children: [title, body]);
        var rCard = Rendered("#card", box: new Box(0, 0, 300, 120), children: [Rendered("#t", box: TitleOk), Rendered("#b", box: new Box(20, -20, 200, 24))]);
        var pairs = new List<NodePair> { new(card, rCard, "auto-name"), new(title, rCard.Children[0], "auto-name"), new(body, rCard.Children[1], "auto-name") };

        Assert.Empty(Engine.StructuralDiffs(Design("root", "root", box: new Box(0, 0, 800, 600), children: card), Rendered("body", children: rCard), pairs));
    }

    [Fact]
    public void Most_texts_leaving_together_means_the_container_was_mismatched_and_nothing_is_reported()
    {
        // 評估語料實測:容器配錯元素,底下 13 段配對正確的字全被報「跑出容器」。真的移位一次只動一個
        DesignNode T(string id, double y) => Design(id, id, DesignNodeType.Text, box: new Box(20, y, 100, 20), characters: id);
        var texts = new[] { T("a", 10), T("b", 40), T("c", 70) };
        var group = Design("g", "Group", box: new Box(0, 0, 300, 100), children: texts);
        var root = Design("root", "root", box: new Box(0, 0, 800, 600), children: group);

        Dictionary<string, List<PropDiff>> Run(params double[] pageY)
        {
            var kids = pageY.Select((y, i) => Rendered($"#{texts[i].Id}", text: texts[i].Id, box: new Box(20, y, 100, 20))).ToArray();
            var rGroup = Rendered("#g", box: new Box(0, 0, 300, 100), children: kids);
            var pairs = new List<NodePair> { new(group, rGroup, "auto-container") };
            pairs.AddRange(texts.Zip(kids, (d, r) => new NodePair(d, r, "auto-text")));
            return Engine.StructuralDiffs(root, Rendered("body", children: rGroup), pairs);
        }

        Assert.DoesNotContain(Run(400, 430, 460).SelectMany(r => r.Value), d => d.Expected.StartsWith("inside", StringComparison.Ordinal));
        var one = Run(10, 40, 400).SelectMany(r => r.Value).Where(d => d.Expected.StartsWith("inside", StringComparison.Ordinal));
        Assert.Single(one);
    }

    [Fact]
    public void A_text_that_leaves_its_container_entirely_is_reported_and_mere_overflow_is_not()
    {
        // 卡片 300×120,Body 被移到卡片外面(完全沒有交集)→ 報;只是凸出去一點(還有交集)→ 不報
        var outside = Card(TitleOk, new Box(20, 200, 200, 24));
        var overflow = Card(TitleOk, new Box(20, 110, 400, 24));

        var diff = Assert.Single(outside.SelectMany(r => r.Value));
        Assert.Equal(("relation", "inside Card", "outside Card"), (diff.Prop, diff.Expected, diff.Actual));
        Assert.Empty(overflow.SelectMany(r => r.Value));
    }
}
