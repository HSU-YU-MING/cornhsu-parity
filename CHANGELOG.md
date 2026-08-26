# Changelog

版本規則:1.0.0 起依語意化版本與「演進條款」(見 1.0.0 節);0.x 期間為新功能升 minor、修正升 patch。

## 未發佈

**網頁外殼動工(M1:報告進得來)**——引擎/外殼分離的第二家外殼:一個只收報告、
只給人看的團隊儀表板。**鐵則:雲端不跑瀏覽器**——伺服器唯一的寫入口收「已完成的報告」,
不存在任何「給我 URL 我去掃」的入口;掃描永遠發生在本機/CI。

- **新增:`parity push` 子指令**(CLI 面加法 = minor)。把既有的 report.json 送上
  Parity.Server:token 走 `PARITY_TOKEN` 環境變數(CI secret;`--token` 旗標僅供本機
  試用,旗標值會進 shell 歷史與 CI log)、`--server` 或 `PARITY_SERVER`;
  commit/branch 自動吃 GitHub Actions 環境(`GITHUB_SHA`/`GITHUB_REF_NAME`)。
  送出前在本機先驗報告信封,壞檔案不浪費往返。
- **新增:網頁外殼 M4——總覽卡與趨勢圖(刻意提前到 M3 之前:分數沒有趨勢就沒有語境)**。
  `/api/overview`(專案×route:最新分、上次分、方向)+ `/api/trend`(單頁時間序);
  首頁總覽 stat tiles,點卡展開趨勢折線(單序列免圖例、y 固定 0–100、FAIL 點狀態色
  + tooltip 文字併述、末點直標;色盤過 dataviz 六檢)。
- **修正:報告儲存的真實帳(M4 實測改寫規畫前提)**。21 頁站一次 push 的 report.json
  = 10.3MB,不是「很小」——(1) 原文 gzip 入庫(~16:1);(2) 逐條落差**不再進關聯表**
  (實測 36,058 列/9.95MB 且零讀者;詳情端點改從原文 blob 重建,總覽/趨勢只讀
  PageResult)。一次 push 的儲存增量 9.87MB → **0.65MB**。伺服器未發佈,dev 資料庫
  重推即遷移。
- **新增:非 localhost 裸奔守門**。讀取端點在 M3 之前沒有認證,唯一防線是只聽本機——
  綁了對外位址就拒絕啟動並講清楚(自擔風險的明示出口:`PARITY_SERVER_ALLOW_REMOTE=1`)。
- **新增:網頁外殼 M2——落差詳情頁(取代 PPT 那一頁的核心畫面)**。`web/`(React +
  Vite + TS,建置產物落 `wwwroot`,伺服器偵測到就改served、沒建置退回 M1 陽春頁)。
  **「在哪裡」不靠截圖**:report.json 本來就含每個節點的量測座標,詳情頁直接把座標
  畫成工程藍圖(SVG 線框,實線 = 實作量到的框、顏色 = 嚴重度),點一條落差就拉出
  **尺寸標註線**(`paddingRight 20 → 8px` 這種工程圖講尺寸的方式)並虛線疊出設計期望的框
  ——零截圖上傳、零儲存成本,「純數值版先行」的拍板就這樣兌現。
  嚴重度過濾、soft 開關、多頁籤(21 頁的 dogfooding 報告實測:328 張落差卡順跑);
  新端點 `GET /api/runs/{id}/report` 回報告原文,UI 吃 CLI 寫出的同一份契約。
- **新增:`Parity.Server` + `Parity.Server.Data`**(不上 NuGet,部署用)。
  `POST /api/runs`(Bearer 專案 token,只存 SHA-256)收報告落庫(SQLite + EF migrations
  從第一天),`GET /api/runs`、`/runs/{id}` 與最陽春的瀏覽頁;`create-project` 指令
  發 token(只印一次)。M1 綁 127.0.0.1(帳號權限是 M3、對外部署是 M5)。
  報告解析走引擎的 `ReportDocument`/`ReportWire`(信封與序列化設定自 CLI 移入引擎,
  wire 格式逐位元不變)——契約單一來源,伺服器不自己抄一份。
  E2E:離線 demo `check` → `push` → 網頁逐條看得到落差(75/100、6 條 diff、metadata)。

介面凍結後的第一個 minor(演進條款第 2 級:量測/配對語意演進,無介面變更)。

- **新增:純字母隨機 id 的連拍實測偵測(F2 的最後一塊)**。`parity snapshot` 現在載入
  頁面兩次,只出現在單邊的 id = 每次載入重新生成的隨機 id(`rkxvdnnzty` 這種純字母亂碼,
  0.13.0 的字元啟發法認不出、實測認得出,且零誤判——穩定 id 兩次都在,絕不誤標)。
  測到隨機 id 時:以「實測穩定的 id 白名單」重拍(錨點只用名單內的 id,其餘走結構路徑),
  並把白名單存進快照信封(選填欄位 `stableIdAnchors`);`parity check` 擷取現場頁面時
  用同一份名單,兩邊 selector 生成規則一致,配對才成立。
  - 相容性:沒有隨機 id 的站,快照**連 key 都不多一個**、行為與 1.0 完全相同;
    舊快照(無此欄)照現行行為讀。演進條款第 2 級(量測語意演進)= minor。
  - 代價:`parity snapshot` 每個 target 多載入一次頁面(有隨機 id 的站再多一次重拍);
    check 不變。
  - 邊界誠實列:動態出現/消失的元素(隨機顯示的公告)其 id 會被白名單漏收,
    該元素改走結構路徑——錨點次穩,但不會誤配。
- **修正:參考截圖一律來自「被凍結的那一拍」**。截圖字典後拍覆寫先拍,連拍偵測的
  探測擷取與 `--stabilize` 的連拍原本會把截圖覆寫成探測那一拍——凍結的樹與參考截圖
  來自不同次載入,serve UI 的疊框會對不齊(`--stabilize` 自 0.13.0 起即有此縫,
  這次一併補上)。探測性擷取不再拍截圖(`ImplRef.CaptureScreenshot`,引擎 API 加法)。
- **修正:隨機 id 警告訊息的計數語意**。原寫「N 個 id 每次載入重新生成」,但 N 數的是
  兩次載入的單邊 id 字串總數(2 個隨機元素會報 4),照字面去頁上找 N 個元素會找不到
  ——改為「N 個 id 字串只出現在兩次載入的其中一次」,數字與語意一致。

## 1.0.0

**介面凍結宣告。** 本版不新增功能——它是一個承諾:**以下六個契約面自此凍結,
破壞任何一面即升 major**。逐項審查與決定過程見
`docs/Parity 1.0 介面凍結審查.md`(2026-07-22 盤點,2026-08-11 六面全數拍板)。

1. **`parity.config.json` schema**——全部欄位與有效值凍結,**含容差預設值**
   (sizePx 2 / spacingPx 2 / colorDeltaE 2.0 / fontSizePx 0.5 / positionPx 4)。
2. **CLI 介面**——子指令、旗標(space-only 格式)、**exit code 契約**:
   `0` 通過 / `1` 落差超 gate / `2` 執行錯誤 / `3` 配對可信度不足。
3. **GitHub Action inputs**——kebab-case 名稱與預設值;**新增選填 input = minor,不算破壞**。
4. **`report.json`**——`schemaVersion` 信封;`severity`(`none/minor/medium/serious/critical`)
   與 `status`(`mismatch/missing`)為**封閉集**(新增成員或改拼寫 = major);
   `unmatched[].reason` 與 `nodes[].matchedBy` 為**開放集合**(消費端必須容忍未知值、
   已知值拼寫不變;當前字彙表由 README「報告字彙表」一節維護)。
5. **baseline SQLite schema**——EF Core migrations 為正式演進路徑;
   舊 db(含 `EnsureCreated` 時代)開啟時自動接管。
6. **`parity.snapshot.json`**——`schemaVersion` 信封;**1.x 內新版永遠讀得動舊檔**;
   量測/selector 語意演進使舊基準過時時,工具必須明確提示重拍(不得靜默誤報)。

**演進條款**(量測演進的版本語意,自本版起為承諾):

1. 上述六面的介面破壞 → **major**
2. 量測語意演進(量得更準、配對規則變化,可能需重拍基準)→ **minor**,
   CHANGELOG 開頭顯著標示「升級必做」(0.11.0 格式)
3. 預設值調整(容差、gate 預設)→ **minor** + 顯著標示
4. 行為完全不變的修正 → **patch**
5. **minor 若需要重拍基準,工具必須大聲失敗並給指引**(如 `randomized-id` 提示、
   配對可信度 exit 3)——不得靜默給錯誤結果

**GitHub Action 自本版起提供移動式 `@v1` tag**(跟隨最新 1.x;之後每個 1.x 發版把 `v1` 前移)。

### 隨版收錄(0.14.0 之後的變更)

路線 B 補測(2026-08-26,Wikimedia Codex;完整發現見
`docs/野生實查-2026-08-11-路線B-figma方言.md` 補測一節)的直接產物:

- **修正:`parity.map.json` 與 config 同樣允許 `//` 註解與尾逗號**(B9)。原本 config
  允許、map 不允許——兩個檔並排放,照 config 的習慣在 map 檔寫註解會直接炸 JSON 解析錯。
  注意:`parity map` 儲存時整檔重寫,手寫註解會消失(註解適合純手寫維護的 map 檔)。
- **修正:Figma API 403 的錯誤訊息把「token 過期」列為候選原因**(B10)。Figma 個人
  token 建立時就選定效期,過期後回 403,樣子跟 scope 不足/檔案指錯一模一樣——原訊息
  只指向 scope/fileKey,野外實踩查了半天才找到真因。
- **README 補「Figma 檔怎麼挑 frame」指引**(B2/B9 承諾的文件債):挑 screen-level 不挑
  變體總表、kit 佔位字直接上 map 檔、免費方案額度是「天」級的三個實測教訓,兩版同步。

## 0.14.0

**1.0 介面凍結審查收官**:六面全數拍板(含 2026-08-11 補盤的面 6 與演進條款),
唯餘發版當下的機械動作。本節的變更是拍板的直接產物:

- **新增:`parity.snapshot.json` 頂層 `schemaVersion` 信封**(審查面 6,對齊 report.json
  的 0.10.0 決定)。新檔為 `{ "schemaVersion": 1, "root": {...} }`;**無版本欄的舊檔
  (含手寫 design JSON)視為 v1 照吃,零破壞**;讀到比自己新的版本給明確錯誤,不靜默失敗。
- **README 補「報告字彙表」**(審查面 4 開放集合決定的配套義務):severity/status 為
  封閉集,reason/matchedBy 為開放集合(解析端容忍未知值),當前值一覽。

以下四項是 1.0 前的文件與供應鏈收尾,不影響既有介面:

- **修正:`parity version` 在原始碼建置下自報錯誤版號。** csproj 的 `<Version>` 寫死
  `0.13.0` 而 tag 已 v0.13.1,任何非 CI 的建置都會自稱 0.13.0。改為固定佔位值
  `0.0.0-dev`(版號的唯一真相源是 tag,release 以 `-p:Version=` 覆蓋),並讓 `version`
  改印 InformationalVersion:本機建置顯示 `0.0.0-dev+db22749`,發行版顯示
  `0.13.1+db22749`——bug 回報直接帶著 commit。
- **變更(輸出格式):`parity version` 現在會多帶一段 `+<commit>`。**
  `parity 0.13.1` → `parity 0.13.1+db22749`。commit 編號由 SourceLink 在建置時填入,
  截為 7 碼。**若你有腳本在 parse 這一行,請改成取 `+` 之前的部分。**
  CLI 輸出是 1.0 要凍結的介面之一,所以刻意在 1.0 之前調整;1.0 之後再動就得升 major。
- **新增:`scripts/verify-readme-facts.ps1`(CI 每次跑)。** 兩份 README 的測試條數、
  累計發佈版數、Action 的 `@v` pin 對著實際情況與 CHANGELOG 比,不一致就紅;
  `-Update` 一鍵重貼。**首度執行就抓到測試條數寫 195、實際 197。**
  `release.yml` 另加一道「tag 必須等於 CHANGELOG 頂端版本」的把關。
- **新增:`SECURITY.md`。** 私下通報管道(GitHub private advisory)、0.x 只支援最新版的
  支援範圍、in-scope/不算漏洞的界線,以及給使用者的加固建議(pin Action 版本、
  Figma token 只給 `file_content:read`、別把 Action inputs 接到不可信文字)。
- **強化:`action.yml` 不再把 `inputs` 直接內插進 `run:`。** `config` / `target` /
  `version` 等一律改走 `env:` 再以 shell 變數引用;呼叫端若把 input 接到 PR 標題、
  issue 內文這類不可信來源,原本會變成 runner 上的指令。行為不變,介面不變。

## 0.13.1

- **修正:0.6.0 之前建的 baseline db 開啟即炸**(`no such column: s.Score`)。0.10.0 的
  EF migration 自動接管假設「legacy schema 恰好等於 InitialCreate」——只對 0.6.0+ 成立,
  更早的 db 連 Score 欄都沒有(0.6.0 是手動 ALTER 加的);標完 InitialCreate 後 EF 認定
  schema 已最新,缺欄永遠不會補。接管前先檢查並補上歷史缺欄。
  **v0.13.0 的三通路發佈驗證抓到的**:parity-action-test 的 db 是 0.2.0 建的——又一個
  自家樣本(全部 0.6+)量不出的盲區;既有的接管測試用「現在的 model」EnsureCreated,
  天生就有 Score,所以也抓不到。新增用 raw SQL 重現 0.2.0 schema 的回歸測試。

## 0.13.0

**首輪野生實查的產物**(對照 XamlContrast 的外部專案實查;方法與完整發現見
`docs/野生實查-2026-08-10-路線A-snapshot.md`):8 個公開網站 × snapshot 模式,
6 個發現全部是自家 dogfooding 樣本(同作者、cornhsu.com 21 頁)量不出來的。

- **修正:深 DOM 讓 snapshot 落地/回讀 crash**(F1)。Wikipedia 條目頁(約 31 層巢狀)
  擷取成功、序列化炸掉——擷取端早已為此放寬 `MaxDepth`,但落地(`ReportJson`)與回讀
  (`JsonDesignSource`)沒跟上。兩處補齊 512,同一個教訓修完整。
- **修正:隨機 id 根治**(F2)。React `useId` / CSS-in-JS 的隨機 id 每次載入重新生成,
  拿它當 selector 錨點 = snapshot 凍住的路徑下次載入必失效。擷取端偵測高熵 id、
  改走結構路徑(nth-of-type),路徑跨載入穩定——MDN 實測未配對 48 → **0**。
  舊 snapshot 若凍有隨機 id selector,該節點列為 `randomized-id` 並提示重跑一次
  `parity snapshot` 完成遷移(那些節點在舊版本來就永遠配不到,遷移只會變好)。
  純字母隨機字串(rkxvdnnzty)仍分不出來,但結構路徑讓它不再是問題。
- **修正:SVG 當葉子**(F3)。內部繪圖指令(path/g/defs)沒有 padding/字體語意,動畫下
  nth-of-type 又不穩(tailwindcss.com 實測 44 個 unmatched、兩次配對數不同)。svg 本身
  照量,內部不展開。既有 snapshot 若凍有 SVG 內部節點,重拍一次即可對齊。
- **新增:`parity snapshot --stabilize`**(F4)。連拍 3 次、比對擷取樹,列出「會動」的
  區域(廣告輪播、動畫、lazy 媒體;收攏到最高的不穩定祖先),並給可直接貼進 config 的
  `ignore` 建議——MDN 實測一次抓出 79 個不穩定區域。
- **修正:auto-name 假配對的尺寸合理性防線**(路線 B 實查 B3)。通用圖層名(Content/
  Button)會在無關頁面撞到同名元素,產生 critical 級荒謬落差。實測數據:假配對面積比
  148×~703×,正當配對全部 ≈1×——面積比 >16(雙軸各 4×)拒當配對,理由列
  `size-implausible`。已知極限:同尺寸撞名(16×16 圖示對 16×16 圖示)幾何分不出來。
- **修正:Figma API 429 的錯誤訊息(兩層)**。被限流時原訊息提示「查 scope/fileKey」,
  把人帶去錯的方向;而且免費方案的方案級額度一撞就是「天」(實測 Retry-After 367422 秒
  ≈ 4.3 天),說「等一下」也是誤導——現在把 `Retry-After` 讀出來換算成分/時/天講,
  並提醒已抓過的 frame 走 `.parity/cache` 不受影響。
- **修正:超深頁面的錯誤訊息講人話**(F1 殘留)。超過 512 層 JSON 深度時,原生訊息是
  誤導的「object cycle」——snapshot 落地與 designFile 回讀兩處都包成「DOM 巢狀超過
  支援上限(約 250 層),用 ignore 修剪最深的區域」。
- **修正:分數不再四捨五入進成 100**(F5)。99.5% 忠實被進位成 100,「100/100」與
  GATE FAIL 同框(Stripe 實測 3306/3322)。滿分保留給「全部忠實」,差一個節點就是 99。

### Onboarding(DX 實查 D1/D6)

從 NuGet 實裝 0.12.0 以「第一次接觸的開發者」身分走完整旅程後的兩個修正
(方法與完整觀察見 `docs/野生實查-2026-08-11-DX開發者視角.md`):

- **`parity init` 範本與完成訊息並列兩條路**(D1)。原範本是 Figma-first,但工具
  「五秒內第一次綠燈」的最快路徑是 snapshot 模式(不需要 Figma)——沒有 Figma 檔的人
  在第一分鐘就被範本勸退。範本以註解附上 snapshot 替代寫法(設定檔解析本就吃註解),
  完成訊息分「有 Figma / 沒有 Figma」兩條路。
- **frame 找不到的錯誤訊息把人接住**(D6)。「init 範本的 frame "10:2" × snapshot 基準」
  是新手必撞組合,原訊息只說找不到——現在列出設計 JSON 裡實際存在的 id,並講明
  snapshot 的慣例:frame id 就是 target 的 route(例如 "/")。

## 0.12.0

**對外語言統一為英文。** 引擎行為、報告 schema、exit code、CLI 介面(指令與旗標名稱)全部不變 ——
這一版只換「使用者讀到的字」。之所以升 minor 而非 patch:輸出文字是有人在 grep 的東西
(CI log 判斷、PR 留言樣板),換語言對他們是破壞性的。

- **CLI 全面英文化**:`--help`、所有子指令用法、錯誤與例外訊息、`check` / `lint` / `snapshot` /
  `baseline` / `serve` 的執行輸出。動機:本套件掛在 GitHub Marketplace、npm 與 NuGet 上,
  介紹文字與關鍵字都是英文,但裝起來之後 CI log 與錯誤訊息全是中文 —— 非中文使用者
  裝得起來卻讀不懂。
- **貼上 PR 的 Markdown 報告英文化**:標題、還原度摘要行、落差表欄位(Element / Property /
  Expected → Actual / Severity / Suggested fix)、相對基準的變化清單、未配對區塊。
- **`parity serve` 的報告 UI 英文化**(含 `parity map` 的互動配對流程),`<html lang>` 改為 `en`。
- **`action.yml` 的 description 與 8 個 input 說明英文化** —— 這些直接顯示在 GitHub Marketplace
  頁面與編輯器的 workflow 補齊上。
- **NuGet 套件 description 英文化**;npm 平台包的 description 一併(原本是中文,而主套件是英文)。
- **README 改為英文為主、中文並存**(`README.md` / `README.zh-Hant.md`,互相連結)。
  `README.md` 會同時被打包進 nupkg 與 npm 主套件 —— 原本 nuget.org、npmjs.com 與
  GitHub Marketplace 上的說明頁全是中文,但關鍵字與套件描述是英文。

### 其他

- **移除 `bootstrap-npm-arm64.yml`**:它是 0.10.0 為了首發兩個新的 arm64 平台包而寫的一次性
  workflow(npm 的 OIDC 信任發布無法建立全新套件,只能更新既有的),檔頭就寫明「用完即可刪」。
  平台包早已存在並改由 OIDC 接手,workflow 與其依賴的 `NPM_TOKEN` secret 都該收掉 ——
  留著等於讓「repo 內零長效金鑰」這件事破一個洞。
  **注意:secret 本身要另外到 repo settings 手動刪除。**
- **CI 補三件事**(對齊 PolyMigrate 的 CI 形狀):`dotnet format` 排版守門、
  ubuntu/windows/macOS 三平台的建置與測試矩陣、以及 **pack → `dotnet tool install` → 實跑**
  的煙霧測試。最後一項針對的是 0.9.1 的真實事故:在 ubuntu 上 pack 成功、Windows 使用者
  裝了直接 Driver not found —— 只在單一平台建置永遠看不出這種事。
- **新增 `.editorconfig`**(排版慣例的單一來源)與 **`.github/dependabot.yml`**(nuget +
  github-actions)。dependabot 對這個 repo 特別有意義:`Parity.Storage.csproj` 手工把
  `SQLitePCLRaw` 釘在 3.0.3 以蓋掉 EF Sqlite 傳遞進來的 NU1903 高風險版本,那個判斷
  現在完全靠人記得回頭看。
- **ROADMAP 清掉一條已解的殘留**:「EF `EnsureCreated` 無 migration」仍列在已知盲點,
  但 0.10.0 已改走正式 EF migrations。
- 全 repo 套一次 `dotnet format`(純空白排版,無行為變更)。

## 0.11.1

**0.11.0 的修法不完整,這版才真的修好。**如果你已經升到 0.11.0 並重拍過基準,升到這版**要再重拍一次**(0.11.0 拍的基準有機率是壞的)。

- **改用「撐高視窗」而非「捲過整頁」觸發進場效果**:0.11.0 的作法是快速捲過整頁再捲回原位。漏掉的關鍵是 **IntersectionObserver 回報的是「callback 送達當下」的相交狀態,不是「當時捲到那裡」**——捲太快,送達時元素已離開畫面 → 回報未相交 → class 永遠不會加上。捲得越快、機器越忙,漏得越多。

  中間還試過「每站停留到版面穩定才前進」,**仍然不夠**:元素還沒被觸發時版面本來就是靜止的,「穩定」的條件在 callback 送達前就已成立,等待形同虛設。實測約三分之一的擷取仍是壞的。

  現在不靠等待,改用**幾何**保證:量測前把視窗高度暫時撐到整頁高,所有元素同時落在畫面內,IntersectionObserver **必然**全部觸發;等版面吃下變化後還原視窗高再量。class 一旦加上就不會被移除,所以還原後元素仍在展開後的位置。撐高有 20000px 上限(防無限捲動頁面)。

  **attach 模式(Electron 活視窗)刻意跳過撐高**——改使用者的視窗大小會被看見,違反「不留痕跡」。

  共用的等待條件也改了:先無條件等 N 輪、再開始要求「連續數次簽章相同」。先前兩版都栽在「靜止 ≠ 已定案」,把最小等待寫進共用函式,避免同樣的思路再溜回來。

- **修正 0.11.0 的驗證方法缺陷**(這是當時會誤判的根本原因):0.11.0 的「12 輪全數通過」是**固定一份基準、反覆跑 check** 得到的——但拍快照本身也是一次擷取,也會輸掉同一場競速,那份證據只測了一半。本版改成**每輪重拍基準**再 check:10 輪、10 份獨立基準、14 次完整擷取全數通過。

## 0.11.0

**升級必做:重拍快照基準**(`parity snapshot`)。這版修正了「捲動觸發的進場效果」的量測,**用捲動進場動畫的站台,量測值會改變**——0.10.x 以前拍的基準不重拍會整批誤報。是修正而非新功能,但因為破壞既有基準的相容性,依 0.x 慣例升 minor 而不是 patch。

- **量測前讓版面定案,修掉進場動畫造成的 flaky 與系統性量錯**:凍結 `transition` 只保證元素**不再動**,不保證它停在**最終狀態**。捲動觸發的進場效果(IntersectionObserver 加 class,例如 `.reveal{transform:translateY(28px)}` → `.reveal.in{transform:none}`)其初始狀態本身就是位移的,凍結後就永遠停在動畫第 0 格。造成兩種錯:
  - **flaky**:首屏邊緣的元素,IO callback 有時趕得上擷取、有時趕不上——同一份 HTML 兩次量到差 28px
  - **系統性量錯**:首屏以下的元素 IO 永不觸發,一直量在未展開狀態,而非設計意圖的版面

  修法(`SettleScript`):凍結之後,以視窗高為步距**走過整頁**叫醒所有 IntersectionObserver、捲回原位(attach 模式不留痕跡),再**等版面簽章連續兩次相同**才擷取——順便涵蓋字型換置等其他「晚一步」的版面變動。動畫已凍結,所以 class 一加上去版面瞬間到位,不必等 transition 的時間。腳本自身有步數(200)與幀數(40)上限,不會把擷取拖住;實測 21 頁的真實網站用到 2–16 步、2–7 幀,離上限很遠。

  dogfooding 實證(cornhsu.com,21 頁):**修正前 6 輪中 3 輪 GATE FAIL,修正後 12 輪全數通過**。差值正好是該站 `.reveal` 的 translateY(28px),全站 25 個節點的量測值因此改變(方向一致:改為已展開的位置)。

  這與 0.9.1「量測時凍結動畫/轉場」是同一類問題的下一層:那次解決的是「動畫還在動」,這次解決的是「動畫根本沒被觸發」。

- **新增 `SettleCaptureTests`**:目標元素放在首屏之外,IntersectionObserver 根本不會觸發,所以**不靠時序賭**——退掉修正時穩定失敗(量到 2000+37)。這是 repo 內唯一需要真實瀏覽器的測試,沒裝 Chromium 時印訊息略過;CI 的 `install-browser` 已改排在單元測試之前,確保它在 CI 上真的會跑(靜靜略過等於沒驗)。測試 159 → 160 條。

## 0.10.1

純文件版,引擎與 CLI 行為不變。之所以要為文件發一版:README 會**打包進兩個通路**(NuGet 的 `PackageReadmeFile`、npm 主套件由 `prepare.mjs` 複製一份),套件頁顯示的是**打包當下那份**——修在 repo 裡不發版,nuget.org 與 npmjs.com 就一直停在舊數字(前例:0.9.4)。

- **成果表數字補到現況**:測試 154 → **159 條**(實跑確認);NuGet 共 13 版(→ v0.9.3)→ **共 19 版**(→ v0.10.1,含本版)。154 是 0.9.5 更新的,之後幾版加了測試沒同步。
- **npm 平台從 4 個補成 6 個**:0.9.x 加了 win32-arm64 / linux-arm64,安裝說明沒跟上,漏講了兩個平台。同一批漏改的還有 `prepare.mjs` 檔頭註解(「五個資料夾」→ 七個)與 `npm/cornhsu-parity/package.json` 的 `optionalDependencies`(只列 4 個平台;發布時 `prepare.mjs` 會用實際建出的平台整份覆寫,不影響已發佈的包,但當範本讀會誤導)。
- **README 兩處 action ref 更新為 `@v0.10.1`**。
- **`Parity.Cli.csproj` 的 `<Version>` 跟上**(原停在 0.9.3):release 一律以 tag 版號 `-p:Version` 覆蓋,發出去的包版號本來就對,但**本機建置**的 `parity --version` 會報錯的版本。順手加註解說明這個值只是本機預設,免得下次有人以為改它會影響發布。

## 0.10.0

1.0 介面凍結前的清理——這些是**破壞性變更**,刻意趁 0.x(改介面免費)一次做掉;1.0 後同樣的改動要升 major。動機:報告與 baseline 資料庫即將被「另一個獨立升版的讀者」(規畫中的網頁儀表板)與別人的 CI 依賴,單一使用者 / 單一版本時看不出的接縫,現在先補平。

- **`report.json` 加頂層 `schemaVersion`**:原本是裸陣列,無從辨識格式版本。改為 `{ "schemaVersion": 1, "reports": [...] }`,未來格式演進時消費端(`parity report` / 未來的 `parity push` 伺服器)有據可判、能相容處理。讀到無法辨識的舊格式給明確訊息,不靜默失敗。
- **報告裡的框改用 `width`/`height`(原 `w`/`h`)**:報告是跨工具契約,自我解釋的欄位名勝過縮寫(C# 內部維持 `.W`/`.H`,只改 wire format)。**讀時向後相容**:`BoxJsonConverter` 寫出 `width`/`height`,但讀時 `w`/`h` 舊檔照吃——升級後新工具讀得動 0.9.x 產的舊快照 / 報告,**不必重拍**(反向:0.9.7 讀不了新檔,無解,那支已發佈)。
- **null 欄位改顯式輸出**:`unit` / `delta` 等為 null 時原本整個 key 消失,消費端得判斷 key 存不存在。改為一律輸出 `null`,契約少一個「有時消失的 key」的坑。
- **exit code 拆分 gate / 可信度**:配對可信度不足(結果不可信,通常是 url/frame 設定錯)從 gate fail 分出來,回獨立的 `3`(落差超門檻仍是 `1`、執行錯誤仍是 `2`)。CI 分得清該修設定還是修實作。
- **量測還原頁面 `transform: scale`**(規畫書 4.6 的已知盲點):祖先的 `scale()` 會讓 `getBoundingClientRect` 量到縮放後的幾何,與設計對不上。擷取時累積祖先縮放係數、把 box 除回版面座標系(padding 等 computed style 不受 transform 影響,不動)。無 transform 時輸出逐位元不變(零回歸);實測 scale(0.5) 頁對未縮放基準 100/100 PASS。
- **baseline schema 演進改走 EF migrations**:原本靠 `EnsureCreated` + 土砲 `ALTER`,對已 commit 進 repo 的舊 db 無法演進。改為正式 migration;**既有(EnsureCreated 建、無遷移史)的 db 首次開啟時自動接管**(先標記 InitialCreate 為已套用再 Migrate),不因「表已存在」而爆。`Microsoft.EntityFrameworkCore.Design` 只在產新 migration 時暫時加回(常駐會帶入 NU1903 高風險的 `System.Security.Cryptography.Xml`),流程見 `Parity.Storage.csproj` 註解。

## 0.9.7

- **npm 發布步驟改為冪等**:同一版號若已在 npm 上就跳過,不再讓「重跑或補跑 workflow」因為 `npm publish` 對已存在版本報錯而整條紅掉。發布是可安全重試的動作。

## 0.9.6

- **npm 改用 OIDC 信任發布,移除長效 token**:發布憑證改由 GitHub Actions 的 OIDC 當場換取,不再於 Secrets 存放長期有效的 npm token——少一顆會外洩、要輪替的長效金鑰。

## 0.9.5

- **新增 npm 發布通路,支援 `npx cornhsu-parity`**:除了 `dotnet tool install`,沒有 .NET SDK 的使用者(前端/CI 常見)現在也能直接 `npx` 跑。主套件是啟動腳本,實際執行檔拆成 `parity-<platform>-<arch>` 平台子套件,靠 optionalDependencies 只裝當前平台那顆(自帶 .NET 執行環境,使用者機器免裝 SDK)。組裝邏輯抽到 [npm/prepare.mjs](npm/prepare.mjs) 而非塞進 workflow YAML:版號要同步五個 `package.json`、還要刪掉 Playwright 自帶的 node,寫成腳本才讀得懂、也能本機驗。
- **文件:定位主軸改以「能否自動化」為主**,比較對象換成真實同類工具;成果一覽測試條數更新 147 → 154。

## 0.9.4

- **文件:README 補上 NuGet / CI 徽章與作品集回連**,作品集連結改用無副檔名網址。純文件版,引擎與 CLI 行為不變。

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
