# Parity 1.0 — 介面凍結前審查表

> **用途**:1.0.0 = 對外承諾「以下介面凍結,破壞它就升 major」。這份表把**當前所有公開契約面**攤開,讓你在 dogfooding 這 2–4 週(2026-07-18 起)逐項決定「凍結 / 趁現在改 / 待議」。
> **狀態**:審查中。動工前 = 0.x,改介面免費;發 1.0 後 = 改介面要 major。**這是最後一次免費窗口。**
> **產生**:2026-07-22,盤點自當時原始碼(`ParityConfig.cs` / `Program.cs` / `Report.cs` / `action.yml` / `BaselineDbContext.cs`)。
> **進度更新**:2026-08-01——**0.10.0 已把面 4、面 5 的高風險待決項全部做掉**(見下方各項的 `已於 0.10.0 處理`)。剩餘待決集中在「字面拼寫凍結」與「發 1.0 時的 `v1` tag」,見文末動作清單。

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
| `snapshot` | `--config --target --out --width --height --headed` | |
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
- [ ] input 名(kebab-case)與預設值凍結——`comment`/`upload-report` 預設為 true 是對外行為承諾。

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
- [ ] `severity` 字串值:`none/minor/medium/serious/critical`;`status`:`mismatch/missing`——這些字面拼寫凍結後改一個字母就是 major。**仍待決。**
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

## 發 1.0.0 的動作清單(審查通過後)

- [ ] dogfooding 滿 2–4 週,且期間對上面五面**沒有再想改的**
- [ ] 上面每個 ⚠️ 都已拍板:schema 遷移策略 ✅(0.10.0 選 EF migrations)、report `schemaVersion` ✅(0.10.0 已加)、action `v1` tag ⬜(**唯一未解**,是發 1.0 當下的動作,見下方最後一項)
- [ ] `npx cornhsu-parity` 端到端實裝跑過一次(通路才 0.9.5 生,1.0 等於承諾它也穩)
- [ ] CHANGELOG 寫 1.0.0:不列功能,**列「以下介面自此凍結」**
- [ ] `git tag v1.0.0 && git push origin v1.0.0`(觸發 release.yml 發 NuGet + npm)
- [ ] 建立 / 移動 `v1` major tag → v1.0.0(之後每個 1.x 都把 `v1` 前移)
