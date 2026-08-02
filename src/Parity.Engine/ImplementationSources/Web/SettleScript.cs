namespace Parity.Engine.ImplementationSources.Web;

/// <summary>
/// 量測前讓版面「定案」的腳本。凍結動畫之後、擷取之前跑。
///
/// 為什麼需要它:凍結 <c>transition</c> 只保證元素不再「動」,不保證它停在**最終狀態**。
/// 捲動觸發的進場效果(IntersectionObserver 加一個 class,例如
/// <c>.reveal{transform:translateY(28px)} .reveal.in{transform:none}</c>)其初始狀態
/// 本身就是位移的——凍結後它就永遠停在動畫第 0 格。造成兩種錯誤:
///   1. **flaky**:首屏邊緣的元素,IO callback 有時趕得上擷取、有時趕不上 → 同一頁兩次量到差 28px
///   2. **系統性量錯**:首屏以下的元素 IO 永遠不觸發 → 一直量在未展開狀態,而非設計意圖的版面
/// (dogfooding 實測:cornhsu.com 的 /works 六次跑出三次 FAIL,差值正好是該站 translateY 的 28px。)
///
/// 作法兩段:
///   - **走過整頁**:以視窗高為步距捲到底再捲回原位,把所有 IntersectionObserver 叫起來。
///     動畫已凍結,所以 class 一加上去版面就瞬間到位,不必等 transition 的時間。
///     捲軸位置會還原——attach 模式面對的是使用者活著的 app,不能留下痕跡。
///   - **等穩定**:連續兩次量到相同的版面簽章才算定案(上限 40 幀,約 0.7 秒),
///     順便涵蓋字型換置、延遲載入圖片等其他「晚一步」的版面變動。
///
/// 已知限制:與凍結樣式相同,搆不到 closed shadow root;捲動不會觸發需要真實使用者手勢的效果。
/// </summary>
internal static class SettleScript
{
    public const string Js = """
        async () => {
          // 等兩幀:第一幀讓瀏覽器跑完 IntersectionObserver callback,第二幀讓它套用版面
          const twoFrames = () => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));

          const startX = window.scrollX, startY = window.scrollY;
          const step = Math.max(1, window.innerHeight);

          // ── 走過整頁,叫醒捲動觸發的效果 ──
          // 每步重讀 scrollHeight:捲下去才載入的內容會把頁面撐長。
          // 上限 200 步,免得無限捲動的頁面把擷取卡死。
          let y = 0, steps = 0;
          while (y <= document.documentElement.scrollHeight && steps < 200) {
            window.scrollTo(0, y);
            await twoFrames();
            y += step;
            steps++;
          }
          window.scrollTo(startX, startY);
          await twoFrames();

          // ── 等版面穩定 ──
          // 簽章 = 所有元素的位置尺寸取整後的雜湊 + 文件總高。
          // 取整是刻意的:次像素抖動不該讓「穩定」永遠等不到。
          const signature = () => {
            let h = 0;
            for (const el of document.querySelectorAll('*')) {
              const r = el.getBoundingClientRect();
              h = (h * 31 + (r.top | 0) + (r.left | 0) * 7 + (r.width | 0) * 13 + (r.height | 0) * 17) | 0;
            }
            return h + ':' + document.documentElement.scrollHeight;
          };

          let prev = signature(), stable = 0, frames = 0;
          while (stable < 2 && frames < 40) {
            await twoFrames();
            const now = signature();
            if (now === prev) { stable++; } else { stable = 0; prev = now; }
            frames++;
          }

          // 回傳供除錯:實測 21 頁的真實網站,scrollSteps 2–16、settleFrames 2–7,
          // 兩個上限都離很遠——若哪天看到貼著上限,才是這裡要調的訊號。
          return { scrollSteps: steps, settleFrames: frames, settled: stable >= 2 };
        }
        """;
}
