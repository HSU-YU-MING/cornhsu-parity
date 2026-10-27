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
- 測試:`dotnet test Parity.slnx -c Release`(條數以 README「At a glance」為準——那格有
  `verify-readme-facts.ps1` 守門;這裡不寫數字,寫了就會像以前停在 227 條那樣靜靜過期)
- 排版守門:`dotnet format Parity.slnx --verify-no-changes`——CI 有獨立 job，不過就紅
- README 數字守門:`.\scripts\verify-readme-facts.ps1`(`-Update` 一鍵重貼兩份 README，
  `-NoBuild` 沿用既有建置)。**本機沒有 pwsh，用 Windows PowerShell 5.1 跑**(腳本存成
  UTF-8 with BOM 就是為了這件事);CI 用 runner 內建的 `pwsh`
- 離線示範(不需 Figma token、不需網路;本機建置是 `0.0.0-dev`,不送匿名統計——加 `--stats-preview` 看它會送什麼):
  ```
  dotnet run --project src/Parity.Cli -c Release --no-build -- check --config samples/demo/parity.config.json
  ```
  **預期 exit 1**——示範頁是刻意做壞的，GATE FAIL 才是對的行為。CI 另外用 `jq -e` 斷言
  report.json 的計數(11/12 配對、5 serious + 1 soft minor,見 ci.yml 那一步的註解);
  `CliBrowserTests.The_offline_demo_produces_exactly_the_counts_ci_asserts` 對 demo 的乾淨複本實跑、
  拿 ci.yml 裡的數字比對,量測或配對一改本機就先紅。數字要改時兩邊一起看
- 首次跑真瀏覽器測試前:`dotnet run --project src/Parity.Cli -- install-browser`
- 本機乾跑封裝(不發佈):見 [RELEASING.md](RELEASING.md) 最後一節

### 網頁外殼(Parity.Server + web/)

```
dotnet run --project src/Parity.Server -- create-project <名稱>          # 印一次 CI token
dotnet run --project src/Parity.Server -- create-invite <專案> <email> owner
dotnet run --project src/Parity.Server -- backup <檔案>                  # VACUUM INTO 線上備份
dotnet run --project src/Parity.Server                                   # http://127.0.0.1:4322
cd web && npm ci && npm run build      # 產物落 src/Parity.Server/wwwroot(gitignore)
cd web && npm run dev                  # vite dev，/api 代理到 127.0.0.1:4322
```

資料庫位置由 web host 與管理指令**共用同一個解析**(`ServerDatabase.ConnectionString`):
`ConnectionStrings:parity`(appsettings 或環境變數 `ConnectionStrings__parity`)→ `PARITY_SERVER_DB`(檔案路徑)
→ cwd(web host 是 ContentRoot)的 `parity-server.db`。2026-10-22 之前兩邊各讀各的,只設 `PARITY_SERVER_DB`
時管理指令與 web host 開的是不同檔。
`npm run build` 內含 `tsc --noEmit`，型別錯就不會產出——CI 的 `web-build` job 靠這點守門。

### EF migrations

- `Parity.Server.Data`:migrations 在該專案，**startup 是 `src/Parity.Server`**
  (`Microsoft.EntityFrameworkCore.Design` 常駐在 Server 專案，`PrivateAssets=all`)
- `Parity.Storage`(baseline db):Design 套件**刻意不常駐**——它會傳遞帶入
  `System.Security.Cryptography.Xml`(NU1903 高風險)污染審計。要產新 migration 時
  臨時裝、產完移除，csproj 裡有完整指令。

## 發版:本 repo 特有

四個 NuGet repo 裡**只有 Parity 已過 1.0**(2.0.0 起是 2.x)，規則跟其他三個開始分岔:

- **六面介面凍結**(config schema／CLI 與 exit code／action inputs／`report.json`／
  baseline schema／snapshot 格式)，破壞任一面升 major。量測語意演進 = minor + CHANGELOG
  開頭標「升級必做」。完整的**演進條款**在 CHANGELOG 的 1.0.0 節(2.0.0 節重申它適用於 2.x)，
  逐項審查過程在 `docs/Parity 1.0 介面凍結審查.md`，2.0.0 之後的六面現況在 `docs/公開介面盤點.md` 第 11 節
  (2.1.0 匿名統計的加法在第 12 節)。
- **每發一個 2.x 要把移動式 `v2` tag 前移**:`git tag -f v2 v2.x.y && git push -f origin v2`。
  README 教使用者用 `@v2`，忘了前移等於使用者永遠拿不到新版。
- **`v1` 停在 v1.1.0，不再前移、不再發 1.x**。那份 `action.yml` 不限 major(`@v1` 會裝到 2.x)，
  這是 2.0.0 發版時接受的副作用(見 RELEASING.md「2.0.0」一節)——**不要為了它補發 1.x**。
- `action.yml` 在 `version` 留空時裝 `"<major>.*"`(現在 `"2.*"`)，major 要跟 CHANGELOG 最新版一致;
  `verify-readme-facts.ps1` 也依最新版號的 major 檢查 README 的 `@vN`。發下一個 major 時兩邊一起改。
- **`parity push` 與伺服器協定是實驗性的,不在六面凍結內**(2.0.0 決定;範圍:push 的旗標、環境變數、exit code,
  `X-Parity-*` 標頭、HTTP 狀態碼、伺服器拒收規則,job summary 內容)。**何時納入凍結:Parity.Server 公開部署
  (網頁外殼 M5)的那一版**——CHANGELOG 要寫明「push 自本版起凍結」,並拿掉 `--help` 的 `(experimental)`;
  在那之前改 push 的介面走 minor。`report.json` 不受影響,一直是凍結面 4。
- **鐵則:`parity bug-report`(與日後任何「讓使用者回報」的功能)不得自動送出任何資料。** bug-report 本身不連網;只在本機
  整理、印出來給使用者看,網址只印出來。(**Parity 整體不是不連網的工具**:Figma 設計來源連 Figma API、`parity push`
  連使用者給的伺服器、2.1.0 起 `parity check` 預設送匿名統計到 stats.cornhsu.com——見下一條。對外文件不要寫成
  「Parity 不連網」。)**對外的說法要準確**:遮罩過的報告全文放在網址的 query string 裡,打開網址
  (使用者自己點,或 `--open` 交給瀏覽器)時就會送到 github.com、留在瀏覽器紀錄;issue 要使用者自己按送出才會建立。
  不要寫成「不送任何資料」。不收網址、route、selector、
  節點名稱、文字、Figma fileKey、token、檔案路徑(遮成 `<path>`)、環境變數內容:設定檔只經 `ConfigSummary` 的白名單
  欄位,整份文字再過 `Redaction`。`BugReportTests` 用一份塞滿秘密的設定檔斷言輸出裡一個都沒有——改這兩個類別時它必須綠。
  bug-report 是實驗性的,不在六面凍結內。
- **匿名使用統計(2.1.0,`src/Parity.Cli/UsageStats.cs`)。協定的唯一真相源是 `D:\擴充功能\nuget\stats-collector\PROTOCOL.md`**
  (與 XamlContrast 1.1 共用)。要改任何欄位:先改協定、收集站先上線新版,工具才能送——收集站對多一個、少一個欄位一律 400,
  工具這邊改了而收集站沒改,資料就靜靜全丟。**不在協定封閉集裡的屬性不送,也不替它發明家族**:`DiffEngine` 多一種屬性時
  `UsageStatsTests` 會紅,先回去改協定。對使用者的承諾(只送計數、不影響結果、說明只在 stderr)寫在 README 的
  「Anonymous usage statistics」,改行為時兩份 README 一起改。
  - **預設打開是 `UsageStats.SendByDefault` 這一個常數**。倫理審查若要求主動同意就改它;協定還要求那時「第一次在終端機
    詢問」,目前沒有實作,Action 的 `share-stats` 預設值也要一起決定。
  - **研究資料不能混進自己的測試**,三道都不能拆:測試行程預設 `DO_NOT_TRACK=1`、只准送往 loopback、標記檔放暫存目錄
    (`tests/Parity.Tests/TestProcessDefaults.cs`);`ci.yml`/`release.yml` 整份設 `DO_NOT_TRACK: '1'`(release 是真版號,
    不是開發版;`RepoInvariantsTests` 守著)。要測送出的測試用 `EnvScope.StatsVariables` 清環境、`CORNHSU_STATS_URL`
    指到本機假站(`StatsCollectorStub`)。本機跑 `samples/demo` 是 `0.0.0-dev`,不會送。
  - job summary 那一行由 CLI 寫,只在 action 設了 `PARITY_STATS_JOB_SUMMARY=true` 時才寫(內部用,不是使用者的開關)。
- **2.1.0 發佈前的硬條件**:維護者要先拿到**指導教授(或倫理審查)對「預設打開」的確認**;收集站 stats.cornhsu.com 也要
  先上線。兩件都沒有就不推 tag(RELEASING.md「2.1.0」;背景:`D:\擴充功能\nuget\稽核-2026-10-22\統計功能規劃.md`)。
- `release.yml` 的觸發**刻意只認三段式 `v[0-9]*.[0-9]*.[0-9]*`，不是 `v*`**——`v1` 也符合
  `v*`，每次前移都會誤觸發一次注定失敗的 release(1.0.0／1.1.0 各踩一次，run #27/#29)。
  **不要「順手」改回 `v*`。**
- `CHANGELOG.md` 是**推 tag 之前**的版本真相源:`verify-readme-facts.ps1` 的「發佈版數」
  與「最新版號」都對著它數，`release.yml` 再確認 tag 與 CHANGELOG 頂端一致。
- 發兩個通路(NuGet + npm)。`npm/cornhsu-parity/package.json` 的版號固定
  `0.0.0-placeholder`，由 `npm/prepare.mjs` 在發布時改寫——**跟 csproj 的 `0.0.0-dev` 一樣不要改**。
- `NuGet/login` 已釘 commit SHA(`8d196754b4036150537f80ac539e15c2f1028841` = v1.2.0)。
  **不要改回 `@v1`**;上游出新版要更新時，四個 NuGet repo 一起換 SHA。

## 曾經的文件漂移(2026-08-26～27 已全數修掉,留紀錄)

這四條都修完了,寫在這裡是因為**它們的成因會重演**:

- **CHANGELOG 掉了 `## 1.1.0` 標題**(`883589b` 修)。網頁外殼 M1 本意是新增「未發佈」區,
  卻把 `## 1.1.0` 就地換掉,已發佈內容被併進未發佈區。
  > **最陰的是**:`verify-readme-facts.ps1` 對著 CHANGELOG 數版本,真相源被污染之後
  > 它是綠燈、數出「26 版/最新 1.0.0」而實際是 27 版/1.1.0——**守門員抓不到自己在說謊**。
  > 教訓:守門員與被守對象共用真相源時,污染真相源會讓守門靜靜失效。
  > 所以**新增「未發佈」區要用插入,不要就地改標題**。
- **README / ROADMAP 停在網頁外殼動工之前**(`00445ae` 修)。`Parity.Server` / `web/`
  在對外文件上完全不存在。**動了架構就要同步兩份 README**(英文是主檔)。
- **CHANGELOG 未發佈區寫著已被拆掉的 `PARITY_SERVER_ALLOW_REMOTE=1`**(同上修)。
- **dependabot 註解的 SQLitePCLRaw 版本寫錯**(3.0.3,實為 3.0.5)。升版時註解要跟著改。

## 架構要點與刻意取捨

- **一個引擎、多個外殼。** `Parity.Engine` 是純函式庫，只比對兩棵正規化的樹;
  `FidelityEngine` 是唯一進入點。外殼有三家:CLI(`Parity.Cli`)、本機報告 UI
  (`parity serve`，零建置 SPA 放 `src/Parity.Cli/wwwroot`)、網頁儀表板
  (`Parity.Server` + `web/`)。**引擎不知道外殼存在**。
- **鐵則:雲端不跑瀏覽器。** `Parity.Server` 唯一收掃描結果的入口是 `POST /api/runs`，收的是
  **已經跑完的報告**(其他寫入端點只管登入、邀請、成員、token 與刪除 run);不存在任何
  「給我 URL 我去掃」的端點。掃描永遠發生在本機／CI。
  這條鐵則的實體化就是 **`Parity.Server.csproj` 永遠不引用 Playwright**——不要為了任何
  方便把它加進去。而且不只它自己:伺服器整張專案參考圖(`Parity.Server` → `Parity.Server.Data`
  → `Parity.Report`)都不准碰到 `Parity.Engine` 或 Playwright。報告型別、`ReportWire`、
  `FidelityScore`、`DiffRecord`/`BaselineComparer` 因此住在**零套件相依的 `Parity.Report`**
  (命名空間仍是 `Parity.Engine`);伺服器要用引擎的新東西時,先問它是不是報告契約的一部分,
  是才搬進 `Parity.Report`,不是就不准用。`RepoInvariantsTests` 沿 ProjectReference 檢查。
- **報告契約單一來源:`Parity.Report/ReportWire.cs`。** CLI 落地、serve API、Server 解析
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
  **不要 commit**。也因此本機跑 demo 是 12/12 全配對，CI 上不是(ci.yml 的計數斷言以 CI 的 11/12 為準)。
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

**只走 env 還不夠**(2026-10-22 修 #4 時踩到,審查實證):env 擋得住 shell 注入,擋不住**換行**。
`$GITHUB_OUTPUT`/`$GITHUB_ENV`/`$GITHUB_PATH` 是一行一筆,值裡帶 `\n` 就多出新的一筆——曾經可以先寫
`exit=0`、再用 heredoc 吞掉真正的 `exit=…`,讓 Enforce gate 讀到 0,還能指定上傳 runner 上的任意檔。
因此:
- 寫進這些檔案的值**不能來自 input**;目前只准 exit code(數字或常數 `exit=2`)與 `$HOME/.dotnet/tools`
  (`ActionYmlTests` 掃所有非註解行把關)。
- **所有可能被印出來的 input 都要擋換行**:工具的錯誤訊息常把參數原樣印出(parity 印 config/target、
  `dotnet tool install` 印 version),換行後的內容落在行首就成了 runner 會解析的 workflow command
  (`::error …`、`::add-mask::`)。現在 version、config、target 帶 `\r`/`\n` 都在交給工具之前就拒絕,
  拒絕訊息本身也不印 input。新增 input 時照做。
- **上傳路徑不能來自 step output**:check 步驟把報告複製到 `$RUNNER_TEMP/parity-artifact/`,Upload 只認那個固定目錄。
- **「關掉某件事」的 input 不能直接映射成 CLI 讀的環境變數**(2.1.0 的 `share-stats`):step 的 `env:` 會蓋過使用者在
  workflow 層設的同名變數,`PARITY_SHARE_STATS: ${{ inputs.share-stats }}` 會把使用者的 `PARITY_SHARE_STATS=false`
  蓋回預設的 `true`(=不表態)而照送。input 先進另一個名字(`PARITY_SHARE_STATS_INPUT`),只在要關的時候才 `export`
  (`ActionYmlTests` 實跑 bash 驗這件事)。
- **複製一個 repo 控制得到的路徑要防 symlink**:repo 可以 commit `.parity/report.json → /proc/self/environ`,
  `cp` 會把這一步的環境(含 `FIGMA_TOKEN`)複製進 artifact。只收一般檔案、檔案與所在目錄都不能是 symlink。

### 其他

- `ImageSharp` 的 major 升級被 dependabot **明確 ignore**:4.0 把授權變成建置期強制，
  沒有授權檔就 CI 全紅(實測於 #19)。3.1.x 仍是 Split License、開源免費。
- ~~`Parity.Server` 的 `create-invite` 印出的邀請連結**硬寫 `http://127.0.0.1:4322`**。~~
  **已改(2026-10-26)**:管理指令與 web 端的邀請都先看 `Parity:PublicUrl`,沒設才退回本機開發位址/請求的 Host。
- **合併伺服器(2026-10-26):一台收 parity 與 XamlContrast 兩種報告。** `POST /api/runs` 依 `X-Parity-Tool` 分流
  (沒帶 = parity),`Run.Tool` 記是哪一種。**任何讀 run 的查詢都要先想「這裡該不該分工具」**:parity 的總覽、趨勢、
  「跟上次比」都限定 `Tool == parity`——同分支夾著一筆 XamlContrast 的 run 時,拿它的原文當 parity 報告解會炸
  (`ServerContrastEndpointTests` 釘住)。XamlContrast 的報告型別是 `ContrastIngest` 裡照它的
  `docs/report.schema.json`(schemaVersion 6)手寫的最小鏡像,**不引用 XamlContrast 的組件**(它沒發佈成函式庫,
  而且伺服器的參考圖只准到 `Parity.Report`);契約的對照組是 `tests/Parity.Tests/Golden/xamlcontrast.v6.json`
  (XamlContrast 對自己的 demo 實跑的輸出)——那邊的 JSON 加欄位不影響,改名或升 schemaVersion 要回來改。
  XamlContrast 的「跟上次比」在 `ContrastCompare`:比法照它的 `Baseline.Compare`(鍵 `file|element|fg|bg`、0.01 的容差),
  同樣是手刻的鏡像——**XamlContrast 遞增 `keyVersion`(改了產生鍵的方式)時這裡要一起看**。`/changes` 依 run 的工具
  回兩種形狀(parity 的 `RunChanges`、XamlContrast 的 `ContrastChanges`),前端照 run 的 tool 讀。
- **部署相關的設定鍵**(`ServerSettings`):`Parity:PublicUrl`、`Parity:KeysDirectory`;容器映像預設
  `PARITY_SERVER_DB=/data/parity-server.db`、`Parity__KeysDirectory=/data/keys`。本機沒有 Docker,`Dockerfile`
  只在 CI 的 `docker-image` job 真的被建過——改 Dockerfile 或伺服器的專案結構時,推上去看那個 job。
  沒指定任何監聽位址時預設 `127.0.0.1:4322`,**`ASPNETCORE_HTTP_PORTS` 也算有指定**(官方映像預設帶它,
  只看 `urls` 的話容器裡會靜靜聽在 loopback)。
- `parity serve` 只綁 127.0.0.1，另外還擋 Host header(DNS rebinding)與 POST 的
  Origin(CSRF)。報告含站點結構與截圖——**不要為了「方便同事看」改成 0.0.0.0**;
  要給人看就是走 `parity push` 到儀表板。

## 技術債與留帳

- **相依漏洞:2026-10-22 實掃只剩 `SixLabors.ImageSharp` 3.1.12 一個套件**(`dotnet list Parity.slnx
  package --vulnerable --include-transitive`;2026-08-26 那次是 0 個,這五個 advisory 是 2026-10-07 才公布的)。
  `SQLitePCLRaw` 手動 pin 到 3.0.5 蓋掉 EF Sqlite 傳遞進來的 NU1903 版本,dependabot 每月盯著。
  - **ImageSharp:2.0.0 維持 3.1.12,不升 4.x**(使用者 2026-10-23 決定:加 `SkipMetadata` 關掉 ICC 路徑,
    升 4.x 之後再評估)。五個 GHSA(高:j3p4-wp97-rph4、j9gm-c75j-xc9q、jjfr-hcj7-qf5w;中:wmxv-xphr-5c9g、
    gwg2-r3hj-4w44)的修補版都只有 **4.1.2**,3.1 線沒有修補版(nuget.org 上最後一版就是 3.1.12);
    **4.x 建置時需要 Six Labors 的授權金鑰**(上面「地雷 → 其他」那條;#19 與 dependabot PR #54 都因此全紅)。
    PR #54 與 `dependabot.yml` 都先不動,等升 4.x 的決定。
  - **逐條暴露面**(Parity 只在 `designImage` 模式解碼一張圖,`ImageDesignSource.LoadPixelsAsync`;
    不做任何影像處理、不編碼):
    - j3p4-wp97-rph4(高,HistogramEqualization 的直方圖索引越界):要對解碼出的 float TIFF 呼叫
      `HistogramEqualization()`。Parity 沒有用任何影像處理 → **碰不到**
    - j9gm-c75j-xc9q、jjfr-hcj7-qf5w(高,TIFF CCITT T4／T6 **編碼器**越界寫):要用 `TiffEncoder` 編 1-bit
      CCITT 壓縮的 TIFF。Parity 不編任何圖 → **碰不到**
    - wmxv-xphr-5c9g(中,BigTIFF 的 IFD 讓解碼卡住):要解碼 TIFF。2.0.0 起只註冊 PNG/JPEG 解碼器,副檔名
      是 .png 的 TIFF 也擋得住(判格式看內容)→ **碰不到**
    - gwg2-r3hj-4w44(中,ICC CLUT 依未驗證的尺寸配置記憶體):PNG(iCCP)與 JPEG(APP2)都能內嵌 ICC。
      2026-10-23 照 advisory 的形狀手刻一份 mpet／clut 截斷的 ICC 實測:3.1.12 **解碼本身不會走到**——解碼只
      解析 128 位元組的檔頭,CLUT 要到讀 `IccProfile.Entries` 才配置(實測 64 MB),advisory 對 1.x～3.x 也是
      這樣寫;Parity 從來不讀中繼資料,所以原本就沒有觸發。2.0.0 再把路關掉:`SkipMetadata = true`(JPEG 不再
      保留 ICC／EXIF),而 **3.1.12 的 PNG 解碼器不理 SkipMetadata、iCCP 照收**,所以 `LoadPixelsAsync` 解碼後
      再把 `IccProfile` 設成 null → **碰不到**。`ImageDesignSourceTests` 鎖住兩件事:改動前後解碼出的像素雜湊
      相同(ICC、Adobe APP14 的 RGB／CMYK JPEG、調色盤 tRNS、EXIF 共 6 種 fixture),解碼後沒有 ICC／EXIF。
  - **重新評估的時機**:決定升 4.x(取得授權金鑰)時,或 3.1 線出修補版時。要加任何讀圖片中繼資料、做影像
    處理或編碼的程式碼之前,先回來看這張表——上面的「碰不到」都建立在「只解碼、只取像素」上。
- **auto-name 通用名假配對**(ROADMAP 有完整留帳):「Content」這種通用圖層名會撞到
  無關頁面的 `#content`。尺寸合理性防線(面積比 >16 拒配)只擋得住量級差很多的;
  **`auto-container`(LCA 推論)不經這道防線**。要根治得上語意層，動工前先蒐集更多野生樣本。
- `Parity.Server.Data` 目前是 SQLite。**2026-10-26 決定部署也維持 SQLite、單一實例**(備份是一個檔、
  登入序列化只在單一行程內剛好成立、migrations 不必重生);要多實例時再換供應商(PostgreSQL 之類),
  屆時 migrations 重生、登入序列化改到 db 層——已知的一次性成本。

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
