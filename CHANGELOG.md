# Changelog

版本規則:0.x 期間,新功能升 minor(0.1→0.2),修正升 patch。

## 未發布

- **Action 消費者流程實證通過**:外部 repo(parity-action-test,實證後已轉私人)以 `@v0.2.0` 跑真 PR——main 綠、壞 PR 打紅 + bot 貼還原度報告、追加 commit 後留言原地更新。非程式變更,純驗證紀錄。
- **CI `--baseline` 實證通過**:main 留既有落差 + commit `parity.baseline.db` + `baseline: true` → CI 綠;PR 新增落差 → 精確只擋新增那條,留言帶「相對基準」區塊。順手修 action.yml 裡 baseline 說明的過期路徑(`.parity/baseline.db` → `parity.baseline.db`)。
- **補 gate 盲點:0 配對不再假 PASS**。gate 先驗「配對可信度」:完全 0 配對或設計端 0 節點 → GATE FAIL 並附原因(通常是 url/frame 指錯);`--baseline` 模式也不豁免(殘缺的現況會把 baseline 的一切誤判成「修好」)。另加選配 `gate.minMatchRate`(0–1)配對率門檻。CLI / Markdown 報告 / serve UI(badge tooltip)都會顯示不通過的原因。

## 0.2.0

首版 0.1.0 之後累積了一大批功能與強化。

### 新功能
- **Electron 桌面 app 支援**:target url 用 `cdp:http://host:port`(可加 `#url片段` 指定視窗)attach 進活視窗抓 DOM。
- **回歸把關(baseline)**:`parity baseline save|list` + `parity check --baseline`。以 SQLite(`parity.baseline.db`)存基準,只擋「相對基準新增/惡化」的落差——已有一堆落差的專案也能漸進導入。
- **報告會說話、會幫忙**:
  - 還原度分數(0–100)。
  - `parity check --md <path>` 輸出 Markdown 報告(分數 + 落差表 + 建議修法)。
  - **建議修法**:把每條落差翻成可直接套的 CSS,並可對齊 design token(`tokensFile`)。
  - 落差照「衝擊度」(嚴重度 + 畫面面積)排序。
- **GitHub Action**:CI 還原度把關,並自動在 PR 貼/更新還原度報告留言。
- 現代 CSS 顏色語法(`rgb(37 99 235 / .5)`、`color(srgb …)`);ΔE 納入 alpha(半透明合成到白底)。

### 修正與強化
- 真實網站:修深度巢狀擷取 crash、auto-layout 幾何誤報(讀 Figma layout sizing)、配對脆弱(容器 LCA 推論 + 同文字消歧)。
- baseline 鍵加 selector(避免重複圖層名誤判);baseline 檔預設放 repo 根(可 commit,CI 才吃得到)。
- serve 資安:擋 DNS-rebinding / CSRF(Host + Origin 檢查);靜態檔 `Cache-Control: no-cache`;報告 UI 左選→右側疊框高亮+捲動。
- Action:fork PR 不再誤紅;PR 留言超長截斷。
- GitInfo 讀 stream 的 deadlock 隱患、token 索引跨型別碰撞、還原度分數與 gate 判定一致(純軟落差不扣分)等。
- 測試 49 → 100。

## 0.1.0

- 引擎核心:設計/實作兩棵樹 → 正規化 → 配對 → 數值 diff + 容差 + CIEDE2000 色差。
- CLI:`parity check` / `serve` / `map` / `init` / `install-browser`。
- 本機報告 UI(Kestrel 綁 127.0.0.1)+ 互動配對。
- 網頁實作來源(Playwright);Figma / 本機 JSON 設計來源。
