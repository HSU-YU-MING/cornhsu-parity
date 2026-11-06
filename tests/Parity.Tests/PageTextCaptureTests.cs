using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 整頁文字(text-not-found 的依據)要涵蓋「畫面上看得到、但 document.body.innerText 沒有」的字——
/// 漏收一種,開了 gate.failOnUnmatched 就會把看得到的文字誤標成 text-not-found、誤擋 PR。
/// Chromium 實測 innerText 收不到的:同源 iframe、shadow DOM、&lt;input&gt;/&lt;textarea&gt; 的值與 placeholder、
/// ::before/::after 的 content;SVG 的 &lt;text&gt; 收得到,但 SVG 在擷取樹裡是葉子(byText 永遠配不到),一樣要驗。
/// 每種陷阱一條:設計稿的文字刻意讓 byText 配不到(跨元素拆開、在 byText 看不到的地方),
/// 原因必須是 ambiguous-or-missing-text 而不是 text-not-found。一頁、一次擷取,沒裝 Chromium 時每條印「略過」。
/// </summary>
public class PageTextCaptureTests(PageTextCaptureTests.CapturedPage page) : IClassFixture<PageTextCaptureTests.CapturedPage>
{
    private const string Skipped = "略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器";

    private const string Html = """
        <!doctype html>
        <html><head><meta charset="utf-8"><style>
          body { margin: 0; font-family: Arial, sans-serif; }
          .price::before { content: "$"; }
          .tag::after { content: attr(data-label) " \2192"; }
          .shout { text-transform: uppercase; }
          iframe { border: 0; width: 300px; height: 80px; }
        </style></head>
        <body>
          <p>Hello <b>world</b></p>
          <p class="shout">quiet <b>words</b></p>
          <iframe id="same" srcdoc="<p>Framed <b>split</b> words</p><input value='Framed input'>"></iframe>
          <iframe id="cross" src="other.html"></iframe>
          <x-card></x-card>
          <p>Hi <x-name></x-name> there</p>
          <input value="Typed value"> <input placeholder="Search the docs"> <input type="submit" value="Send message">
          <textarea id="area"></textarea>
          <input type="password" value="hunter2secret">
          <p>Price: <span class="price">99</span></p>
          <span class="tag" data-label="New arrival"></span>
          <svg width="200" height="40"><text x="0" y="20">Chart <tspan>label</tspan></text><title>Svg tooltip</title></svg>
          <p>請先<b>登入</b><br>帳號</p>
          <p>歡迎使用<b>我們的</b>服務</p>
          <div><span>Total:</span><span style="margin-left: 4px">$10</span></div>
          <p>Don&rsquo;t miss out</p>
          <p>Inter&shy;nationalization</p>
          <div style="display: none">Hidden copy</div>
          <div style="visibility: hidden">Invisible copy</div>
          <script>
            // 使用者打進去的字只在 .value 裡(沒有子文字節點,byText 看不到)
            document.getElementById('area').value = 'Area typed text';
            customElements.define('x-card', class extends HTMLElement {
              constructor() {
                super();
                const inner = '<p>Shadow <i>only</i> copy</p><x-deep></x-deep><input placeholder="Shadow field">';
                this.attachShadow({ mode: 'open' }).innerHTML = inner;
              }
            });
            customElements.define('x-deep', class extends HTMLElement {
              constructor() { super(); this.attachShadow({ mode: 'open' }).innerHTML = '<span>Nested <b>shadow</b> text</span>'; }
            });
            customElements.define('x-name', class extends HTMLElement {
              constructor() { super(); this.attachShadow({ mode: 'open' }).innerHTML = '<b>Ada</b>'; }
            });
          </script>
        </body></html>
        """;

    /// <summary>整頁擷取一次,所有測試共用。Root = null 表示沒有瀏覽器。</summary>
    public sealed class CapturedPage : IAsyncLifetime
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("parity-pagetext-").FullName;

        public RenderedNode? Root { get; private set; }

        public async Task InitializeAsync()
        {
            var html = Path.Combine(_dir, "page.html");
            await File.WriteAllTextAsync(html, Html);
            // file:// 頁面互相是不同來源(Chromium 預設):拿來當跨域 iframe,不必起伺服器
            await File.WriteAllTextAsync(Path.Combine(_dir, "other.html"), "<p>Cross origin words</p>");
            await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));
            try
            {
                Root = await source.CaptureAsync(new ImplRef(new Uri(html).AbsoluteUri, 800, 900));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase))
            {
                Root = null;
            }
        }

        public Task DisposeAsync()
        {
            Directory.Delete(_dir, recursive: true);
            return Task.CompletedTask;
        }
    }

    /// <summary>設計稿只有一個文字節點(圖層名對不到頁面上任何 id/class)時,它的未配對原因;配上了回 null。</summary>
    private string? ReasonFor(string characters)
    {
        var design = Design("1", "frame", children: Design("2", "copy-under-test", DesignNodeType.Text, characters: characters));
        return Matcher.Match(design, page.Root!).Unmatched.SingleOrDefault()?.Reason;
    }

    private bool Captured()
    {
        if (page.Root is null) Console.WriteLine(Skipped);
        return page.Root is not null;
    }

    private string NormalizedPageText => Matcher.NormalizeText(page.Root!.PageText!);

    [Fact]
    public void The_root_carries_the_page_text_and_a_missing_text_is_text_not_found()
    {
        if (!Captured()) return;

        Assert.NotNull(page.Root!.PageText);
        Assert.All(page.Root.DescendantsAndSelf().Skip(1), n => Assert.Null(n.PageText)); // 只掛在根節點
        Assert.Equal("text-not-found", ReasonFor("Get Started"));
    }

    [Theory]
    [InlineData("Hello world")]  // Hello <b>world</b>:byText 只有 "Hello" 與 "world"
    [InlineData("QUIET WORDS")]  // text-transform: uppercase,而且同樣拆在兩個元素
    public void Text_split_across_elements_or_transformed_is_found(string characters)
    {
        if (!Captured()) return;

        Assert.Equal("ambiguous-or-missing-text", ReasonFor(characters));
    }

    [Theory]
    [InlineData("Framed split words")]
    [InlineData("Framed input")]
    public void Same_origin_iframe_text_is_found(string characters)
    {
        if (!Captured()) return;

        Assert.Equal("ambiguous-or-missing-text", ReasonFor(characters));
    }

    [Theory]
    [InlineData("Shadow only copy")]     // shadow root 內、跨元素拆開
    [InlineData("Nested shadow text")]   // shadow 裡的 shadow
    [InlineData("Hi Ada there")]         // light DOM 的字夾著 shadow 的字
    [InlineData("Shadow field")]         // shadow 裡的 placeholder
    public void Shadow_dom_text_is_found(string characters)
    {
        if (!Captured()) return;

        Assert.Equal("ambiguous-or-missing-text", ReasonFor(characters));
    }

    [Theory]
    [InlineData("Typed value")]
    [InlineData("Search the docs")]      // placeholder
    [InlineData("Send message")]         // <input type=submit value>
    [InlineData("Area typed text")]      // <textarea> 的 .value
    public void Form_control_values_and_placeholders_are_found(string characters)
    {
        if (!Captured()) return;

        Assert.Equal("ambiguous-or-missing-text", ReasonFor(characters));
    }

    [Theory]
    [InlineData("Price: $99")]           // 父層的字 + 子元素 ::before
    [InlineData("New arrival →")]        // content: attr(...) 加 CSS 跳脫字元
    public void Pseudo_element_content_is_found(string characters)
    {
        if (!Captured()) return;

        Assert.Equal("ambiguous-or-missing-text", ReasonFor(characters));
    }

    [Theory]
    [InlineData("歡迎使用\n我們的服務")]  // 設計稿手動斷行,頁面是一整行
    [InlineData("請先登入帳號")]          // 頁面用 <br> 斷行,設計稿一整行
    [InlineData("Total: $10")]            // 兩個行內元素靠 margin 隔開,innerText 沒有空白
    public void Whitespace_differences_across_elements_are_not_missing_text(string characters)
    {
        if (!Captured()) return;

        Assert.Equal("ambiguous-or-missing-text", ReasonFor(characters));
    }

    [Theory]
    [InlineData("Don't miss out")]        // 設計稿直引號,頁面彎引號
    [InlineData("Internationalization")]  // 頁面有軟連字號(&shy;),畫面上通常看不到
    public void Typography_differences_inside_one_element_are_paired(string characters)
    {
        // 文字都在同一個元素裡:放寬的文字比對配得上,不會進未配對清單
        if (!Captured()) return;

        Assert.Null(ReasonFor(characters));
    }

    [Fact]
    public void Svg_text_is_found_although_svg_is_a_leaf_in_the_capture_tree()
    {
        if (!Captured()) return;

        Assert.Equal("ambiguous-or-missing-text", ReasonFor("Chart label"));
        Assert.DoesNotContain("Svg tooltip", NormalizedPageText); // <title> 是滑過才出現的提示,不是畫面上的字
    }

    [Fact]
    public void Hidden_text_and_password_values_are_not_page_text()
    {
        if (!Captured()) return;

        var text = NormalizedPageText;
        Assert.DoesNotContain("Hidden copy", text);
        Assert.DoesNotContain("Invisible copy", text);
        Assert.DoesNotContain("hunter2secret", text); // 畫面上是圓點
        Assert.Equal("text-not-found", ReasonFor("Hidden copy"));
    }

    [Fact]
    public void Cross_origin_iframe_text_is_out_of_reach_and_counts_as_not_found()
    {
        // README 寫明的限制:語意是「在 Parity 看得到的範圍內找不到」。擷取本身不能因此失敗
        if (!Captured()) return;

        Assert.DoesNotContain("Cross origin words", NormalizedPageText);
        Assert.Equal("text-not-found", ReasonFor("Cross origin words"));
    }
}
