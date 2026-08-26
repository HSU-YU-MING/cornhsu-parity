# Parity 開發指南

設計還原度的**屬性級**自動檢查(.NET 10、C#):把 Figma／快照的數值與真實渲染的數值逐項比，
落差超過門檻就讓 CI 紅燈。功能、設定檔格式、比什麼不比什麼見 [README.md](README.md)
(使用者文件，中英雙份)；未完成與已知盲點見 [ROADMAP.md](ROADMAP.md)；版本史見
[CHANGELOG.md](CHANGELOG.md)。這份是開發慣例。

**發版流程的共通部分寫在全域 skill `nuget-packages`**(`~/.claude/skills/nuget-packages/SKILL.md`)
——tag 是版本真相源、Trusted Publishing、csproj 版號固定 `0.0.0-dev`，那裡有，這裡不重複。
本 repo 特有的部分在下面「發版:本 repo 特有」。

## 指令

- 建置:`dotnet build Parity.slnx -c Release`
- 測試:`dotnet test Parity.slnx -c Release`(227 條)
- 排版守門:`dotnet format Parity.slnx --verify-no-changes`——CI 有獨立 job，不過就紅
- README 數字守門:`.\scripts\verify-readme-facts.ps1`(`-Update` 一鍵重貼兩份 README，
  `-NoBuild` 沿用既有建置)。**本機沒有 pwsh，用 Windows PowerShell 5.1 跑**(腳本存成
  UTF-8 with BOM 就是為了這件事);CI 用 runner 內建的 `pwsh`
- 離線示範(不需 Figma token、不需網路):
  ```
  dotnet run --project src/Parity.Cli -c Release --no-build -- check --config samples/demo/parity.config.json
  ```
  **預期 exit 1**——示範頁是刻意做壞的，GATE FAIL 才是對的行為
- 首次跑真瀏覽器測試前:`dotnet run --project src/Parity.Cli -- install-browser`
- 本機乾跑封裝(不發佈):見 [RELEASING.md](RELEASING.md) 最後一節

### 網頁外殼(Parity.Server + web/)

```
dotnet run --project src/Parity.Server -- create-project <名稱>          # 印一次 CI token
dotnet run --project src/Parity.Server -- create-invite <專案> <email> owner
dotnet run --project src/Parity.Server                                   # http://127.0.0.1:4322
cd web && npm ci && npm run build      # 產物落 src/Parity.Server/wwwroot(gitignore)
cd web && npm run dev                  # vite dev，/api 代理到 127.0.0.1:4322
```

管理指令的資料庫路徑吃 `PARITY_SERVER_DB`(預設 cwd 的 `parity-server.db`)。
`npm run build` 內含 `tsc --noEmit`，型別錯就不會產出——CI 的 `web-build` job 靠這點守門。

### EF migrations

- `Parity.Server.Data`:migrations 在該專案，**startup 是 `src/Parity.Server`**
  (`Microsoft.EntityFrameworkCore.Design` 常駐在 Server 專案，`PrivateAssets=all`)
- `Parity.Storage`(baseline db):Design 套件**刻意不常駐**——它會傳遞帶入
  `System.Security.Cryptography.Xml`(NU1903 高風險)污染審計。要產新 migration 時
  臨時裝、產完移除，csproj 裡有完整指令。

## 發版:本 repo 特有

四個 NuGet repo 裡**只有 Parity 已進 1.x**，規則跟其他三個開始分岔:

- **六面介面凍結**(config schema／CLI 與 exit code／action inputs／`report.json`／
  baseline schema／snapshot 格式)，破壞任一面升 major。量測語意演進 = minor + CHANGELOG
  開頭標「升級必做」。完整的**演進條款**在 CHANGELOG 的 1.0.0 節，逐項審查過程在
  `docs/Parity 1.0 介面凍結審查.md`。
- **每發一個 1.x 要把移動式 `v1` tag 前移**:`git tag -f v1 v1.x.y && git push -f origin v1`。
  README 教使用者用 `@v1`，忘了前移等於使用者永遠拿不到新版。
- `release.yml` 的觸發**刻意只認三段式 `v[0-9]*.[0-9]*.[0-9]*`，不是 `v*`**——`v1` 也符合
  `v*`，每次前移都會誤觸發一次注定失敗的 release(1.0.0／1.1.0 各踩一次，run #27/#29)。
  **不要「順手」改回 `v*`。**
- `CHANGELOG.md` 是**推 tag 之前**的版本真相源:`verify-readme-facts.ps1` 的「發佈版數」
  與「最新版號」都對著它數，`release.yml` 再確認 tag 與 CHANGELOG 頂端一致。
- 發兩個通路(NuGet + npm)。`npm/cornhsu-parity/package.json` 的版號固定
  `0.0.0-placeholder`，由 `npm/prepare.mjs` 在發布時改寫——**跟 csproj 的 `0.0.0-dev` 一樣不要改**。
- `NuGet/login` 已釘 commit SHA(`8d196754b4036150537f80ac539e15c2f1028841` = v1.2.0)。
  **不要改回 `@v1`**;上游出新版要更新時，四個 NuGet repo 一起換 SHA。

## ⚠ 現況與文件不符(下次發版前必須先處理)

**`CHANGELOG.md` 掉了 `## 1.1.0` 標題。** 網頁外殼 M1(commit `00ad515`)把 `## 1.1.0`
就地改成 `## 未發佈`，於是 1.1.0 那一節**已發佈的內容被併進未發佈區**。後果:

- `verify-readme-facts.ps1` 是綠的，但它數出來的是「26 版／最新 1.0.0」——
  實際已發到 **v1.1.0(27 版)**。守門的真相源被污染，所以它抓不到。
- 下次發版**不能直接把「未發佈」改成新版號**:那會把 1.1.0 的內容再發一次。
  先把 `## 1.1.0` 那一節切回去(內容 = `git show 5525fa1:CHANGELOG.md` 的 1.1.0 段落)，
  剩下的網頁外殼條目才是新版的內容，然後跑 `-Update` 讓 README 的數字跟上。

另外兩處較輕的漂移(不擋發版，但別照著文件行事):

- README 的 Architecture 與 Milestones、ROADMAP 的「M6 雲端外殼:明文不做」都**還停在
  網頁外殼動工之前**——`Parity.Server` / `Parity.Server.Data` / `web/` 三個專案在文件上
  是不存在的。(當年否決 M6 的理由是 SSRF 與雲端跑瀏覽器的成本;現在這個外殼**不掃描**，
  所以理由不衝突——但文字要補。)
- CHANGELOG 未發佈區還寫著 `PARITY_SERVER_ALLOW_REMOTE=1` 這個逃生口，**程式碼裡已經沒有了**
  (M3 上鎖後把保險絲拆了，同一節後面有講)。發版前把這條併乾淨。
- `.github/dependabot.yml` 的註解說 SQLitePCLRaw pin 在 3.0.3，實際已是 3.0.5。

## 架構要點與刻意取捨

- **一個引擎、多個外殼。** `Parity.Engine` 是純函式庫，只比對兩棵正規化的樹;
  `FidelityEngine` 是唯一進入點。外殼有三家:CLI(`Parity.Cli`)、本機報告 UI
  (`parity serve`，零建置 SPA 放 `src/Parity.Cli/wwwroot`)、網頁儀表板
  (`Parity.Server` + `web/`)。**引擎不知道外殼存在**。
- **鐵則:雲端不跑瀏覽器。** `Parity.Server` 唯一的寫入口是 `POST /api/runs`，收的是
  **已經跑完的報告**;不存在任何「給我 URL 我去掃」的端點。掃描永遠發生在本機／CI。
  這條鐵則的實體化就是 **`Parity.Server.csproj` 永遠不引用 Playwright**——不要為了任何
  方便把它加進去。
- **報告契約單一來源:`Parity.Engine/ReportWire.cs`。** CLI 落地、serve API、Server 解析
  全對同一組 `JsonSerializerOptions`。兩個設定是契約的一部分，不是風格:
  **不設 `WhenWritingNull`**(null 欄位顯式輸出，消費端不必判斷 key 存不存在)、
  **`MaxDepth = 512`**(真實網站 DOM 30+ 層，每層 Children 佔 2 個 JSON 深度，預設 64 會炸)。
- **絕對 x/y 永遠不比。** 彈性版面裡它本來就會差，比了就是保證誤報，誤報就是失去信任。
  比的是尺寸／padding／spacing／字體／顏色(CIEDE2000 ΔE)／**相對**位置。
- **`Parity.Server.Data` 刻意沒有 Diff 表。** M4 實測:21 頁站一次 push 的逐條落差是
  36,058 列／9.95MB，而且**零讀者**——總覽與趨勢讀 `PageResult`，詳情頁從 gzip 原文重建。
  這是實測後拿掉的，不是忘了做(`Entities.cs` 結尾有留帳)。要加回來請先確認有讀者。
- **`0.0.0-dev` 是刻意的。** `parity version` 印 `InformationalVersion`，所以本機建置顯示
  `0.0.0-dev+<commit>`——一眼看得出不是發行版，而且 bug 回報裡直接帶著 commit。

## 地雷

### CI 步驟的順序有語意

`ci.yml` 的 `install-browser` **刻意排在 `dotnet test` 之前**:`SettleCaptureTests` 需要
真實瀏覽器，沒裝會**自己靜靜略過**——那條測的是量測正確性，靜靜略過等於沒驗。
調換順序不會有任何錯誤訊息，只會少驗一批東西。

### `PlaywrightPlatform=all` 不能「優化」掉

`Parity.Cli.csproj` 收齊五個平台的 Playwright driver。預設只複製建置當下平台的 node，
在 ubuntu runner 上 pack 出來的包，Windows 使用者裝了直接 `Driver not found`——
**0.9.1 真的這樣發出去過**。`pack` 綠不代表裝得起來，所以 CI 有 `pack-smoke` job
真的把包裝起來跑一次 `--help`。

### 量測前的「定案」是幾何保證，不是等待

`SettleScript.cs` 走過兩次彎路(0.11.0、0.11.1)才收斂:凍結 transition 只保證不動，
不保證停在**最終狀態**;捲動觸發的進場效果凍結後會永遠停在第 0 格。等待也不行——
元素還沒被觸發時版面本來就是靜止的，「穩定」的條件在 callback 送達前就成立了。
現在的做法是 C# 端**把視窗高度暫時撐到整頁高**，讓 IntersectionObserver 必然全部觸發。
`wait(minRounds, …)` 的 `minRounds` **不能省**——那就是前兩版失敗的原因。檔案開頭的
註解寫了完整脈絡，改這塊之前先讀。

### 快照的探測擷取不能拍截圖

`parity snapshot` 會多載入頁面一次來實測隨機 id(純字母亂碼 `rkxvdnnzty` 這種，
字元啟發法認不出)。截圖字典是**後拍覆寫先拍**，所以探測擷取一律
`CaptureScreenshot = false`——否則「被凍結的那棵樹」與「參考截圖」來自不同次載入，
serve UI 的疊框會對不齊(`--stabilize` 從 0.13.0 起就有這個縫，1.1.0 一併補上)。

### 穩定 id 白名單:兩端規則必須一致

拍照端(`SnapshotCommand`)把實測穩定的 id 存進快照信封的 `stableIdAnchors`，
check 端(`ScanSession`)從快照讀出來傳進擷取。**只改一端 selector 就對不上、整批配不到。**
`CaptureScript` 的白名單閘 `null = 現行行為`，舊快照因此零破壞。

### 版控裡刻意缺席的檔案

- `samples/demo/parity.map.json` 與 `samples/demo/parity.baseline.db` **刻意不進版控**——
  demo 要保持「有未配對、要人工補」的教學情境。本機跑過 `parity map` 會生出來，
  **不要 commit**。也因此本機跑 demo 是 12/12 全配對，CI 上不是。
- `.env`(裡面是 `FIGMA_TOKEN`)、`Parity網頁外殼規畫書.md`(含個人職涯脈絡的本機筆記)
  都在 `.gitignore` 裡，兩個都在 repo 根目錄，**不要 commit、不要貼進對外文件**。
- `.gitignore` 最後那條 `!npm/cornhsu-parity/bin/` 是反向規則:npm 主套件的啟動腳本是
  **原始碼**，不是建置產物。刪掉它，npm 包會少掉入口點。

### `verify-readme-facts.ps1` 的 `Occurrences` 不能省

每條事實都宣告了預期命中次數。少了這格，某天有人改寫 README 措辭、regex 不再命中時，
檢查會**安靜地變成「零項通過」**而不是報錯。regex 對不上時**修 regex，不要刪檢查**。

### action.yml 的 inputs 一律先進 env

`${{ }}` 是 shell 看到指令**之前**的字串替換。呼叫端只要把 input 接到不可信來源
(`version: ${{ github.event.issue.body }}`)，那段內容就變成 runner 上的指令。
所以每個 input 都先進 `env:` 再用 shell 變數引用。**不要為了短而改成內插。**

### 其他

- `ImageSharp` 的 major 升級被 dependabot **明確 ignore**:4.0 把授權變成建置期強制，
  沒有授權檔就 CI 全紅(實測於 #19)。3.1.x 仍是 Split License、開源免費。
- `Parity.Server` 的 `create-invite` 印出的邀請連結**硬寫 `http://127.0.0.1:4322`**。
  真的對外部署(M5)時這裡要跟著改，否則發出去的連結是壞的。
- `parity serve` 只綁 127.0.0.1，另外還擋 Host header(DNS rebinding)與 POST 的
  Origin(CSRF)。報告含站點結構與截圖——**不要為了「方便同事看」改成 0.0.0.0**;
  要給人看就是走 `parity push` 到儀表板。

## 技術債與留帳

- **相依漏洞:2026-08-26 實掃 0 個**(`dotnet list Parity.slnx package --vulnerable
  --include-transitive`，六個專案全綠)。`SQLitePCLRaw` 手動 pin 到 3.0.5 蓋掉 EF Sqlite
  傳遞進來的 NU1903 版本，dependabot 每月盯著。
- **auto-name 通用名假配對**(ROADMAP 有完整留帳):「Content」這種通用圖層名會撞到
  無關頁面的 `#content`。尺寸合理性防線(面積比 >16 拒配)只擋得住量級差很多的;
  **`auto-container`(LCA 推論)不經這道防線**。要根治得上語意層，動工前先蒐集更多野生樣本。
- `Parity.Server.Data` 目前是 SQLite。M5 定供應商時(PostgreSQL 之類)migrations 要重生
  ——已知的一次性成本，寫在規畫書「待決區」。

## 開工慣例

- **動這個 repo 任何檔案，都要先看 `nuget-packages` skill。**
- 收尾:動了 console 輸出或加了測試 → 跑 `verify-readme-facts.ps1`;
  動了功能面 → **兩份 README 一起改**(英文是主檔，`README.zh-Hant.md` 不能漂);
  重大決策記進 CHANGELOG／ROADMAP，workaround 留技術債帳。
- 這個專案的野生實查(`docs/野生實查-*.md`)有紀律:**樣本必須包含非作者的網站與設計檔**。
  路線 A／B 的 11 個發現沒有一個能在自家 dogfooding 樣本上量出來。要驗新的量測規則，
  別只拿 `samples/demo` 跟 cornhsu.com 試。
- `parity check` 走真瀏覽器，沒有 headless 以外的替身——量測相關的改動，
  編譯過與單元測試綠**都不等於量得對**，要真的對一個頁面跑一次看數字。
