using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// 擷取腳本(CaptureScript)量到的東西——量測的源頭,也是 coverlet 看不到的 JS。
/// 一頁塞進所有情境、只開一次瀏覽器(class fixture),每條測試各驗一種行為:
/// config 的 ignore(凍結面 1)、data-parity 與 map 檔的手動錨點、隱形元素、computed style
/// 的換算、祖先 transform: scale 的還原、組合樹(open shadow / slot / 同源 iframe,README
/// 「Shadow DOM / iframes」一節的承諾與限制)。沒裝 Chromium 時每條都印出「略過」。
/// </summary>
public class CaptureScriptTests(CaptureScriptTests.CapturedPage page) : IClassFixture<CaptureScriptTests.CapturedPage>
{
    private const string Skipped = "略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器";

    private const string Html = """
        <!doctype html>
        <html><head><meta charset="utf-8"><style>
          body { margin: 0; font-family: Arial, sans-serif; }
          #styled { width: 200px; height: 50px; padding: 4px 6px 8px 10px; font-size: 18px; font-weight: 700;
                    line-height: 24px; letter-spacing: 1.5px; border-radius: 12px;
                    background: #2563eb; color: rgb(17, 24, 39); }
          #panel { background: #336699; padding: 10px; }
          #inner { width: 50px; height: 20px; }
          #zoom { transform: scale(2); transform-origin: 0 0; width: 100px; height: 40px; }
          #zoom-child { width: 30px; height: 10px; }
          #frame { border: 0; position: absolute; left: 300px; top: 400px; width: 200px; height: 100px; }
        </style></head>
        <body>
          <div id="styled">  Hello
                world  <span id="nested">nested</span></div>
          <div id="panel"><div id="inner"></div></div>
          <div id="gone" style="display: none">x</div>
          <div id="invisible" style="visibility: hidden">x</div>
          <div id="ad" class="ad-slot">ad</div>
          <div id="skipme" data-parity-ignore><p id="skipme-child">skip</p></div>
          <div id="manual" data-parity="Hero Card">m</div>
          <div id="mapped">mapped</div>
          <button id="close" aria-label="Close dialog"></button>
          <div id="tested" data-testid="checkout-form"></div>
          <div id="zoom"><div id="zoom-child"></div></div>
          <my-card id="card"><span id="slotted">slotted text</span></my-card>
          <closed-box id="closed"></closed-box>
          <iframe id="frame" srcdoc="<body style='margin:0'><div id='in-frame' style='width:40px;height:20px;margin:7px 0 0 5px'></div></body>"></iframe>
          <script>
            customElements.define('my-card', class extends HTMLElement {
              constructor() {
                super();
                this.attachShadow({ mode: 'open' }).innerHTML =
                  '<div id="shadow-inner" style="width:60px;height:30px;background:#ff0000"><slot></slot></div>';
              }
            });
            customElements.define('closed-box', class extends HTMLElement {
              constructor() {
                super();
                this.attachShadow({ mode: 'closed' }).innerHTML = '<div id="secret">secret</div>';
              }
            });
          </script>
        </body></html>
        """;

    /// <summary>整頁擷取一次,所有測試共用。Root = null 表示沒有瀏覽器。</summary>
    public sealed class CapturedPage : IAsyncLifetime
    {
        private readonly string _htmlPath = Path.Combine(Path.GetTempPath(), $"parity-capture-{Guid.NewGuid():N}.html");

        public RenderedNode? Root { get; private set; }

        public async Task InitializeAsync()
        {
            await File.WriteAllTextAsync(_htmlPath, Html);
            await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));
            try
            {
                Root = await source.CaptureAsync(new ImplRef(new Uri(_htmlPath).AbsoluteUri, 800, 600)
                {
                    // 第三個是壞掉的 selector:使用者手寫的 config,一條寫錯不能讓整次擷取炸掉
                    IgnoreSelectors = ["[data-parity-ignore]", ".ad-slot", "[[broken"],
                    MapSelectors = new Dictionary<string, string>
                    {
                        ["Mapped Box"] = "#mapped",
                        ["Ghost"] = "#does-not-exist",
                        ["Typo"] = "!!not a selector",
                        ["Inside Shadow"] = "#shadow-inner", // map 檔搆不到 shadow 內(README 寫明的限制)
                    },
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase))
            {
                Root = null;
            }
        }

        public Task DisposeAsync()
        {
            if (File.Exists(_htmlPath)) File.Delete(_htmlPath);
            return Task.CompletedTask;
        }
    }

    private bool Captured(out IReadOnlyList<RenderedNode> all)
    {
        all = page.Root?.DescendantsAndSelf().ToList() ?? [];
        if (page.Root is null) Console.WriteLine(Skipped);
        return page.Root is not null;
    }

    private static RenderedNode ById(IEnumerable<RenderedNode> all, string id) => Assert.Single(all, n => n.DomId == id);

    [Fact]
    public void Config_ignore_selectors_drop_the_element_and_its_whole_subtree()
    {
        if (!Captured(out var all)) return;

        Assert.DoesNotContain(all, n => n.DomId is "skipme" or "skipme-child" or "ad");
        ById(all, "manual"); // 其他元素不受影響(壞掉的 selector 也沒讓擷取失敗)
    }

    [Fact]
    public void Display_none_and_visibility_hidden_elements_and_scripts_are_not_captured()
    {
        if (!Captured(out var all)) return;

        Assert.DoesNotContain(all, n => n.DomId is "gone" or "invisible");
        Assert.DoesNotContain(all, n => n.Tag is "script" or "style");
    }

    [Fact]
    public void Data_parity_attribute_and_map_file_selectors_become_explicit_anchors()
    {
        if (!Captured(out var all)) return;

        Assert.Equal("Hero Card", ById(all, "manual").ExplicitMatch);
        Assert.Equal("Mapped Box", ById(all, "mapped").ExplicitMatch);
        // 找不到的、寫壞的 map selector 不標任何東西,也不讓擷取失敗;map 檔搆不到 shadow 內
        Assert.Equal(["Hero Card", "Mapped Box"], all.Select(n => n.ExplicitMatch).OfType<string>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Aria_label_and_data_testid_are_the_name_anchors()
    {
        if (!Captured(out var all)) return;

        Assert.Equal("Close dialog", ById(all, "close").AriaLabel);
        Assert.Equal("checkout-form", ById(all, "tested").AriaLabel);
    }

    [Fact]
    public void Text_is_the_elements_own_text_with_whitespace_collapsed()
    {
        if (!Captured(out var all)) return;

        Assert.Equal("Hello world", ById(all, "styled").Text); // 子元素的字不算進來
        Assert.Equal("nested", ById(all, "nested").Text);
        Assert.Null(ById(all, "panel").Text);
    }

    [Fact]
    public void Computed_styles_arrive_as_numbers_and_colors()
    {
        if (!Captured(out var all)) return;
        var styled = ById(all, "styled");

        Assert.Equal((216.0, 62.0), (styled.Box.W, styled.Box.H)); // border-box:內容 + 內距
        Assert.Equal(new Parity.Engine.Model.Insets(4, 6, 8, 10), styled.Padding);
        Assert.Equal((18.0, 700.0, 24.0, 1.5), (styled.Typography!.FontSize, styled.Typography.FontWeight,
            styled.Typography.LineHeight, styled.Typography.LetterSpacing));
        Assert.Equal(12, styled.CornerRadius);
        Assert.Equal("#2563EB", styled.Background!.Value.ToHex());
        Assert.Equal("#111827", styled.Color!.Value.ToHex());
    }

    [Fact]
    public void Transparent_element_reports_the_nearest_painted_ancestor_as_effective_background()
    {
        if (!Captured(out var all)) return;
        var inner = ById(all, "inner");

        Assert.True(inner.Background!.Value.IsTransparent);
        Assert.Equal("#336699", inner.EffectiveBackground!.Value.ToHex());
    }

    [Fact]
    public void Ancestor_scale_is_divided_back_out_of_the_geometry()
    {
        // CHANGELOG 0.12:祖先的 scale() 會讓 getBoundingClientRect 量到縮放後的大小,與設計對不上
        if (!Captured(out var all)) return;

        Assert.Equal((100.0, 40.0), (ById(all, "zoom").Box.W, ById(all, "zoom").Box.H));
        Assert.Equal((30.0, 10.0), (ById(all, "zoom-child").Box.W, ById(all, "zoom-child").Box.H));
    }

    [Fact]
    public void Open_shadow_roots_and_slotted_content_are_walked_exactly_once()
    {
        if (!Captured(out var all)) return;

        var inner = Assert.Single(all, n => n.Selector == "#card >>> #shadow-inner");
        Assert.Equal("#FF0000", inner.Background!.Value.ToHex());
        var slotted = ById(all, "slotted"); // 只出現一次:從 slot 帶回來,不會在 host 底下再算一次
        Assert.Contains(inner.DescendantsAndSelf(), n => ReferenceEquals(n, slotted));
        Assert.Equal("slotted text", slotted.Text);
    }

    [Fact]
    public void Closed_shadow_roots_are_skipped_but_the_host_is_still_measured()
    {
        if (!Captured(out var all)) return;

        ById(all, "closed");
        Assert.DoesNotContain(all, n => n.DomId == "secret" || n.Text == "secret");
    }

    [Fact]
    public void Same_origin_iframe_content_is_walked_in_page_coordinates()
    {
        if (!Captured(out var all)) return;

        var inFrame = ById(all, "in-frame");
        Assert.StartsWith("#frame >>> ", inFrame.Selector);
        Assert.Equal((305.0, 407.0, 40.0, 20.0), (inFrame.Box.X, inFrame.Box.Y, inFrame.Box.W, inFrame.Box.H));
    }
}
