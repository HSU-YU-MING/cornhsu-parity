using Parity.Engine.ImplementationSources;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// 量測前的版面定案(<c>SettleScript</c>)。
///
/// 這是**唯一需要真實瀏覽器**的測試檔——其餘測試都是純邏輯。沒裝 Chromium 時自動略過
/// (不讓「還沒 install-browser」變成假的紅燈);CI 會先裝好瀏覽器,所以在 CI 上一定會真的跑。
///
/// 背景:凍結 transition 只保證元素不再動,不保證它停在最終狀態。捲動觸發的進場效果
/// (IntersectionObserver 加 class)初始狀態本身就是位移的,不叫醒它就會量到動畫第 0 格。
/// dogfooding 實測:cornhsu.com 的 /works 同一份 HTML 六次跑出三次 FAIL,
/// 差值正好是該站 <c>.reveal</c> 的 translateY(28px)。
/// </summary>
public class SettleCaptureTests : IDisposable
{
    // 進場位移量。刻意用怪數字:若測試通過純屬巧合(例如量到別的元素),這個值不會剛好對上。
    private const int RevealShiftPx = 37;

    // 目標元素放在首屏之外,確保 IntersectionObserver 在初始畫面**不會**觸發。
    // 這是本測試能確定性失敗的關鍵:不賭 callback 時序,而是它根本不會自己發生。
    private const int ViewportHeight = 600;
    private const int TargetTopPx = 2000;

    private readonly string _htmlPath;

    // 不是 const:內插洞裡是 int 常數,轉字串不算常數運算(CS0133)。
    private static readonly string Html = $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><style>
          body { margin: 0; }
          .spacer { height: {{TargetTopPx}}px; }
          .reveal { opacity: 0; transform: translateY({{RevealShiftPx}}px); transition: all .9s; }
          .reveal.in { opacity: 1; transform: none; }
          #target { height: 100px; background: #334455; }
        </style></head>
        <body>
          <div class="spacer"></div>
          <div id="target" class="reveal"></div>
          <script>
            const io = new IntersectionObserver(es => es.forEach(e => {
              if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
            }));
            document.querySelectorAll('.reveal').forEach(el => io.observe(el));
          </script>
        </body></html>
        """;

    public SettleCaptureTests()
    {
        _htmlPath = Path.Combine(Path.GetTempPath(), $"parity-settle-{Guid.NewGuid():N}.html");
        File.WriteAllText(_htmlPath, Html);
    }

    public void Dispose()
    {
        if (File.Exists(_htmlPath)) File.Delete(_htmlPath);
    }

    [Fact]
    public async Task 首屏之外的進場元素_量到展開後的位置而非動畫第0格()
    {
        await using var source = new WebImplementationSource(new WebCaptureOptions(Headless: true));

        RenderedNode root;
        try
        {
            root = await source.CaptureAsync(new ImplRef(
                Url: new Uri(_htmlPath).AbsoluteUri,
                ViewportWidth: 800,
                ViewportHeight: ViewportHeight));
        }
        catch (Exception ex) when (BrowserMissing(ex))
        {
            // 沒瀏覽器就不算數。刻意印出來:靜靜地綠燈會讓人以為這條驗過了。
            Console.WriteLine("略過:未安裝 Chromium(parity install-browser);此測試需要真實瀏覽器");
            return;
        }

        var target = root.DescendantsAndSelf().SingleOrDefault(n => n.DomId == "target");
        Assert.NotNull(target);

        // 沒定案的話會量到 TargetTopPx + RevealShiftPx。先斷言這個具體的錯,
        // 讓失敗訊息直接說出「是哪一種錯」,而不是只丟兩個對不上的數字。
        Assert.False(
            Math.Abs(target!.Box.Y - (TargetTopPx + RevealShiftPx)) < 1,
            $"量到動畫第 0 格:top={target.Box.Y},正好是 {TargetTopPx}+{RevealShiftPx}。" +
            "表示 IntersectionObserver 沒被叫醒——SettleScript 的「走過整頁」失效。");

        Assert.Equal(TargetTopPx, target.Box.Y, precision: 0);
    }

    private static bool BrowserMissing(Exception ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
}
