/* Parity 本機報告 UI(M3)
   - 疊框以截圖原始像素為基準,用百分比定位 → 視窗縮放不跑位
   - 檢視模式:清單 ↔ 疊框互相選取
   - 配對模式:選未配對圖層 → 點截圖元素(hit-test 實作樹)→ POST /api/map */

const $ = (sel) => document.querySelector(sel);

const state = {
  data: null,        // /api/state 回應
  cur: 0,            // 目前 target index
  mode: 'inspect',   // inspect | map
  selected: null,    // designId
  mapLayer: null,    // 配對模式:選中的未配對圖層
  candidate: null,   // 配對模式:hit-test 出的候選 { selector }
  detail: null,      // /api/detail/{cur}(lazy,配對用)
  natural: { w: 1, h: 1 },
};

const sevRank = { critical: 4, serious: 3, medium: 2, minor: 1, none: 0 };
const matchedByLabel = { selector: '快照', explicit: '手動', 'auto-text': '文字', 'auto-name': '名稱', 'auto-container': '容器' };

// ---------- 資料 ----------

async function load() {
  const res = await fetch('/api/state');
  state.data = await res.json();
  if (state.cur >= state.data.targets.length) state.cur = 0;
  state.detail = null;
  render();
}

function target() { return state.data.targets[state.cur]; }

async function loadDetail() {
  if (state.detail) return state.detail;
  const res = await fetch(`/api/detail/${state.cur}`);
  state.detail = await res.json();
  return state.detail;
}

// ---------- 畫面 ----------

function render() {
  const d = state.data;
  const badge = $('#gate-badge');
  badge.textContent = d.gateFail ? '✘ GATE FAIL' : '✔ PASS';
  badge.className = 'badge ' + (d.gateFail ? 'fail' : 'pass');
  // 不過的原因(例如 0 配對——畫面上沒半條落差,卻 FAIL)滑鼠移上去要看得到
  badge.title = (d.gateReasons || []).join('\n');
  $('#score').textContent = `還原度 ${d.score}/100`;
  $('#generated-at').textContent = new Date(d.generatedAt).toLocaleTimeString();
  $('#watch-dot').hidden = !d.watch;

  const tabs = $('#target-tabs');
  tabs.innerHTML = '';
  d.targets.forEach((t, i) => {
    const b = document.createElement('button');
    b.textContent = t.route;
    b.className = i === state.cur ? 'active' : '';
    b.onclick = () => { state.cur = i; state.selected = null; state.detail = null; render(); };
    tabs.appendChild(b);
  });

  renderSidebar();
  renderStage();
  renderMapBar();
}

function renderSidebar() {
  const t = target();
  const side = $('#sidebar');
  side.innerHTML = '';

  if (state.mode === 'map') {
    side.insertAdjacentHTML('beforeend', `
      <div class="map-hint">
        <b>配對模式</b><br>
        1. 點選下方未配對的設計圖層<br>
        2. 在右邊截圖上點對應的元素<br>
        3. 確認後寫入 parity.map.json 並自動重掃
      </div>
      <div class="section-title">未配對(${t.unmatched.length})</div>`);
    if (t.unmatched.length === 0)
      side.insertAdjacentHTML('beforeend', '<div class="empty">🎉 沒有未配對的節點</div>');
    for (const u of t.unmatched) {
      const el = document.createElement('div');
      el.className = 'unmatched-item' + (state.mapLayer === u.designLayer ? ' selected' : '');
      el.innerHTML = `<span class="sev critical"></span><span class="name"></span><span class="reason">${u.reason}</span>`;
      el.querySelector('.name').textContent = u.designLayer;
      el.onclick = () => { state.mapLayer = u.designLayer; state.candidate = null; render(); };
      side.appendChild(el);
    }
    return;
  }

  const withDiffs = t.nodes.filter(n => n.diffs.length > 0)
    .sort((a, b) => sevRank[b.severity] - sevRank[a.severity]);
  const clean = t.nodes.filter(n => n.diffs.length === 0);

  const s = t.summary;
  side.insertAdjacentHTML('beforeend', `
    <div class="section-title">摘要</div>
    <div class="node" style="padding:8px 10px">
      已配對 ${s.matched}/${s.designNodes} 個設計節點,${s.nodesWithDiffs} 個有落差<br>
      <span class="muted">落差數:critical ${s.critical}、serious ${s.serious}、medium ${s.medium}、minor ${s.minor}</span>
    </div>`);

  side.insertAdjacentHTML('beforeend', `<div class="section-title">有落差(${withDiffs.length})</div>`);
  if (withDiffs.length === 0)
    side.insertAdjacentHTML('beforeend', '<div class="empty">🎉 沒有落差</div>');
  for (const n of withDiffs) side.appendChild(nodeItem(n, true));

  if (t.unmatched.length > 0) {
    side.insertAdjacentHTML('beforeend', `<div class="section-title">未配對(${t.unmatched.length})</div>`);
    for (const u of t.unmatched) {
      const el = document.createElement('div');
      el.className = 'unmatched-item' + (state.selected === u.designId ? ' selected' : '');
      el.dataset.id = u.designId;
      el.innerHTML = `<span class="sev critical"></span><span class="name"></span><span class="reason">${u.reason}</span>`;
      el.querySelector('.name').textContent = u.designLayer;
      const fig = figmaLink(u.designId);
      if (fig) el.querySelector('.name').after(fig);
      el.onclick = () => select(u.designId);
      side.appendChild(el);
    }
  }

  if (clean.length > 0) {
    side.insertAdjacentHTML('beforeend', `<div class="section-title">一致(${clean.length})</div>`);
    for (const n of clean) side.appendChild(nodeItem(n, false));
  }
}

function nodeItem(n, open) {
  const det = document.createElement('details');
  det.className = 'node' + (state.selected === n.designId ? ' selected' : '');
  det.open = open && state.selected === n.designId;
  det.dataset.id = n.designId;

  const sum = document.createElement('summary');
  sum.innerHTML = `<span class="sev ${n.severity}"></span><span class="name"></span>
    <span class="how">${matchedByLabel[n.matchedBy] ?? n.matchedBy}</span><span class="sel"></span>`;
  sum.querySelector('.name').textContent = n.designLayer;
  sum.querySelector('.sel').textContent = n.selector;
  const fig = figmaLink(n.designId);
  if (fig) sum.querySelector('.name').after(fig);
  sum.addEventListener('click', () => select(n.designId, false));
  det.appendChild(sum);

  if (n.diffs.length > 0) {
    const table = document.createElement('table');
    table.className = 'diff-table';
    for (const diff of n.diffs) {
      const isColor = diff.prop === 'color' || diff.prop === 'background';
      const row = table.insertRow();
      row.insertCell().textContent = diff.prop + (diff.soft ? ' ⁽soft⁾' : '');
      const exp = row.insertCell(); exp.className = 'exp';
      const act = row.insertCell(); act.className = 'act';
      if (isColor) {
        exp.innerHTML = `<i class="swatch" style="background:${diff.expected}"></i>${diff.expected}`;
        act.innerHTML = `<i class="swatch" style="background:${diff.actual}"></i>${diff.actual}` +
          (diff.delta != null ? ` <span class="soft-note">ΔE ${diff.delta}</span>` : '');
      } else {
        exp.textContent = `${diff.expected}${diff.unit ?? ''}`;
        act.textContent = `${diff.actual}${diff.unit ?? ''}` +
          (diff.delta != null ? `(差 ${diff.delta})` : '');
      }
    }
    det.appendChild(table);
  }
  return det;
}

// 設計來源是 Figma 時,給「跳回那個圖層」的小連結(設計師的入口)
function figmaLink(designId) {
  if (!state.data.figmaFileKey || !designId) return null;
  const a = document.createElement('a');
  a.className = 'fig';
  a.textContent = '↗';
  a.title = '在 Figma 開啟這個圖層';
  a.target = '_blank';
  a.rel = 'noopener';
  a.href = `https://www.figma.com/design/${encodeURIComponent(state.data.figmaFileKey)}?node-id=${encodeURIComponent(designId.replaceAll(':', '-'))}`;
  a.onclick = (e) => e.stopPropagation();
  return a;
}

function renderStage() {
  const t = target();
  const img = $('#shot');
  const stage = $('#stage');

  // 截圖包一層 .frame 讓 rects 跟圖同寬高
  if (!img.parentElement.classList.contains('frame')) {
    const frame = document.createElement('div');
    frame.className = 'frame';
    stage.insertBefore(frame, img);
    frame.appendChild(img);
    frame.appendChild($('#rects'));
  }

  const src = t.screenshot ? `${t.screenshot}?v=${encodeURIComponent(state.data.generatedAt)}` : '';
  if (img.dataset.src !== src) {
    img.dataset.src = src;
    img.onload = () => {
      state.natural = { w: img.naturalWidth, h: img.naturalHeight };
      drawRects();
    };
    img.src = src;
  } else {
    drawRects();
  }
}

function drawRects() {
  const t = target();
  const rects = $('#rects');
  rects.innerHTML = '';
  const { w: NW, h: NH } = state.natural;
  const showDesign = $('#show-design').checked;
  const showRendered = $('#show-rendered').checked;
  const onlyDiffs = $('#only-diffs').checked;

  const place = (el, box, extra = { x: 0, y: 0 }) => {
    el.style.left = ((extra.x + box.x) / NW * 100) + '%';
    el.style.top = ((extra.y + box.y) / NH * 100) + '%';
    el.style.width = (box.w / NW * 100) + '%';
    el.style.height = (box.h / NH * 100) + '%';
  };

  // 配對模式下,已配對的框不攔截點擊——點擊要穿透到截圖做 hit-test
  const passthrough = state.mode === 'map';

  for (const n of t.nodes) {
    if (onlyDiffs && n.diffs.length === 0) continue;
    if (showRendered) {
      const el = document.createElement('div');
      el.className = `rect rendered ${n.severity}` + (state.selected === n.designId ? ' selected' : '');
      place(el, n.renderedBox, t.origin);
      el.innerHTML = `<span class="tag"></span>`;
      el.querySelector('.tag').textContent = `${n.designLayer} ‹${n.selector}›`;
      if (passthrough) el.style.pointerEvents = 'none';
      else el.onclick = (e) => { e.stopPropagation(); select(n.designId); };
      rects.appendChild(el);
    }
    if (showDesign) {
      const el = document.createElement('div');
      el.className = 'rect design' + (state.selected === n.designId ? ' selected' : '');
      place(el, n.designBox, t.origin);
      if (passthrough) el.style.pointerEvents = 'none';
      else el.onclick = (e) => { e.stopPropagation(); select(n.designId); };
      rects.appendChild(el);
    }
  }

  // 未配對:設計端虛線紅框(檢視與配對模式都畫)
  for (const u of t.unmatched) {
    const el = document.createElement('div');
    const isSel = state.mode === 'map' ? state.mapLayer === u.designLayer : state.selected === u.designId;
    el.className = 'rect unmatched' + (isSel ? ' selected' : '');
    place(el, u.designBox, t.origin);
    el.innerHTML = `<span class="tag"></span>`;
    el.querySelector('.tag').textContent = `${u.designLayer}(未配對)`;
    if (state.mode === 'map' && state.mapLayer === u.designLayer) {
      el.style.pointerEvents = 'none'; // 已選中:點擊穿透到截圖 hit-test(框正好蓋在目標元素上)
    } else {
      el.onclick = (e) => {
        e.stopPropagation();
        if (state.mode === 'map') { state.mapLayer = u.designLayer; state.candidate = null; render(); }
        else select(u.designId);
      };
    }
    rects.appendChild(el);
  }

  // 配對模式:候選元素高亮(不攔截點擊,可re-hit-test)
  if (state.mode === 'map' && state.candidate) {
    const el = document.createElement('div');
    el.className = 'rect rendered selected';
    el.style.pointerEvents = 'none';
    place(el, state.candidate.box, t.origin);
    rects.appendChild(el);
  }
}

function select(designId, rerender = true) {
  state.selected = designId;
  // 右側疊框一定要重畫,套用 .selected——即使清單那邊不整個 rerender
  if (rerender) {
    render();
  } else {
    drawRects();
    // 左側清單同步高亮(不重建 DOM——重建或改 open 都會打斷 <summary> 的原生開合:
    // listener 裡設 open=true 會被緊接著的預設 toggle 動作翻回去,落差表就永遠展不開)
    for (const el of document.querySelectorAll('#sidebar .node, #sidebar .unmatched-item'))
      el.classList.toggle('selected', el.dataset.id === designId);
  }

  // 關鍵:把右側對應的框捲到中央並閃一下,讓「左邊點的 → 右邊在這」一目了然
  requestAnimationFrame(() => {
    const rect = $('#rects .rect.rendered.selected')
      || $('#rects .rect.unmatched.selected')
      || $('#rects .rect.selected');
    if (rect) {
      rect.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
      rect.classList.remove('flash');
      void rect.offsetWidth;      // 重觸發動畫
      rect.classList.add('flash');
    }
  });
}

// ---------- 配對模式 ----------

function renderMapBar() {
  const bar = $('#map-bar');
  if (state.mode !== 'map') { bar.hidden = true; return; }
  bar.hidden = false;

  if (!state.mapLayer) {
    bar.innerHTML = '從左邊選一個未配對的設計圖層開始。';
    return;
  }
  if (!state.candidate) {
    bar.innerHTML = '';
    const strong = document.createElement('b');
    strong.textContent = state.mapLayer;
    bar.append(strong, ' — 在截圖上點選對應的元素…');
    return;
  }
  bar.innerHTML = '';
  const strong = document.createElement('b');
  strong.textContent = state.mapLayer;
  const code = document.createElement('code');
  code.textContent = state.candidate.selector;
  const spacer = document.createElement('span'); spacer.className = 'spacer';
  const ok = document.createElement('button'); ok.className = 'primary'; ok.textContent = '確認配對';
  ok.onclick = confirmMapping;
  const cancel = document.createElement('button'); cancel.className = 'ghost'; cancel.textContent = '取消';
  cancel.onclick = () => { state.candidate = null; render(); };
  bar.append(strong, ' → ', code, spacer, ok, cancel);
}

$('#stage').addEventListener('click', async (e) => {
  if (state.mode !== 'map' || !state.mapLayer) return;
  const img = $('#shot');
  const r = img.getBoundingClientRect();
  if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) return;

  // 點擊 → 截圖原始像素座標 → 相對 body 座標 → hit-test 實作樹
  const t = target();
  const px = (e.clientX - r.left) / r.width * state.natural.w - t.origin.x;
  const py = (e.clientY - r.top) / r.height * state.natural.h - t.origin.y;

  const detail = await loadDetail();
  const hit = hitTest(detail.renderedTree, px, py);
  if (hit) {
    state.candidate = { selector: hit.selector, box: hit.box };
    render();
  }
});

function hitTest(node, x, y) {
  // 深度優先:回傳包含點擊點、面積最小的節點
  let best = null;
  const visit = (n) => {
    const b = n.box;
    if (x >= b.x && x <= b.x + b.w && y >= b.y && y <= b.y + b.h && b.w > 0 && b.h > 0) {
      if (!best || b.w * b.h <= best.box.w * best.box.h) best = n;
    }
    for (const c of n.children ?? []) visit(c);
  };
  visit(node);
  return best;
}

async function confirmMapping() {
  const res = await fetch('/api/map', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ layer: state.mapLayer, selector: state.candidate.selector }),
  });
  if (res.ok) {
    toast(`已配對 ${state.mapLayer} → ${state.candidate.selector}`);
    state.mapLayer = null;
    state.candidate = null;
    // SSE 會推 reload;這裡不主動重抓,避免 double render
  } else {
    toast('配對失敗:' + (await res.text()));
  }
}

function toast(msg) {
  const el = document.createElement('div');
  el.className = 'toast';
  el.textContent = msg;
  document.body.appendChild(el);
  setTimeout(() => el.remove(), 3500);
}

// ---------- 事件 ----------

$('#rerun').onclick = async () => {
  $('#rerun').disabled = true;
  try { await fetch('/api/rerun', { method: 'POST' }); }
  finally { $('#rerun').disabled = false; }
};

$('#mode-inspect').onclick = () => setMode('inspect');
$('#mode-map').onclick = () => setMode('map');
function setMode(mode) {
  state.mode = mode;
  state.candidate = null;
  $('#mode-inspect').classList.toggle('active', mode === 'inspect');
  $('#mode-map').classList.toggle('active', mode === 'map');
  render();
}

for (const id of ['show-design', 'show-rendered', 'only-diffs'])
  $('#' + id).addEventListener('change', drawRects);

// SSE:掃描完成自動更新
new EventSource('/api/events').onmessage = () => load();

// 啟動;parity map 開的 #map 直接進配對模式
load().then(() => { if (location.hash === '#map') setMode('map'); });
