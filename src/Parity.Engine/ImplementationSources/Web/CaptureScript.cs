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
///   - 根節點另外帶 pageText(整頁文字,只給 Matcher 判斷 text-not-found;見 RenderedNode.PageText)
/// </summary>
internal static class CaptureScript
{
    public const string Js = """
        (input) => {
          const { mapSelectors, ignoreSelectors, allowedIdAnchors } = input || {};

          // 穩定 id 白名單(snapshot 連拍實測的產物):有名單時,名單外的 id 不當錨點。
          // 純字母隨機 id(rkxvdnnzty)looksRandom 的字元啟發法認不出來,只有實測認得出;
          // null = 沒有名單 = 現行行為(只靠 looksRandom)。
          const allowedIds = Array.isArray(allowedIdAnchors) ? new Set(allowedIdAnchors) : null;
          const idAnchorOk = (id) => !looksRandom(id) && (!allowedIds || allowedIds.has(id));

          // map 檔:在頁面內解析 selector,把命中的元素標上圖層名(light DOM 限定)
          const mapped = new Map();
          for (const [name, sel] of Object.entries(mapSelectors || {})) {
            try { const el = document.querySelector(sel); if (el) mapped.set(el, name); } catch {}
          }

          const ignored = new Set();
          for (const sel of (ignoreSelectors || [])) {
            try { document.querySelectorAll(sel).forEach(el => ignored.add(el)); } catch {}
          }

          // 隨機 id 偵測(野生實查 F2 根治):React useId / CSS-in-JS 的 id 每次載入重新生成,
          // 拿它當 selector 錨點 = snapshot 凍住的路徑下次載入必失效(MDN 實測 48 個未配對)。
          // 高熵 id 不當錨點、改走結構路徑(nth-of-type),路徑就跨載入穩定。
          // 啟發法與 C# 端 Matcher.ContainsRandomizedToken 同步(兩邊改要一起改):
          // 長度 ≥8 的純字母數字 token,(≥2 位數字 + ≥3 字母)或(≥10 字元 + 內嵌數字)。
          const looksRandom = (id) => {
            for (const token of id.split(/[^A-Za-z0-9]+/)) {
              if (token.length < 8) continue;
              const digits = (token.match(/[0-9]/g) || []).length;
              const letters = token.length - digits;
              if (digits >= 2 && letters >= 3) return true;
              if (digits >= 1 && token.length >= 10 && /^[A-Za-z]/.test(token) && /[A-Za-z]$/.test(token)) return true;
            }
            return false;
          };

          // el 在自己的 root(body / shadowRoot / iframe body)內的路徑;prefix 帶上外層脈絡
          const pathIn = (el, stopNode, prefix, rootLabel) => {
            if (el === stopNode) return prefix + rootLabel;
            if (el.id && idAnchorOk(el.id)) return prefix + '#' + CSS.escape(el.id);
            const parts = [];
            let cur = el;
            while (cur && cur.nodeType === 1 && cur !== stopNode) {
              let idx = 1, sib = cur;
              while ((sib = sib.previousElementSibling)) if (sib.tagName === cur.tagName) idx++;
              parts.unshift(cur.tagName.toLowerCase() + ':nth-of-type(' + idx + ')');
              const parent = cur.parentElement;
              if (parent && parent !== stopNode && parent.id && idAnchorOk(parent.id)) {
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
          // clean:純旋轉的 hypot 可能因浮點算出 0.9999999 而非 1,會對子樹引入無謂次像素
          // 位移、破壞「無縮放零回歸」——貼近 1 就吸附成 1;NaN/0/負/Infinity 一律夾到 1。
          const clean = (v) => (Number.isFinite(v) && v > 0 ? (Math.abs(v - 1) < 1e-9 ? 1 : v) : 1);
          const scaleOf = (t) => {
            if (!t || t === 'none') return [1, 1];
            let m = t.match(/^matrix\(([^)]+)\)/);
            if (m) { const p = m[1].split(',').map(Number); return [clean(Math.hypot(p[0], p[1])), clean(Math.hypot(p[2], p[3]))]; }
            m = t.match(/^matrix3d\(([^)]+)\)/);
            if (m) { const p = m[1].split(',').map(Number); return [clean(Math.hypot(p[0], p[1], p[2])), clean(Math.hypot(p[4], p[5], p[6]))]; }
            return [1, 1];
          };

          const SKIP = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'META', 'LINK']);

          // 連子孫一起的文字(文字內容比對用,見 RenderedNode.TextContent)。子元素太多的是版面容器,不是一段文字;
          // 裡面有 script/style 的話 textContent 會把程式碼算進去,都不給。2000 與 DiffEngine.TextContentLimit 同值
          const fullText = (el) => {
            if (el.childElementCount === 0 || el.childElementCount > 50) return null;
            if (el.querySelector('script,style,template,noscript')) return null;
            return el.textContent.slice(0, 2000);
          };

          // 文字佔幾行、最寬多少:量實際的行框。不用「高度 ÷ 行高」:line-height: normal 算不出來,padding 也會被算進去。
          // 中心點落在上一行底部以下才算新的一行,同一行裡字型大小不同的行內元素不會被算成兩行。
          // 裡面有區塊子元素時,範圍會包到別段的框,不準,不量。
          const onlyInlineChildren = (el, win) => {
            for (const c of el.children) if (!win.getComputedStyle(c).display.startsWith('inline')) return false;
            return true;
          };
          const measureText = (el, win) => {
            if (!onlyInlineChildren(el, win)) return null;
            const range = el.ownerDocument.createRange();
            range.selectNodeContents(el);
            const rects = [...range.getClientRects()].filter(q => q.width > 0 && q.height > 0).sort((a, b) => a.top - b.top);
            if (rects.length === 0) return null;
            let lines = 1, bottom = rects[0].bottom, left = Infinity, right = -Infinity;
            for (const q of rects) {
              if (q.top + q.height / 2 > bottom) { lines++; bottom = q.bottom; } else bottom = Math.max(bottom, q.bottom);
              left = Math.min(left, q.left);
              right = Math.max(right, q.right);
            }
            return { lines, width: right - left };
          };

          // 文字對齊:換算成 left/center/right/justify。文字把容器塞滿時(flex 子元素、行內區塊)對齊沒有視覺意義,不給。
          // 行內元素的「容器」是最近的非行內祖先
          const textAlignOf = (el, s, win, measured) => {
            let a = s.textAlign.replace(/^-(webkit|moz)-/, '');
            if (a === 'start') a = s.direction === 'rtl' ? 'right' : 'left';
            else if (a === 'end') a = s.direction === 'rtl' ? 'left' : 'right';
            if (!['left', 'center', 'right', 'justify'].includes(a) || !measured) return null;
            let box = el, bs = s;
            while (bs.display === 'inline' || bs.display === 'contents') {
              box = box.parentElement;
              if (!box) return null;
              bs = win.getComputedStyle(box);
            }
            const room = box.clientWidth - parseFloat(bs.paddingLeft) - parseFloat(bs.paddingRight);
            return measured.lines === 1 && room - measured.width < 2 ? null : a;
          };

          // 文字裝飾會傳給子孫,但計算值不會顯示出來:<a><span>字</span></a> 的 span 計算值是 none,畫面上有底線。
          // 所以沿祖先收集,直到傳遞中斷的地方(行內區塊、浮動、絕對定位)
          const decorationOf = (el) => {
            const found = new Set();
            for (let cur = el; cur && cur.nodeType === 1; cur = cur.parentElement) {
              const cs = cur.ownerDocument.defaultView.getComputedStyle(cur);
              for (const line of cs.textDecorationLine.split(' '))
                if (line === 'underline' || line === 'line-through') found.add(line);
              if (cs.display.startsWith('inline-') || cs.float !== 'none' || cs.position === 'absolute' || cs.position === 'fixed') break;
            }
            return found.size ? [...found].sort().join(' ') : 'none';
          };

          // 透明的邊框看不見,算沒有邊框:border: 1px solid transparent 很常見,留位置給 hover 或 focus 用
          // (Carbon 的按鈕就是,對真實 Figma 實測抓到的:設計稿沒有邊框,被報成 0 → 1)
          // 只認真的帶 alpha 的寫法:rgba(…, 0) 或 … / 0)。rgb(255, 0, 0) 的結尾也是「, 0)」,那是紅色,不是透明
          const isTransparent = (c) => !c || c === 'transparent' || /^rgba\(.*,\s*0(\.0+)?\s*\)$/.test(c) || /\/\s*0(\.0+)?%?\s*\)$/.test(c);
          const borderWidthOf = (s, side) => isTransparent(s['border' + side + 'Color']) ? '0px' : s['border' + side + 'Width'];

          // 邊框顏色:第一條看得見的邊;沒有邊框不給
          const borderColorOf = (s) => {
            for (const side of ['Top', 'Right', 'Bottom', 'Left'])
              if (parseFloat(borderWidthOf(s, side)) > 0) return s['border' + side + 'Color'];
            return null;
          };

          // 外框(CSS outline,2.5.0):不佔版面,所以以前完全沒比。style 是 none 或顏色透明 → 0,看不見的外框不算
          const outlineWidthOf = (s) => (s.outlineStyle === 'none' || isTransparent(s.outlineColor)) ? '0px' : s.outlineWidth;

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
            } else if (el.tagName.toLowerCase() === 'svg') {
              // SVG 當葉子(野生實查 F3):內部是繪圖指令(path/g/defs),沒有 padding/字體
              // 語意,對設計比對是雜訊;動畫/重繪下 nth-of-type 又不穩,配對必 flaky
              // (tailwindcss.com 實測 44 個 unmatched、兩次配對數不同)。svg 本身照量
              // (圖示的尺寸/位置還是要比),內部不展開。
              kids = [];
              ctxForKids = ctx;
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

            const textContent = fullText(el);
            const hasText = !!(ownText || textContent);
            const measured = ownText ? measureText(el, win) : null;

            return {
              selector: selfPath,
              tag: el.tagName.toLowerCase(),
              text: ownText || null,
              textContent,
              textIsInline: textContent !== null && onlyInlineChildren(el, win),
              lineCount: measured ? measured.lines : null,
              fontStyle: hasText ? (s.fontStyle === 'normal' ? 'normal' : 'italic') : null,
              textDecoration: hasText ? decorationOf(el) : null,
              textTransform: hasText ? (s.fontVariantCaps.includes('small-caps') ? 'small-caps' : s.textTransform) : null,
              textAlign: ownText ? textAlignOf(el, s, win, measured) : null,
              opacity: s.opacity,
              borderTopWidth: borderWidthOf(s, 'Top'), borderRightWidth: borderWidthOf(s, 'Right'),
              borderBottomWidth: borderWidthOf(s, 'Bottom'), borderLeftWidth: borderWidthOf(s, 'Left'),
              borderColor: borderColorOf(s),
              outlineWidth: outlineWidthOf(s),
              outlineColor: parseFloat(outlineWidthOf(s)) > 0 ? s.outlineColor : null,
              boxShadow: s.boxShadow,
              textShadow: hasText ? s.textShadow : null,
              backgroundImage: s.backgroundImage.includes('gradient(') ? s.backgroundImage : null,
              hasBackgroundImage: s.backgroundImage.includes('url('),
              imageBroken: el.tagName === 'IMG' && el.complete && el.naturalWidth === 0,
              domId: el.id || null,
              // 讀 class 屬性,不讀 className:SVG 元素的 className 是 SVGAnimatedString,以前一律收成 null,圖示靠名字永遠配不上(2.5.0)
              classes: el.getAttribute('class') || null,
              ariaLabel: el.getAttribute('aria-label') || el.getAttribute('data-testid') || null,
              box: { x: r.x / csx + ctx.ox, y: r.y / csy + ctx.oy, w: r.width / (csx * esx), h: r.height / (csy * esy) },
              color: s.color,
              background: s.backgroundColor,
              effectiveBackground: effectiveBg(el),
              fontFamily: s.fontFamily, fontSize: s.fontSize, fontWeight: s.fontWeight,
              lineHeight: s.lineHeight, letterSpacing: s.letterSpacing,
              paddingTop: s.paddingTop, paddingRight: s.paddingRight,
              paddingBottom: s.paddingBottom, paddingLeft: s.paddingLeft,
              marginTop: s.marginTop, marginRight: s.marginRight,
              marginBottom: s.marginBottom, marginLeft: s.marginLeft,
              cornerRadius: s.borderTopLeftRadius,
              outOfFlow: s.position === 'absolute' || s.position === 'fixed',
              explicitMatch: el.getAttribute('data-parity') || mapped.get(el) || null,
              children
            };
          };

          // 整頁文字(text-not-found 用):Matcher 只拿它判斷「設計稿的這段文字整頁都找不到」,不進報告。
          // body.innerText 看不到的(Chromium 實測):shadow DOM、同源 iframe、<input>/<textarea> 的值與 placeholder、
          // ::before/::after 的 content、<img alt>——所以另走一遍組合樹(與 walk 同一套:shadowRoot、slot、同源 iframe)補上。
          // 原則是「寧可多收」:多收只會讓 text-not-found 少標(退回 ambiguous-or-missing-text),
          // 少收才會把看得到的文字誤標成找不到、開了 gate 就是誤擋。display:none 的子樹不收、visibility:hidden 的文字不收
          // (畫面上看不到);password 的值不收(畫面上是圓點)。各段以 \u001f 隔開,子字串比對不會橫跨兩段。
          // 跨域 iframe、closed shadow root、canvas 與圖片裡的字讀不到——語意是「在 Parity 看得到的範圍內找不到」。
          const collectPageText = () => {
            const NO_TEXT = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'META', 'LINK', 'TITLE', 'DESC']);
            const cssUnescape = (str) => str.replace(/\\([0-9a-fA-F]{1,6})\s?|\\([\s\S])/g, (_, hex, ch) => {
              if (hex === undefined) return ch === '\n' ? '' : ch;
              const cp = parseInt(hex, 16);
              return cp > 0 && cp <= 0x10FFFF ? String.fromCodePoint(cp) : '\uFFFD';
            });
            // computed content:Chromium 會把 attr() 解成字串,counter() 原樣留著(解不出來,只取引號內的字)
            const pseudo = (win, el, which) => {
              const c = win.getComputedStyle(el, which).content;
              if (!c || c === 'none' || c === 'normal') return '';
              let out = '';
              for (const m of c.matchAll(/"((?:[^"\\]|\\[\s\S])*)"|'((?:[^'\\]|\\[\s\S])*)'/g)) out += cssUnescape(m[1] ?? m[2]);
              return out;
            };
            const docs = new Set([document]);
            let flat = '';
            const visit = (node, shown) => {
              if (node.nodeType === 3) { if (shown) flat += node.data; return; }
              if (node.nodeType !== 1 || NO_TEXT.has(node.tagName.toUpperCase())) return; // SVG 的 title/style 是小寫
              const el = node, win = el.ownerDocument.defaultView;
              const s = win.getComputedStyle(el);
              if (s.display === 'none') return;
              const vis = s.visibility === 'visible';
              // 區塊元素前後補換行(近似 innerText 的分段);行內元素不補,Hello<b>world</b> 才不會被拆成兩個字
              const block = !s.display.startsWith('inline') && s.display !== 'contents';
              if (block) flat += '\n';
              if (vis) flat += pseudo(win, el, '::before');
              const tag = el.tagName.toUpperCase();
              if (tag === 'BR') flat += '\n';
              else if (tag === 'INPUT') {
                const type = (el.type || '').toLowerCase();
                // 沒寫 value 的 submit/reset 顯示瀏覽器的預設字(英文介面是 Submit/Reset)
                if (vis && type !== 'password' && type !== 'hidden')
                  flat += ' ' + (el.value || (type === 'submit' ? 'Submit' : type === 'reset' ? 'Reset' : '')) +
                    ' ' + (el.placeholder || '') + ' ' + (type === 'image' ? (el.alt || '') : '') + ' ';
              } else if (tag === 'TEXTAREA') {
                if (vis) flat += ' ' + el.value + ' ' + (el.placeholder || '') + ' ';
              } else if (tag === 'IMG' || tag === 'AREA') {
                if (vis && el.alt) flat += ' ' + el.alt + ' ';
              } else if (tag === 'IFRAME' || tag === 'OBJECT') {
                try {
                  const d = el.contentDocument;
                  if (d && d.body && !docs.has(d)) { docs.add(d); flat += '\n'; visit(d.body, true); flat += '\n'; }
                } catch { /* 跨域 */ }
              } else {
                const kids = el.shadowRoot ? el.shadowRoot.childNodes
                  : tag === 'SLOT' ? (el.assignedNodes().length ? el.assignedNodes() : el.childNodes)
                  : el.childNodes;
                for (const k of kids) visit(k, vis);
              }
              if (vis) flat += pseudo(win, el, '::after');
              if (block) flat += '\n';
            };
            visit(document.body, true);
            // innerText 另外留一份:它套用 text-transform、分段也最準;走訪那份補它看不到的
            const parts = [];
            for (const d of docs) { try { if (d.body) parts.push(d.body.innerText); } catch { } }
            parts.push(flat);
            return parts.join('\u001f');
          };

          // 回傳 JSON 字串(而非物件):Playwright 的 JS→C# 值序列化外殼會把每層巢狀放大成
          // 數層協定 JSON,~16 層以上的真實 DOM 就會超過 System.Text.Json 預設 MaxDepth(64)而 crash。
          // 字串在協定裡只有一層,C# 端再自行用放寬的 MaxDepth 解析。
          const root = walk(document.body,
            { ox: scrollX, oy: scrollY, stop: document.body, prefix: '', rootLabel: 'body', csx: 1, csy: 1 });
          if (root) root.pageText = collectPageText();
          return JSON.stringify(root);
        }
        """;
}
