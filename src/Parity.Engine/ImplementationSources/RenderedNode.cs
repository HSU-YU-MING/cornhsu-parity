using System.Text.Json.Serialization;
using Parity.Engine.Model;

namespace Parity.Engine.ImplementationSources;

/// <summary>從實作端讀出的節點(規畫書 4.5)。網頁來自 DOM,未來桌面來自 UI 樹。</summary>
public sealed record RenderedNode(
    string Selector,
    string Tag,
    string? Text,
    Box Box,
    Rgba? Color,
    Rgba? Background,
    Typography? Typography,
    Insets Padding,
    IReadOnlyList<RenderedNode> Children)
{
    /// <summary>手動錨點:data-parity 屬性值,或 map 檔選中的圖層名(規畫書 4.7)。</summary>
    public string? ExplicitMatch { get; init; }

    /// <summary>沿祖先鏈找到的第一個不透明背景(避免 transparent 誤報)。</summary>
    public Rgba? EffectiveBackground { get; init; }

    public string? DomId { get; init; }
    public string? Classes { get; init; }
    public string? AriaLabel { get; init; }
    public double? CornerRadius { get; init; }

    /// <summary>
    /// margin 四邊(computed,auto 是瀏覽器算出來的值)。只給內距的等效判斷用:設計稿的 padding 在頁面上可能是子元素的 margin
    /// 撐出來的(<see cref="Comparison.DiffEngine"/> 的 EffectivePadding)。擷取端沒提供 = null(假來源、舊測試),當成 0。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Insets? Margin { get; init; }

    /// <summary>position: absolute 或 fixed:不參與版面流,子元件順序與容器對齊都不算它。</summary>
    public bool OutOfFlow { get; init; }

    /// <summary>元素自己的 opacity(不累積祖先)。</summary>
    public double? Opacity { get; init; }

    public Insets? BorderWidth { get; init; }

    /// <summary>第一條有粗細的邊的顏色;沒有邊框是 null。</summary>
    public Rgba? BorderColor { get; init; }

    /// <summary>box-shadow。看不見的陰影不算(<see cref="CssEffects.ParseShadows"/>)。</summary>
    public IReadOnlyList<ShadowSpec>? Shadows { get; init; }

    /// <summary>text-shadow;元素沒有文字時是 null。</summary>
    public IReadOnlyList<ShadowSpec>? TextShadows { get; init; }

    /// <summary>background-image 的第一個漸層;沒有漸層是 null。</summary>
    public GradientSpec? Gradient { get; init; }

    /// <summary>元素自己的文字佔幾行(量實際的行框);沒有文字或裡面有區塊子元素時是 null。</summary>
    public int? LineCount { get; init; }

    /// <summary>背景是圖片(background-image: url(…))。</summary>
    public bool HasBackgroundImage { get; init; }

    /// <summary><c>img</c> 載入失敗(已經完成載入、卻沒有像素)。</summary>
    public bool ImageBroken { get; init; }

    /// <summary>
    /// 整頁文字(只有根節點有;擷取端沒提供 = null,例如假來源與舊測試)。**只給配對用**:
    /// Matcher 拿它判斷設計稿的文字是不是整頁都找不到(<c>text-not-found</c>)。
    /// byText 只收元素「自己的」文字(<c>Hello &lt;b&gt;world&lt;/b&gt;</c> 會被拆開),不能拿來判斷「找不到」,
    /// 所以另外收一份。內容是頁面上的所有文字——**不能外流**:不進 report.json、快照、統計、bug-report;
    /// <c>[JsonIgnore]</c> 是因為 serve 的 /api/detail 會把整棵樹序列化給本機 UI。
    /// <c>with</c> 運算式(Normalizer)會原樣帶著它。
    /// </summary>
    [JsonIgnore]
    public string? PageText { get; init; }

    /// <summary>
    /// 有子元素的節點,連子孫一起的文字(<see cref="Text"/> 只有元素自己的:<c>&lt;h1&gt;&lt;span&gt;Title&lt;/span&gt;&lt;/h1&gt;</c> 是 null)。
    /// 只給文字內容比對用。保留文件順序,所以 <c>Hello &lt;b&gt;world&lt;/b&gt; again</c> 不會亂掉;
    /// 用 textContent 不用 innerText:innerText 會被 text-transform 改掉。沒有子元素、子元素太多或裡面有 script 時是 null。
    /// 長度上限是 <see cref="Comparison.DiffEngine.TextContentLimit"/>,到上限代表被截斷。
    /// </summary>
    [JsonIgnore]
    public string? TextContent { get; init; }

    /// <summary>
    /// 子元素全部是行內的:<see cref="TextContent"/> 是同一段文字被 <c>&lt;b&gt;</c>、<c>&lt;br&gt;</c> 拆開,不是好幾段文字疊在一起。
    /// 只有這種才能用來配對文字節點:區塊容器的字體是別的元素的,配上去就是假落差。
    /// </summary>
    [JsonIgnore]
    public bool TextIsInline { get; init; }

    public IEnumerable<RenderedNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children ?? [])
            foreach (var d in child.DescendantsAndSelf())
                yield return d;
    }
}

/// <summary>指向實作端某個畫面的參照。網頁=URL;視窗大小由引擎依 frame 尺寸補上(規畫書 4.5)。</summary>
public sealed record ImplRef(
    string Url,
    int? ViewportWidth = null,
    int? ViewportHeight = null)
{
    /// <summary>map 檔的「圖層名 → CSS selector」對應,擷取時在頁面內解析並標記。</summary>
    public IReadOnlyDictionary<string, string>? MapSelectors { get; init; }

    /// <summary>要忽略的 selector 清單(如 "[data-parity-ignore]")。</summary>
    public IReadOnlyList<string>? IgnoreSelectors { get; init; }

    /// <summary>
    /// 允許當 selector 錨點的 id 白名單(snapshot 的連拍實測產物,見 SnapshotDocument
    /// .StableIdAnchors)。null = 不限制(現行行為);有名單時,名單外的 id 一律走結構路徑
    /// ——純字母隨機 id(rkxvdnnzty)字元啟發法認不出,實測名單認得出。
    /// </summary>
    public IReadOnlyList<string>? AllowedIdAnchors { get; init; }

    /// <summary>
    /// 這一次擷取要不要拍參考截圖(還需要來源本身開 CaptureScreenshot 才生效)。
    /// 截圖字典以 URL 為鍵、後拍覆寫先拍——snapshot 的探測/連拍擷取若照拍,
    /// 「凍結的樹」與「參考截圖」就會來自不同次載入,疊框視圖對不齊。
    /// 探測性擷取設 false,截圖永遠屬於被凍結的那一拍。
    /// </summary>
    public bool CaptureScreenshot { get; init; } = true;
}

/// <summary>實作來源抽象——留給 WPF/桌面的門(規畫書 4.5)。</summary>
public interface IImplementationSource
{
    Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct = default);
}
