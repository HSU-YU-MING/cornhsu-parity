namespace Parity.Engine.ImplementationSources.Web;

/// <summary>
/// 注入頁面的擷取腳本:走訪 DOM 樹,吐出 RenderedNode 形狀的 JSON。
/// 重點(規畫書 4.5/4.6):
///   - getBoundingClientRect 相對視窗 → 加 scroll 位移變「相對頁面 root」
///   - getComputedStyle 拿實際樣式;背景 transparent 時沿祖先鏈找有效背景(避免顏色誤報)
///   - data-parity 屬性與 map 檔 selector 都標成 explicitMatch(手動錨點)
/// </summary>
internal static class CaptureScript
{
    public const string Js = """
        (input) => {
          const { mapSelectors, ignoreSelectors } = input || {};

          // map 檔:在頁面內解析 selector,把命中的元素標上圖層名
          const mapped = new Map();
          for (const [name, sel] of Object.entries(mapSelectors || {})) {
            try { const el = document.querySelector(sel); if (el) mapped.set(el, name); } catch {}
          }

          const ignored = new Set();
          for (const sel of (ignoreSelectors || [])) {
            try { document.querySelectorAll(sel).forEach(el => ignored.add(el)); } catch {}
          }

          const cssPath = (el) => {
            if (el === document.body) return 'body';
            if (el.id) return '#' + CSS.escape(el.id);
            const parts = [];
            let cur = el;
            while (cur && cur.nodeType === 1 && cur !== document.body) {
              let idx = 1, sib = cur;
              while ((sib = sib.previousElementSibling)) if (sib.tagName === cur.tagName) idx++;
              parts.unshift(cur.tagName.toLowerCase() + ':nth-of-type(' + idx + ')');
              cur = cur.parentElement;
              if (cur && cur.id) { parts.unshift('#' + CSS.escape(cur.id)); return parts.join(' > '); }
            }
            parts.unshift('body');
            return parts.join(' > ');
          };

          const effectiveBg = (el) => {
            let cur = el;
            while (cur && cur.nodeType === 1) {
              const bg = getComputedStyle(cur).backgroundColor;
              if (bg && bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent') return bg;
              cur = cur.parentElement;
            }
            return null;
          };

          const SKIP = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'META', 'LINK']);

          const walk = (el) => {
            if (SKIP.has(el.tagName) || ignored.has(el)) return null;
            const s = getComputedStyle(el);
            if (s.display === 'none' || s.visibility === 'hidden') return null;
            const r = el.getBoundingClientRect();

            const children = [];
            for (const c of el.children) { const n = walk(c); if (n) children.push(n); }

            let ownText = '';
            for (const n of el.childNodes) if (n.nodeType === 3) ownText += n.textContent;
            ownText = ownText.replace(/\s+/g, ' ').trim();

            return {
              selector: cssPath(el),
              tag: el.tagName.toLowerCase(),
              text: ownText || null,
              domId: el.id || null,
              classes: (typeof el.className === 'string' && el.className) ? el.className : null,
              ariaLabel: el.getAttribute('aria-label') || el.getAttribute('data-testid') || null,
              box: { x: r.x + scrollX, y: r.y + scrollY, w: r.width, h: r.height },
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
          return JSON.stringify(walk(document.body));
        }
        """;
}
