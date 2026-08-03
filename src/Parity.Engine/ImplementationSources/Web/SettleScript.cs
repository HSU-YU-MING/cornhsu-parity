namespace Parity.Engine.ImplementationSources.Web;

/// <summary>
/// 量測前讓版面「定案」的腳本組。凍結動畫之後、擷取之前跑。
///
/// 為什麼需要:凍結 <c>transition</c> 只保證元素不再「動」,不保證它停在**最終狀態**。
/// 捲動觸發的進場效果(IntersectionObserver 加 class,例如
/// <c>.reveal{transform:translateY(28px)}</c> → <c>.reveal.in{transform:none}</c>)其初始狀態
/// 本身就是位移的——凍結後就永遠停在動畫第 0 格。造成 flaky(首屏邊緣的元素看 callback 賽跑)
/// 與系統性量錯(首屏以下永遠不觸發)。
///
/// **走過兩次彎路,記錄下來免得再踩**:
///   - 0.11.0「快速捲過整頁再捲回」不夠:IntersectionObserver 回報的是**callback 送達當下**的
///     相交狀態,不是「當時捲到那裡」。捲太快,送達時元素已離開畫面 → 回報未相交 → class 永不加上。
///   - 接著加的「每站停留到版面穩定」仍不夠:**元素還沒被觸發時版面本來就是靜止的**,
///     「穩定」的條件在 callback 送達前就已成立,等待形同虛設。實測仍有約三分之一的擷取是壞的。
///
/// 現在不靠等待,改用**幾何**保證:由 C# 端把視窗高度暫時撐到整頁高(見
/// <c>WebImplementationSource</c>),所有元素同時落在畫面內,IntersectionObserver **必然**全部觸發
/// ——不需要賭送達時機。撐高期間跑 <see cref="Trigger"/> 等版面吃下這些變化,
/// 然後還原視窗高、跑 <see cref="Settle"/> 做最後定案。class 一旦加上就不會被移除,
/// 所以還原視窗後元素仍在展開後的位置。
///
/// 已知限制:與凍結樣式相同,搆不到 closed shadow root;需要真實使用者手勢的效果不會被觸發。
/// </summary>
internal static class SettleScript
{
    /// <summary>整頁高度(CSS px)。C# 端據此決定要把視窗撐多高。</summary>
    public const string DocumentHeight = """
        () => Math.max(
          document.documentElement.scrollHeight,
          document.body ? document.body.scrollHeight : 0)
        """;

    /// <summary>共用:版面簽章 + 等到連續數次相同。取整是刻意的——次像素抖動不該讓等待永遠不結束。</summary>
    private const string Common = """
        const twoFrames = () => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
        const signature = () => {
          let h = 0;
          for (const el of document.querySelectorAll('*')) {
            const r = el.getBoundingClientRect();
            h = (h * 31 + (r.top | 0) + (r.left | 0) * 7 + (r.width | 0) * 13 + (r.height | 0) * 17) | 0;
          }
          return h + ':' + document.documentElement.scrollHeight;
        };
        // minRounds 是關鍵:元素還沒被觸發時版面本來就是靜止的,
        // 只看「穩定」會在 callback 送達前就結束等待(這正是先前兩版的錯)。
        // 所以先無條件花掉 minRounds,再開始要求穩定。
        const wait = async (minRounds, needStable, maxRounds) => {
          let prev = signature(), stable = 0, rounds = 0, changed = false;
          while (rounds < maxRounds) {
            await twoFrames();
            rounds++;
            const now = signature();
            if (now !== prev) { changed = true; prev = now; stable = 0; }
            else if (rounds >= minRounds) { stable++; }
            if (rounds >= minRounds && stable >= needStable) break;
          }
          return { changed, rounds };
        };
        """;

    /// <summary>視窗撐高後跑:讓所有 IntersectionObserver 觸發並讓版面吃下變化。</summary>
    public const string Trigger = $$"""
        async () => {
          {{Common}}
          // 撐高後所有元素都在畫面內,IO 必然觸發。無條件等 6 輪讓 callback 送達,
          // 再要求連續 3 次穩定;上限 60 輪防呆。
          const a = await wait(6, 3, 60);
          // 再確認一次:這一輪不該再有變化。有的話代表還有連鎖(例如展開後又觸發新的)。
          const b = await wait(2, 3, 30);
          return { triggered: a.changed, settledAfter: !b.changed, rounds: a.rounds + b.rounds };
        }
        """;

    /// <summary>視窗還原後跑:最後定案,順便涵蓋字型換置等其他「晚一步」的版面變動。</summary>
    public const string Settle = $$"""
        async () => {
          {{Common}}
          const r = await wait(3, 3, 60);
          return { changed: r.changed, rounds: r.rounds };
        }
        """;
}
