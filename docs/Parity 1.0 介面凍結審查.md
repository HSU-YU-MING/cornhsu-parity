# Parity 1.0 — 介面凍結前審查表

> **用途**:1.0.0 = 對外承諾「以下介面凍結,破壞它就升 major」。這份表把**當前所有公開契約面**攤開,讓你在 dogfooding 這 2–4 週(2026-07-18 起)逐項決定「凍結 / 趁現在改 / 待議」。
> **狀態**:**審查完成(2026-08-11)——六面全數拍板,唯餘發版當下的機械動作(CHANGELOG 凍結清單、`v1` tag)**。動工前 = 0.x,改介面免費;發 1.0 後 = 改介面要 major。
> **產生**:2026-07-22,盤點自當時原始碼(`ParityConfig.cs` / `Program.cs` / `Report.cs` / `action.yml` / `BaselineDbContext.cs`)。
> **進度更新**:2026-08-01——**0.10.0 已把面 4、面 5 的高風險待決項全部做掉**(見下方各項的 `已於 0.10.0 處理`)。剩餘待決集中在「字面拼寫凍結」與「發 1.0 時的 `v1` tag」,見文末動作清單。
> **進度更新**:2026-08-11(野生實查輪,PR #26 + onboarding 分支)——面 2 新增 `snapshot --stabilize` 旗標(加法,不破壞);面 4 **發現一個先前漏盤的契約面**:`unmatched[].reason` 字彙(本輪就長出 `randomized-id` 與 `size-implausible` 兩個新值),見面 4 新增待決項;面 1 的「init 範本最小子集」決定被 DX 實查 D1 推翻(改為註解並列 Figma/snapshot 兩條路——範本不是 schema,不影響凍結)。動作清單的「npx 端到端」已實測打勾。
> **進度更新**:2026-08-11(1.0 就緒度分析,0.13.1 三通路驗證後)——**補盤兩個漏列面向與一個缺的條款**:面 6(snapshot 檔案格式,持久化契約先前只盤了 report.json)、面 1 新增「預設容差值」待決、新增「演進條款」節(量測演進的版本語意——0.11.0 的慣例該升格成 1.0 承諾)。五個舊面向全數取得野外實測背書(面 5 的 migration 剛被 0.2.0 老 db 的真實 bug 考驗過並修復,見 0.13.1)。

決定欄填法:`凍` = 就這樣凍結 / `改` = 凍結前要改(附想改成什麼)/ `議` = 還沒想清楚。

---

## 面 1 — `parity.config.json` schema

來源:`src/Parity.Cli/ParityConfig.cs`。JSON 讀取 `PropertyNameCaseInsensitive`、允許註解與尾逗號;正規寫法 camelCase。

| 欄位 | 型別 / 預設 | 備註 | 決定 |
|---|---|---|---|
| `figmaFileKey` | string? | 與 `designFile` 二擇一 | |
| `designFile` | string? | 本機設計 JSON(離線/snapshot 基準) | |
| `designImage` | string? | 搭 `designFile`(此時 designFile 當標註檔) | |
| `designToken` | string? | `"env:FIGMA_TOKEN"` 慣例(env: 前綴讀環境變數) | |
| `mapFile` | string? | 手動圖層→selector 對應檔 | |
| `tokensFile` | string? | design token 平面 JSON;lint 必需 | |
| `baselineFile` | string? | 預設 `parity.baseline.db`(放 repo 根、應 commit) | |
| `targets[]` | `{route, frame, url, width?, height?}` | `frame`=Figma nodeId(如 `10:2`)或 snapshot 的 route | |
| `compare.position` | `"relative"`(預設)/ `"none"` | 只收這兩值,其餘載入即報錯 | |
| `tolerances.sizePx` | 2 | | |
| `tolerances.spacingPx` | 2 | | |
| `tolerances.colorDeltaE` | 2.0 | | |
| `tolerances.fontSizePx` | 0.5 | | |
| `tolerances.positionPx` | 4 | 刻意比尺寸鬆 | |
| `ignore[]` | string[] | CSS selector 清單 | |
| `gate.failOn[]` | `["critical","serious"]` | 有效值:`minor`/`medium`/`serious`/`critical`(**注意是 medium 不是 moderate**) | |
| `gate.minMatchRate` | 0(=不設門檻) | 0–1;0 配對永遠擋 | |

**凍結前要決的點** — 已定案(2026-07-22)
- [x] **`tolerances` 各項預設值**(2026-08-11 補列):預設值改變會讓同一頁面 PASS↔FAIL 翻轉,
  對 CI 使用者是實質行為變更,但先前只凍結了「欄位」沒凍結「預設」。
  ——**已定案(2026-08-11):凍**。預設值凍結;調整預設 = minor + CHANGELOG 顯著標示
  (演進條款第 3 級)。使用者視角:這是「什麼都沒改 CI 卻紅了」的最典型來源,必須可預期。
- [x] `frame` 一欄兩義 → **維持**(「設計來源內的 frame 識別」是一致概念,只是各來源字串形式不同;不改名/不拆欄)。
- [x] 嚴重度字彙 `minor/medium/serious/critical` → **維持凍結**(清楚通用;`medium` 保留)。
- [x] `designToken` 命名 → **維持**(來源中立,未來 Penpot 等也可能需 token;與 action 的 `figma-token` 分屬不同層,非真衝突)。
- [x] `init` 範本最小子集 → **維持**(刻意的最小示範)。

---

## 面 2 — CLI 介面

來源:`src/Parity.Cli/Program.cs`。

**子指令**:`check` `report` `snapshot` `lint` `serve` `map` `baseline (save|list)` `init` `install-browser` `help` `version`

**exit code 契約**(印在 help 裡,是強契約):`0` = 通過 / `1` = 落差超 gate / `2` = 執行錯誤。

| 指令 | 旗標 | 決定 |
|---|---|---|
| `check` | `--config --target --out --md --refresh --headed --baseline --reverse` | |
| `report` | `--config --in --md` | |
| `snapshot` | `--config --target --out --width --height --headed --stabilize`(`--stabilize` 為 2026-08-11 新增,加法) | |
| `lint` | `--config --target --refresh` | |
| `serve` | `--config --port --watch --open` | |
| `map` | `--config --port` | |
| `baseline` | `save` \| `list`,`--config` | |
| `init` | (無) | |
| `install-browser` | `--with-deps` | |

**凍結前要決的點** — 已定案(2026-07-22)
- [x] exit code → **已拆**(0.10.0):通過=0、gate fail=1、執行錯誤=2、配對可信度不足=3。
- [x] `--flag value` vs `--flag=value` → **凍結 space-only**(目前只支援空格式);`=` 式是「只多接受一種寫法」的非破壞性增強,要加隨時可加、不卡凍結前。
- [x] `map` 獨立子指令 → **維持**。
- [x] 旗標命名(`--headed`/`--refresh`/`--reverse`/`--with-deps`)→ **維持凍結**。

---

## 面 3 — GitHub Action inputs

來源:`action.yml`。

| input | 預設 | 決定 |
|---|---|---|
| `config` | `parity.config.json` | |
| `target` | `''` | |
| `working-directory` | `.` | |
| `version` | `''`(=最新) | |
| `figma-token` | `''` | |
| `baseline` | `false` | |
| `comment` | `true` | |
| `upload-report` | `true` | |

**凍結前要決的點(這面有實際 bug 要修)**
- [x] ⚠️ **README 引用不一致**:line 14 用 `@v0.9.7`、line 266 用 `@v1`,但 repo **沒有 `v1` 這個移動式 major tag**。這是別人 copy-paste 就會踩的坑。
  ——**引用不一致已解**(0.10.1:兩處統一為 `@v0.10.1`,都指向真實存在的 tag)。
  但 **① 建立並維護 `v1` moving tag 仍未做**,那是發 1.0 當下的動作,見文末清單。
- [x] input 名(kebab-case)與預設值凍結——`comment`/`upload-report` 預設為 true 是對外行為承諾。
  ——**已定案(2026-08-11):凍**,並明文:**新增「選填」input = minor,不算破壞**
  (凍結是為了保護既有 workflow,不是為了阻止功能演進——不寫這句,以後加任何選項都會猶豫)。

---

## 面 4 — `report.json` 形狀 ★(引擎↔伺服器契約)

來源:`src/Parity.Engine/Report.cs`,序列化 `src/Parity.Cli/ReportJson.cs`(camelCase、字串 enum)。網頁儀表板規畫書 5.3 直接對齊此形狀——這面最該慎重。

> **0.10.0 後的實際形狀**(以下條列為 2026-07-22 盤點時的舊狀態,保留以對照):頂層是 `{ "schemaVersion": 1, "reports": [...] }`,不再是裸陣列;`Box` 寫出 `width`/`height`(讀時仍吃舊的 `w`/`h`);`unit`/`delta` 為 null 時**顯式輸出 `null`**,不再讓 key 消失。

- `FidelityReport`: `route, url, designReference, nodes[], unmatched[], summary`
- `nodes[]` (`NodeResult`): `designLayer, designId, selector, matchedBy, severity, diffs[], designBox, renderedBox`
- `diffs[]` (`PropDiff`): `prop, expected, actual, unit?, delta?, tolerance, severity, status, soft`
- `unmatched[]` (`UnmatchedNode`): `designLayer, designId, reason, designBox`
- `summary` (`ReportSummary`): `designNodes, matched, unmatched, nodesWithDiffs, critical, serious, medium, minor, maxSeverity`
- `*Box` (`Box`): **`x, y, w, h`**(注意是 `w`/`h`,不是 `width`/`height`)

**凍結前要決的點**
- [x] `severity` 字串值:`none/minor/medium/serious/critical`;`status`:`mismatch/missing`——這些字面拼寫凍結後改一個字母就是 major。
  ——**已定案(2026-08-11):凍——且為「封閉集」,拼寫與成員都凍結**。消費端視角:
  嚴重度會進 switch 語句與 `gate.failOn` 設定,冒出新等級與改拼寫同等災難——
  新增等級 = major。(與 reason/matchedBy 的「開放集合」刻意相反:嚴重度是 gate 的
  判斷基礎,字彙必須封閉;未配對理由是診斷資訊,字彙必須能長。)
- [ ] **`unmatched[].reason` 與 `nodes[].matchedBy` 的字彙**(2026-08-11 補盤,先前漏列):
  `reason` 目前有 `no-anchor` / `ambiguous-or-missing-text` / `randomized-id` / `size-implausible`——
  後兩個就是野生實查這一輪長出來的,**證明這是會持續演進的開放字彙**。建議凍結策略:
  **明文宣告為開放集合**(消費端必須容忍未知值,已知值的拼寫不變)而非凍死清單——
  否則每輪實查加一個理由就是 major。`matchedBy`(`selector`/`explicit`/`auto-text`/
  `auto-name`/`auto-container`)同此處理。
  ——**已定案(2026-08-11):凍結策略 = 開放集合**。消費端必須容忍未知值(標準的
  forward-compatible enum 作法);已知值拼寫不變;**配套義務:README/文件維護
  「當前字彙表」,每次新增值同步更新**——開放但無文件 = 黑箱,消費端無從得知有哪些值。
- [x] `WhenWritingNull`:`unit`/`delta` 為 null 時整個 key 消失。消費者(含未來自家伺服器)必須容忍「key 不存在」。要不要改成永遠輸出(null 顯式)以簡化消費端?
  ——**已於 0.10.0 處理**:改為顯式輸出 `null`(`ReportJson.cs` 刻意不設 `WhenWritingNull`),契約少一個「有時消失的 key」。
- [x] `Box` 的 `w/h` 簡寫——疊框視圖消費者要知道。要不要正名 `width/height` 讓 JSON 自我解釋?
  ——**已於 0.10.0 處理**:寫出 `width`/`height`,**讀時 `w`/`h` 舊檔照吃**(`BoxJsonConverter`),舊快照/報告不必重拍。C# 內部維持 `.W`/`.H`,只改 wire format。
- [x] 頂層是「裸陣列」而非包一層 `{version, reports:[...]}`。**沒有 schema 版本欄位** → 未來報告格式演進時消費端無從判斷版本。**強烈建議凍結前加一個 `schemaVersion`**,否則 1.0 就把「無版本」這件事也凍死了。
  ——**已於 0.10.0 處理**:改為 `{ "schemaVersion": 1, "reports": [...] }`(`ReportDocument`)。讀到無法辨識的舊格式給明確訊息,不靜默失敗。

---

## 面 5 — baseline SQLite schema ⚠️(最高風險)

來源:`src/Parity.Storage/BaselineDbContext.cs`。`parity.baseline.db` **會被 commit 進使用者 repo**,CI 讀它。

- `Snapshots`: `Id, CreatedAt, Commit, Branch, Score`
- `Diffs`: `Id, SnapshotId, Route, DesignLayer, Selector, Prop, Severity(存字串), Expected, Actual`;index on `SnapshotId`;cascade delete
- ~~建表用 **`EnsureCreated`,無 migration**~~ → **0.10.0 起走 EF Core migrations**(`src/Parity.Storage/Migrations/`,已有 `InitialCreate`)

**凍結前必決(這是整份表最該先拍板的一條)**
- [x] ⚠️ **`EnsureCreated` 對已存在的 db 不會加欄位**。`Score` 當初能加是因為走了一次手動 ALTER(0.6.0)。一旦 1.0 凍結 schema,而使用者 repo 裡已 commit 舊 db,未來任何加欄位:要嘛破相容、要嘛得補 migration。**二選一**:
  - (A) 1.0 前導入 EF Core migrations(有正式演進路徑),或
  - (B) 明文宣告「1.x 內 baseline schema 凍結不動」,把 schema 演進推到 2.0。

  ——**已於 0.10.0 選 (A)**:改走正式 migration;既有(`EnsureCreated` 建、無遷移史)的 db 首次開啟時**自動接管**(先標記 `InitialCreate` 為已套用再 `Migrate`),不因「表已存在」而爆。這條原本是整份表最高風險項,現已消除。
- [ ] 決定後寫進 CHANGELOG 的 1.0.0 凍結清單與 ROADMAP。**仍待做**——0.10.0 的 CHANGELOG 已記錄這次遷移,但「1.0.0 凍結清單」要等發 1.0 時才寫。

---

## 面 6 — `parity.snapshot.json` 檔案格式(2026-08-11 補盤)★ 持久化契約

來源:`SnapshotBuilder.ToFrame` 產出的 DesignNode 樹,經 `ReportJson` 序列化;
`JsonDesignSource` 回讀。**使用者會把它 commit 進 repo、跨版本讀取**——與 report.json
同級的持久化契約,先前漏盤(只盤了 report.json)。

實際的演進壓力(2026-08 野生實查輪,一輪就發生三次):
- SVG 葉子化 → 舊 snapshot 凍有 svg 內部節點,重拍才對齊
- 隨機 id 改結構路徑 → 舊 snapshot 凍有隨機 id selector,重拍才配得上
- 深度 512 → 讀寫都放寬(w/h 舊欄名相容已於 0.10.0 處理,`BoxJsonConverter` 讀時照吃)

**凍結前要決的點**
- [x] **相容策略明文化**。目前事實上的策略是「格式向後可讀 + 語意演進靠重拍一次遷移」
  (工具會以 `randomized-id` 之類的理由提示重拍,不靜默壞掉)。
  ——**已定案(2026-08-11):凍**,措辭收窄為——「**1.x 內**新版永遠讀得動舊檔
  (欄位相容);量測/selector 語意演進可能使舊基準過時,此時工具必須明確提示重拍
  (不得靜默誤報),屬 minor(演進條款第 2 級);major 才允許丟棄讀取相容」。
  維護者視角:「永遠」是無限期承諾,配上 schemaVersion 才有 2.0 的逃生門。
- [x] snapshot 檔要不要加 `schemaVersion`(report.json 於 0.10.0 加過同款)?
  ——**已定案並實作(2026-08-11):加**。信封 `{ "schemaVersion": 1, "root": {...} }`
  (`SnapshotDocument`);讀取端嗅探:無版本欄的舊檔(裸 DesignNode,含手寫 design JSON)
  視為 v1 照吃、零破壞;讀到比自己新的版本給明確錯誤(「由較新的 Parity 產生,請升級」),
  不靜默失敗。E2E:新檔含信封、samples/demo 裸檔照常運作。

---

## 演進條款 — 量測演進的版本語意(2026-08-11 補列)

**問題**:量測修正(更準)會改變數值 → 使用者的 CI 變紅、基準要重拍——嚴格主義下這是
breaking,但把每次量測修正都升 major 等於凍結工具的核心價值。0.11.0 已有先例
(修捲動進場動畫的量測 → 破壞基準相容 → 依 0.x 慣例升 minor 並在 CHANGELOG 開頭
標「升級必做:重拍基準」)。**不先寫下規則,1.0 後第一次量測修正就會陷入 major/minor 辯論。**

**建議寫進 1.0 承諾的規則**——**已定案(2026-08-11):採納**,並補第 5 條(使用者保護):
1. **介面**(config / CLI / action / report / baseline / snapshot 六面)破壞 → **major**
2. **量測語意演進**(量得更準、配對規則變化,可能需重拍 snapshot 基準或使 baseline
   計數變化)→ **minor**,且 CHANGELOG 開頭顯著標示「升級必做」(0.11.0 格式)
3. **預設值調整**(容差、gate 預設)→ **minor** + 顯著標示
4. 行為完全不變的修正 → patch
5. **minor 若需要重拍基準,工具必須大聲失敗並給指引**(如 `randomized-id` 的遷移提示、
   配對可信度 exit 3)——不得靜默給錯誤結果。這已是現行行為,升格為承諾。

---

## 發 1.0.0 的動作清單(審查通過後)

- [x] dogfooding 滿 2–4 週,且期間對上面各面**沒有再想改的**
  ——2026-08-11 檢核:07-18 起算已滿 3.5 週;野生實查 + 三通路驗證期間,
  六面均無破壞性變更需求(--stabilize 為加法、reason 字彙走開放集合)
- [ ] 上面每個 ⚠️ 都已拍板:schema 遷移策略 ✅(0.10.0 選 EF migrations)、report `schemaVersion` ✅(0.10.0 已加)、action `v1` tag ⬜(**唯一未解**,是發 1.0 當下的動作,見下方最後一項)
- [x] `npx cornhsu-parity` 端到端實裝跑過一次(通路才 0.9.5 生,1.0 等於承諾它也穩)
  ——**已實測**(2026-08-11,DX 實查):Windows x64 上 `npx -y cornhsu-parity` 首跑 12 秒
  (含平台包下載),snapshot + check 全程正常,exit code 正確。
- [ ] CHANGELOG 寫 1.0.0:不列功能,**列「以下介面自此凍結」**
- [ ] `git tag v1.0.0 && git push origin v1.0.0`(觸發 release.yml 發 NuGet + npm)
- [ ] 建立 / 移動 `v1` major tag → v1.0.0(之後每個 1.x 都把 `v1` 前移)
