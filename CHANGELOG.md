# Changelog

版本規則:0.x 期間,新功能升 minor(0.1→0.2),修正升 patch。

## 0.9.3

Dogfooding 回報的 CLI 安全性修正——「查詢動作」絕不能變成「破壞性寫入」:

- **子指令不再靜默忽略未知參數**:未知旗標、多餘的位置參數、缺值,一律報錯 exit 2。之前 `--taget`(typo)會被吞掉,「只重拍一頁」靜默變成「全站重拍」;現在 typo 直接被擋下。
- **每個子指令都認得 `--help` / `-h`**:印該指令用法後 exit 0。之前 `parity snapshot --help` 會**直接執行 snapshot 把基準無聲覆寫**——查詢變破壞性寫入,最要命的一類事故;現在查詢就只是查詢。用法文字重構為單一來源(主 help 與子指令 --help 共用,不漂移)。
- **snapshot 覆寫前自動備份**到 `.parity/snapshot.bak.json`——站台壞掉時誤拍基準可救回。刻意不用 `--force`:重拍基準是日常動作,摩擦要加在事故上,不是加在正當流程上。
- `parity baseline` 未知子指令改為報錯 exit 2(原本印說明卻回 0)。

## 0.9.2

- **修 NuGet 包只含 linux-x64 Playwright driver**:release 在 ubuntu runner 上 `dotnet pack`,Microsoft.Playwright 預設只複製「建置當下平台」的 driver,做出來的包缺 `win32_x64/node.exe` 與 darwin——Windows/macOS 使用者裝了任何指令都報 `Driver not found`,連 `install-browser` 都失敗。修法:Parity.Cli 設 `PlaywrightPlatform=all` 收齊五個平台的 node(工具是給各平台 CI 用的,不挑平台;包約 237MB,低於 nuget.org 250MB 上限)。已本機 pack 實測:`dotnet tool install` 後 `install-browser` 與指令皆正常。

## 0.9.1

Dogfooding 首日(cornhsu.com 作品集,16 頁、近 4,000 節點)逼出的兩個修正:

- **量測時凍結動畫/轉場**:進場 `transition` 與 `infinite` 動畫會讓同一頁兩次擷取量到不同座標(flaky 誤報)。現在擷取前注入 `animation/transition: none`,量完移除(attach 模式不在使用者的活 app 留痕跡)。量的是設計意圖的版面,不是動畫中途格。已知限制:凍結樣式不 cascade 進 shadow DOM。
- **snapshot 視窗自我參照修正**:快照 frame 原本記 body 尺寸,check 用它開視窗 → 捲軸再吃 16px、`100vh` 變成整頁高,必然落差。改記「拍照當時的視窗尺寸」,check 在完全相同的渲染條件下重現。實測:16 頁連續兩輪 check 全綠。

## 0.9.0

設計師的兩個方向(討論結論:同一顆引擎的薄外殼,不另開套件):

- **`parity lint`(design lint)**:只看設計稿,驗值是否落在 design token 允許集合——顏色(ΔE 容差內命中即過)、fontSize / padding / itemSpacing / cornerRadius(等於任一尺寸 token 即過)。違規附「最近的 token」建議;有違規 exit 1 可進 CI;某維度沒定義 token 就不 lint 該維度(沒規範就不裝有規範)。不開瀏覽器、不需要實作端。
- **`parity check --reverse`(反向檢視)**:設計師照現有頁面重畫/改版時,「期望」= 現況(實作)、「實際」= 設計稿——給設計師的 diff 清單,不做把關(exit 0)。console/report.json/Markdown 全部同向交換,與 `--baseline` 互斥。

## 0.8.0

實作端覆蓋率的兩塊大拼圖:

- **Shadow DOM / iframe 走訪**:擷取腳本改走「組合樹」——open shadow root、`<slot>` 實際塞進的內容、同源 iframe(含 `srcdoc`)全部看得到、比得到,web components 網站不再整塊隱形。內部座標平移回外層頁面座標系;selector 以 `host >>> 內部路徑` 表示。closed shadow root / 跨域 iframe 拿不到 → 誠實跳過;map 檔 selector 搆不到 shadow 內(`data-parity` 不受限)。實測:shadow 內改背景色、iframe 內改尺寸都精準抓到,demo 輸出零變化。
- **RWD 多斷點**:同一 URL 各斷點對各自的 Figma frame 就能測(渲染視窗 = frame 尺寸,media query 自然生效)——這其實一直可行,本次補上文件、實測(桌機綠/手機紅的精準隔離),並新增 target 級 `width`/`height` 覆蓋(frame 寬 ≠ 視窗寬的少數情況;snapshot 也吃)。
- M6 雲端外殼:明文**不做**(除非出現真實使用者)——設計 QA 是上線前的活,報告分享 PR 留言已覆蓋;決定記 ROADMAP。

## 0.7.0

**M5 下半完工,M1–M5 全部收官**——兩種新設計來源:

- **`parity snapshot`(重構守門)**:把「現在跑著的畫面」凍結成設計基準(JSON + 參考截圖),之後 `check` 保證不跑版——visual regression 的數值版。凍結節點的 Id = CSS selector,配對走新的「selector 身分」關(第 0 關),100% 確定性;不需要 Figma、不經設計來源就能拍。實測:快照 vs 同一頁 = 100/100;模擬重構改壞(色、gap)→ 精準抓到,連 gap 變大擠縮卡片寬度的連鎖後果都如實報出。
- **`ImageDesignSource`(一張圖 + 標註)**:`designImage`(PNG/JPG)搭配標註檔(= DesignNode JSON,`fill` 可省略——顏色由引擎從圖片對應區域取樣;內縮避開反鋸齒、取樣格點眾數;TEXT 字色刻意不取樣,可手填)。這是 XD/Sketch/PS 等**其他設計工具的萬用轉接頭**:匯出圖片就能上車。新依賴 SixLabors.ImageSharp(純 managed)。
- 設計工具支援的明文決定(見 ROADMAP):XD 不做(Adobe 已棄養)、Sketch 門留著等真需求、未來優先 Penpot。

## 0.6.0

給「還沒被服務到的角色」的兩個功能(延續 0.2–0.5 的方向:每個角色從「知道有錯」到「知道下一步」):

- **Figma deep link(設計師的入口)**:設計來源是 Figma 時,Markdown 報告與本機 UI 的圖層名連回 Figma 的那個節點(`node-id`),點一下直接跳到圖層。JSON 設計來源不受影響。
- **還原度走勢(PM 的方向感)**:
  - `baseline save` 順手把當下分數存進快照;`baseline list` 多一欄分數 = 走勢時間軸。
  - `check --baseline` 顯示「基準 75/100 → 現在 83/100(↑ +8)」;Markdown 報告表頭同步(PR 留言就看得到方向)。
  - 本機 UI header 直接顯示還原度分數(之前 UI 竟然沒有分數)。
  - Storage 第一次 schema 演進(加 `Score` 欄):用「加欄位、已存在就略過」的最小遷移,舊 `parity.baseline.db` 直接相容(舊快照分數顯示「—」),實測過 0.4 版的 db。

## 0.5.0

- **新比對維度:相對位置(`offsetX`/`offsetY`)**——補上規畫書 4.8「比相對位置」的承諾,抓得到「尺寸顏色全對但擺錯位置」(例如 badge 從右上角跑到左上角)。設計原則是「寧可漏、不可誤報」:
  - 只比**自由擺放**(非 auto-layout、無 padding)容器的子節點——版面容器的位置由 padding/gap 決定,那邊已有比對。
  - 參照優先取「最近的可靠兄弟」邊(同排/同列、非 TEXT、尺寸 FIXED),沒有才用父層邊——一個元素偏掉不會讓後面整排被連坐。
  - TEXT 不當比對目標(inline/置中的 DOM 文字框 ≠ Figma 文字框);上方只有文字兄弟時 Y 誠實跳過(位置是流經不可靠行高累積的)。
  - 新容差 `tolerances.positionPx`(預設 4);`compare.position: "none"` 可整個關閉——這個欄位從裝飾品變成真的開關。
  - 實測:符合設計的頁面 100/100 零誤報;badge 跑位精準報一條 critical;demo 輸出與之前完全相同(零新雜訊)。
- **修報告 UI:左側清單點了展不開**。`<summary>` 的 click listener 設 `open=true` 會被瀏覽器緊接著的原生 toggle 翻回去 → 落差詳表永遠展不開、清單高亮也沒同步。改為不與原生開合打架:選取只同步高亮(不重建 DOM),開合交還給 `<summary>` 原生行為。另:未配對項目補 `dataset.id`(高亮同步用)、摘要第二行標明「落差數」(它數的是落差條數,不是節點數)。

## 0.4.1

全量程式碼檢視(為 M5 下半暖身)揪出的七項修正:

- **報告口徑一致**:Markdown 表頭的「忠實實作」計數與還原度分數用同一判準(純軟落差節點算忠實)——修掉「100/100 卻 11/12 忠實」的自相矛盾標題。
- **多 Electron 目標**:CDP 連線改按 endpoint 各一條;之前多 target 指向不同 `cdp:` 端點時,第二個會沿用第一條連線抓錯 app。
- **長輪詢/websocket 頁面**:`networkidle` 等不到就退回「DOM load + 緩衝」照常擷取,不再等滿 30 秒炸掉整份報告。
- **`baseline save` 拒存不可信基準**:0 配對時存下的會是空基準(之後所有真實落差全變「新增」),現在直接拒存並說明。
- **設定驗證**:`gate.failOn` 拼錯、`minMatchRate` 超出 0–1,載入時就給看得懂的錯誤。
- **每角不同圓角**:Figma `rectangleCornerRadii` 取 top-left(與實作端讀 `border-top-left-radius` 口徑一致),之前整個不比。
- 小項:報告 UI 配對方式補「容器」標籤;map+watch 同開時儲存配對不再重掃兩次;設計 frame 尺寸為 0 時給明確錯誤(之前把 0 傳給 Playwright 炸出無關錯誤)。

## 0.4.0

整潔項清空:

- **新指令 `parity report`**:從既有 `.parity/report.json` 重生 Markdown 報告,免重掃(`--in` 指定來源、`--md` 寫檔,預設印 stdout)。CI 上傳的 report.json artifact 抓下來就能在本機重現同一份報告。
- **現代色域**:顏色解析新增 `oklch()` 與 `color(display-p3 …)`(OKLCH→OKLab→sRGB、P3 線性矩陣轉換;超出 sRGB 色域 clamp)。`lab` / `rec2020` 等仍不支援。
- GitHub Actions 升版:checkout@v7、setup-dotnet@v6、upload-artifact@v7(脫離 Node 20 淘汰警告)。
- 內部:check / serve / report 的報告 JSON 序列化設定抽共用 `ReportJson`,避免兩處定義漂移。

## 0.3.0

ROADMAP 已知盲點全數清空的版本:一個功能修補 + 兩條主打路徑的真實實證。

- **補 gate 盲點:0 配對不再假 PASS**。gate 先驗「配對可信度」:完全 0 配對或設計端 0 節點 → GATE FAIL 並附原因(通常是 url/frame 指錯);`--baseline` 模式也不豁免(殘缺的現況會把 baseline 的一切誤判成「修好」)。另加選配 `gate.minMatchRate`(0–1)配對率門檻。CLI / Markdown 報告 / serve UI(badge tooltip)都會顯示不通過的原因。
- **Action 消費者流程實證通過**(純驗證紀錄):外部 repo(parity-action-test,實證後已轉私人)以 `@v0.2.0` 跑真 PR——main 綠、壞 PR 打紅 + bot 貼還原度報告、追加 commit 後留言原地更新。
- **CI `--baseline` 實證通過**(純驗證紀錄):main 留既有落差 + commit `parity.baseline.db` + `baseline: true` → CI 綠;PR 新增落差 → 精確只擋新增那條,留言帶「相對基準」區塊。順手修 action.yml 裡 baseline 說明的過期路徑(`.parity/baseline.db` → `parity.baseline.db`)。

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
