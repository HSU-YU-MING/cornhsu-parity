namespace Parity.Engine.ImplementationSources.Web;

/// <summary>
/// 注入頁面的擷取腳本:走訪 DOM 樹,吐出 RenderedNode 形狀的 JSON。
/// 重點(規畫書 4.5/4.6):
///   - getBoundingClientRect 相對視窗 → 加 scroll 位移變「相對頁面 root」
///   - getComputedStyle 拿實際樣式;背景 transparent 時沿祖先鏈找有效背景(避免顏色誤報)
///   - data-parity 屬性與 map 檔 selector 都標成 explicitMatch(手動錨點)
///   - 走「組合樹」:shadow host 走 shadowRoot(open)、&lt;slot&gt; 走 assignedElements、
///     同源 iframe(含 srcdoc)走進去並把座標平移回外層頁面座標系——
///     web components 網站的內容不再整塊看不見。closed shadow root 與跨域 iframe 拿不到,誠實跳過。
///     限制:map 檔的 selector 用 document.querySelector 解析,搆不到 shadow/iframe 內
///     (data-parity 屬性不受限,照常可用);shadow/iframe 內的 selector 以「host &gt;&gt;&gt; 內部路徑」表示,僅供人讀與 UI 顯示。
/// </summary>
internal static class CaptureScript
{
    public const string Js = """
        (input) => {
          const { mapSelectors, ignoreSelectors } = input || {};

          // map 檔:在頁面內解析 selector,把命中的元素標上圖層名(light DOM 限定)
          const mapped = new Map();
          for (const [name, sel] of Object.entries(mapSelectors || {})) {
            try { const el = document.querySelector(sel); if (el) mapped.set(el, name); } catch {}
          }

          const ignored = new Set();
          for (const sel of (ignoreSelectors || [])) {
            try { document.querySelectorAll(sel).forEach(el => ignored.add(el)); } catch {}
          }

          // el 在自己的 root(body / shadowRoot / iframe body)內的路徑;prefix 帶上外層脈絡
          const pathIn = (el, stopNode, prefix, rootLabel) => {
            if (el === stopNode) return prefix + rootLabel;
            if (el.id) return prefix + '#' + CSS.escape(el.id);
            const parts = [];
            let cur = el;
            while (cur && cur.nodeType === 1 && cur !== stopNode) {
              let idx = 1, sib = cur;
              while ((sib = sib.previousElementSibling)) if (sib.tagName === cur.tagName) idx++;
              parts.unshift(cur.tagName.toLowerCase() + ':nth-of-type(' + idx + ')');
              const parent = cur.parentElement;
              if (parent && parent !== stopNode && parent.id) {
                parts.unshift('#' + CSS.escape(parent.id));
                return prefix + parts.join(' > ');
              }
              cur = parent; // shadow 頂端 parentElement 為 null → 自然離開迴圈
            }
            if (rootLabel) parts.unshift(rootLabel);
            return prefix + parts.join(' > ');
          };

          const effectiveBg = (el) => {
            let cur = el;
            while (cur && cur.nodeType === 1) {
              const bg = getComputedStyle(cur).backgroundColor;
              if (bg && bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent') return bg;
              // 穿過 shadow 邊界往 host 找(組合樹的視覺祖先)
              cur = cur.parentElement ?? (cur.parentNode && cur.parentNode.host ? cur.parentNode.host : null);
            }
            return null;
          };

          // 從 computed transform 取縮放係數(matrix / matrix3d);純 translate、rotate → [1,1]。
          const scaleOf = (t) => {
            if (!t || t === 'none') return [1, 1];
            let m = t.match(/^matrix\(([^)]+)\)/);
            if (m) { const p = m[1].split(',').map(Number); return [Math.hypot(p[0], p[1]) || 1, Math.hypot(p[2], p[3]) || 1]; }
            m = t.match(/^matrix3d\(([^)]+)\)/);
            if (m) { const p = m[1].split(',').map(Number); return [Math.hypot(p[0], p[1], p[2]) || 1, Math.hypot(p[4], p[5], p[6]) || 1]; }
            return [1, 1];
          };

          const SKIP = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'META', 'LINK']);

          // ctx = { ox, oy(座標平移), stop(路徑的 root), prefix, rootLabel }
          const walk = (el, ctx) => {
            if (SKIP.has(el.tagName) || ignored.has(el)) return null;
            const win = el.ownerDocument.defaultView;
            const s = win.getComputedStyle(el);
            if (s.display === 'none' || s.visibility === 'hidden') return null;
            const r = el.getBoundingClientRect();
            const selfPath = pathIn(el, ctx.stop, ctx.prefix, ctx.rootLabel);

            // 祖先累積的 transform 縮放:getBoundingClientRect 會被祖先 scale() 污染,除回去
            // 讓幾何回到版面座標系(padding 等 computed style 本就不受 transform 影響,不必動)。
            // Parity 比相對位置+尺寸,絕對位置差一個常數不影響;無 transform 時 csx=csy=1,
            // 輸出與未修改前逐位元相同(零回歸)。esx/esy = el 自身的縮放,往下傳給子節點累積。
            const csx = ctx.csx || 1, csy = ctx.csy || 1;
            const [esx, esy] = scaleOf(s.transform);

            // 組合樹的子節點(kidCtx 必須是區域變數:遞迴共用會互相污染脈絡)
            let kids, ctxForKids;
            if (el.shadowRoot) {
              // host → 走 shadowRoot(slot 會把 light 子節點帶回來,不會重複)
              kids = [...el.shadowRoot.children];
              ctxForKids = { ...ctx, stop: el.shadowRoot, prefix: selfPath + ' >>> ', rootLabel: '', csx: csx * esx, csy: csy * esy };
            } else if (el.tagName === 'SLOT') {
              const assigned = el.assignedElements();
              kids = assigned.length ? assigned : [...el.children];
              ctxForKids = ctx; // 塞進來的是外層 light DOM 的元素,脈絡沿用
            } else {
              kids = [...el.children];
              // 自身有縮放才開新脈絡累積,否則沿用(避免每層都複製物件)
              ctxForKids = (esx === 1 && esy === 1) ? ctx : { ...ctx, csx: csx * esx, csy: csy * esy };
            }

            const children = [];
            for (const c of kids) { const n = walk(c, ctxForKids); if (n) children.push(n); }

            // 同源 iframe(含 srcdoc):走進去,座標平移回外層頁面座標系;跨域拿不到 → 誠實跳過
            if (el.tagName === 'IFRAME') {
              try {
                const doc = el.contentDocument;
                if (doc && doc.body) {
                  const iox = r.x + ctx.ox + el.clientLeft - (el.contentWindow.scrollX || 0);
                  const ioy = r.y + ctx.oy + el.clientTop - (el.contentWindow.scrollY || 0);
                  const inner = walk(doc.body, {
                    ox: iox, oy: ioy,
                    stop: doc.body, prefix: selfPath + ' >>> ', rootLabel: 'body',
                  });
                  if (inner) children.push(inner);
                }
              } catch { /* 跨域 */ }
            }

            let ownText = '';
            for (const n of el.childNodes) if (n.nodeType === 3) ownText += n.textContent;
            ownText = ownText.replace(/\s+/g, ' ').trim();

            return {
              selector: selfPath,
              tag: el.tagName.toLowerCase(),
              text: ownText || null,
              domId: el.id || null,
              classes: (typeof el.className === 'string' && el.className) ? el.className : null,
              ariaLabel: el.getAttribute('aria-label') || el.getAttribute('data-testid') || null,
              box: { x: r.x / csx + ctx.ox, y: r.y / csy + ctx.oy, w: r.width / (csx * esx), h: r.height / (csy * esy) },
              color: s.color,
              background: s.backgroundColor,
              effectiveBackground: effectiveBg(el),
              fontFamily: s.fontFamily, fontSize: s.fontSize, fontWeight: s.fontWeight,
              lineHeight: s.lineHeight, letterSpacing: s.letterSpacing,
              paddingTop: s.paddingTop, paddingRight: s.paddingRight,
              paddingBottom: s.paddingBottom, paddingLeft: s.paddingLeft,
              cornerRadius: s.borderTopLeftRadius,
              explicitMatch: el.getAttribute('data-parity') || mapped.get(el) || null,
              children
            };
          };

          // 回傳 JSON 字串(而非物件):Playwright 的 JS→C# 值序列化外殼會把每層巢狀放大成
          // 數層協定 JSON,~16 層以上的真實 DOM 就會超過 System.Text.Json 預設 MaxDepth(64)而 crash。
          // 字串在協定裡只有一層,C# 端再自行用放寬的 MaxDepth 解析。
          return JSON.stringify(walk(document.body,
            { ox: scrollX, oy: scrollY, stop: document.body, prefix: '', rootLabel: 'body', csx: 1, csy: 1 }));
        }
        """;
}
