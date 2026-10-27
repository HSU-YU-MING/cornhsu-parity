# Roadmap / 未完成與已知盲點

「已完成」看 [README](README.md) 里程碑與 [CHANGELOG](CHANGELOG.md)。這裡只記**還沒做的、已知的缺口、與建議的優先序**。

## 里程碑層級

- ~~**M5 下半 — `ImageDesignSource`**~~ **完成(2026-07-18)**,且超出原案:除了「圖片+標註+像素取樣」(其他設計工具的萬用轉接頭),還加了 `parity snapshot`(把現況凍結成基準,重構守門,selector 身分配對)。**M5 全部完成,M1–M5 收官。**
- **M6 — 雲端「掃描」外殼:明文不做,決定不變**(2026-07-18 決定,除非出現真實使用者)。
  當年否決的是**「把 URL 交給雲端、由雲端跑瀏覽器去掃」**這件事,理由三條:設計 QA 是
  上線前的活,雲端只碰得到公開網址(最不重要的階段);「報告分享給非技術人」PR 留言 +
  Markdown 已覆蓋;架伺服器的成本(Docker/Chromium/**SSRF**/月費/維運)換不到新價值。
  **這三條到今天都還成立**,所以這一條保留原判,不是過期文字。
- **M7 — 團隊儀表板外殼(`Parity.Server` + `Parity.Server.Data` + `web/`):進行中**
  (2026-08-26 動工,尚未發佈)。**它跟被否決的 M6 不是同一件事,所以不牴觸上面那條決定。**
  差別只有一句話:**雲端不跑瀏覽器**。伺服器唯一收掃描結果的入口是 `POST /api/runs`
  (其他寫入端點只管登入、邀請、成員、token 與刪除 run),收的是
  `parity push` 送上來的**已完成報告**;不存在「給我 URL 我去掃」的端點,掃描永遠留在
  本機/CI。逐條對回當年否決的理由:
  - *只碰得到公開網址* → 不成立:掃描端仍是你的 CI 與本機,localhost、staging、
    登入後頁面照掃,雲端只負責存放與呈現結果。
  - *PR 留言已覆蓋* → 部分成立,缺的是**累積**:PR 留言是單次快照,沒有跨 commit 的
    趨勢、沒有跨頁面的總覽,PR 關掉就找不回來。M7 補的正是這一塊,不是重做留言。
  - *成本(Docker/Chromium/SSRF/月費/維運)* → 少掉最貴的兩項:**沒有 Chromium**
    (`Parity.Server.csproj` 永遠不引用 Playwright,這是結構性保證,不是自律),
    **沒有 SSRF 面**(沒有人交給它 URL 去抓)。剩下的是一般 Web 應用的維運成本。
  現況:內部 M1–M4.6 完成(收報告 / 落差詳情藍圖 / 總覽與趨勢 / 帳號角色邀請 /
  工作流結締組織 / 防禦層)。**2026-10-26 合併伺服器**:同一個 `POST /api/runs` 依 `X-Parity-Tool` 也收
  XamlContrast 的報告(統計功能規劃「一台、兩種報告」),run 帶 `Tool` 欄位,XamlContrast 有自己的總覽卡、
  fail/warn 趨勢與問題清單。**內部 M5 的部署前置也已完成**:`Dockerfile`(CI 的 `docker-image` job 實際建起來打
  `/healthz`)、`Parity:PublicUrl`(邀請連結)、`Parity:KeysDirectory`(登入金鑰持久化)、管理指令 `backup`。
  **資料庫維持 SQLite、單一實例**(2026-10-26 決定):單一實例剛好讓「登入序列化只在單一行程內」那條限制成立,
  備份就是一個檔,migrations 不必重生;要多實例時再換供應商。還沒做的是**真的上線**:選主機、網域(cornhsu.com
  子網域)、TLS、uptime 監測(打 `/healthz`)——這些是對外、會產生月費的動作,要維護者自己決定。
  M5 的待辦另有:**`parity push` 與伺服器協定正式納入凍結**
  (2.0.0 起標為實驗性,上線那一版在 CHANGELOG 寫明、拿掉 `--help` 的 `(experimental)`);**反向代理後面一定要設
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`**(零改碼;只在信任的代理後面開)——不設的話伺服器只看到代理的位址,
  任何一個人每分鐘打 20 次就讓所有人登不進去(驗證者實測);多實例部署時登入序列化要改成 db 層(見已知盲點的登入那一條)。
  **若哪天真的要做 M6,那是另一個決定,要重新過上面那三條理由——不能用 M7 已經上線
  當作理由順勢帶過。**
- **匿名使用統計(2.1.0,未發佈)**:`parity check` 預設送計數與分數區間(協定:stats-collector 的 `PROTOCOL.md`,
  與 XamlContrast 1.1 共用;收集站與儀表板刻意分開)。**研究倫理對「預設打開」的確認已取得(2026-10-28);發佈前收集站要先上線**
  (RELEASING.md「2.1.0」)。還沒做的、刻意留著的:
  - 審查若要求主動同意:改 `UsageStats.SendByDefault` 之外,還要做「第一次在終端機詢問」(協定 §3),目前沒有。
  - 「開發版不送」只認 `0.0.0-dev`(協定原文)。CI 的 `pack-smoke` 裝的 `0.0.0-ci`、RELEASING 本機乾跑的 `0.0.0-local.*`
    不算開發版——它們現在只跑 `--help`/`--version`,不會送;哪天拿它們跑 check,要嘛設 `DO_NOT_TRACK`,要嘛先改協定
    把「開發版」擴成 `0.0.0-*`。
  - `stats` 區段裡拼錯的 key(`"shrae": false`)跟整份設定檔一樣靜默忽略,所以拼錯的關閉開關不會生效;README 叫人用
    `--stats-preview` 確認。改成報錯是 major(面 1 的載入規則),印警告是 patch——等真的有人踩到再決定。
  - 論文的效度:資料有自我選擇偏差、CI 跑得多的專案佔大部分、沒有 ID 的多次執行合併不起來(規劃文件「論文相關的提醒」)。
- **WPF adapter(`WpfImplementationSource`,規畫書 4.5)**:動工時優先評估引用姊妹專案的
  `Cornhsu.XamlContrast.Core`(靜態解析「XAML 裡應該生效的顏色」,十三條規則、
  四專案雙實作互證)當**預期值產生器**,執行期讀值與之對照——不要重寫顏色解析。
  該套件**尚未發佈**,觸發條件(本 adapter 動工)成立時由 XamlContrast 端抽出,
  見其 ROADMAP「與 Parity 的交會點」(2026-07-31 兩邊同步記錄)。

## 其他設計工具的決定(2026-07-18)

- **Adobe XD:不做**——Adobe 已停止開發(維護模式、不再單售),幫死掉的工具寫 adapter 是負資產。
- **Sketch:先不做,門留著**——Mac 限定、市占萎縮;檔案格式可解析,有真實使用者要求再做(工作量 ≈ FigmaDesignSource)。
- **任何工具匯出 PNG 都能走 `designImage`**——ImageDesignSource 就是萬用轉接頭,小眾工具的底線用法已覆蓋。
- 未來若加第三個「活」來源,優先考慮 **Penpot**(開源、有 API、社群成長中),不是 XD。

## 已知盲點 / 該補的

- **auto-name 通用名假配對**(2026-08-11 路線 B 實查 B3):「Content」之類的通用圖層名
  會在無關頁面上撞到 `#content` 等容器,產生 critical 級荒謬落差。尺寸合理性防線
  (面積比 >16 拒配)已於 0.13.0 落地;**2026-08-26 Codex 補測首度野外回歸:擋下
  2 個假配對,但 auto-container(LCA 推論)不經防線、同量級撞名也照樣通過**(見路線 B
  補測 B8)。剩餘的要語意層才殺得掉——動工前先蒐集更多野生樣本。
- ~~**隨機 id 的偵測降級**(2026-08-10 路線 A 實查 F2 殘留):純字母隨機字串(rkxvdnnzty)
  現行啟發法分不出來,誠實漏放。~~
  **已補(1.1.0)**:照當日留帳的設計實作——連拍實測(兩次載入,
  單邊 id = 隨機)+ 快照信封存穩定 id 白名單 + check 擷取沿用同一份名單
  (只在拍照端避開是不夠的,check 端規則要一致)。零誤判、免語言模型;
  無隨機 id 的站快照同形零回歸。端到端實測:純字母隨機 id 頁 snapshot→check
  5/5 配對 100/100(1.0 上同頁整批配不上)。
- ~~**深度超過 512 的錯誤訊息**(F1 殘留):仍是誤導的「object cycle」,值得包成人話。~~
  **已補(0.13.0)**:snapshot 落地與 designFile 回讀兩處都包成「DOM 巢狀超過支援上限,
  用 ignore 修剪最深的區域」,附回歸測試——此條漏劃,2026-08-26 盤點時發現。
- **取樣紀律**(對照 XamlContrast 憲法 8.3):實查樣本必須包含非作者的網站/設計檔——
  路線 A(8 個野生網站)與路線 B(2 個公開設計系統 Figma 檔)的 11 個發現,沒有一個
  能在自家 dogfooding 樣本上量出來。方法與結果見 docs/野生實查-*.md。
- ~~**2026-10-21 補測試與 README 逐段核對留下的產品端落差**~~ **已全數處理(2026-10-22)**:可直接修的併進
  2.0.0 的修正;會改 gate 結果、讓原本接受的輸入變成錯誤、或要定 semver 的,維護者拍板走 **2.0.0**
  (CHANGELOG 2.0.0 節的「破壞性變更」,含 npm 通路的 Node 20 下限)。
- **Action 複製報告時,中間層目錄是 symlink 擋不住**(2026-10-22 審查):check 步驟只複製「一般檔案、
  且檔案與 `.parity` 目錄都不是 symlink」的 `<dirname(config)>/.parity/report.json`。但 config 路徑上**更上層**的
  目錄若是 repo commit 的 symlink(例如 `config: web/parity.config.json` 而 `web` → 別處),複製的就是別處的
  `.parity/report.json`。能碰到的只有字面上叫 `.parity/report.json` 的檔案,所以威脅範圍是:**共用的
  self-hosted runner 上,理論上讀得到同一台機器上別的 repo 留下的報告**(報告含站點結構與截圖座標)。
  GitHub 託管的 runner 每次都是乾淨的 VM,不受影響。要補的話:用 `realpath` 解出實際路徑,確認落在
  `$GITHUB_WORKSPACE` 底下才複製(macOS 的 realpath 沒有 `-e`,要注意可攜性)。
- **合併伺服器的 XamlContrast 那一半還缺的**(2026-10-26):
  - ~~「跟上次比」只有 parity 的 run 有~~ **已補(2026-10-28,`ContrastCompare`)**。留下的耦合:鍵照 XamlContrast 的
    baseline 鍵(`file|element|fg|bg`)手刻,伺服器沒有引用它的組件——它的 `keyVersion` 遞增時這裡要一起改。
    報告裡沒有 keyVersion,所以畫面只能在兩筆的 toolVersion 不同時提醒「變化可能來自工具」。
  - ~~**色塊只畫原文就是色碼或具名色的值**~~ **已接上(2026-10-26)**:XamlContrast 1.1 的 JSON 每筆多了 `colors`
    (比值實際用的、合成之後的不透明色),詳情頁在每個主題的比值旁畫「Aa」樣本,與 XamlContrast 的 HTML 報告同一個呈現。
    1.0 的報告沒有 `colors`,照舊只畫原文就是色碼的值。
  - XamlContrast 報告的 `file` 是相對**稽核根目錄**、不是 repo 根目錄,所以問題清單不連回 GitHub 的檔案。
- **登入端點的剩餘限制**(2026-10-22,#21 審查後):存在與不存在的 email 已共用 `LoginThrottles` 表、依 email
  序列化、IP/email 兩層額度(實測見 CHANGELOG 2.0.0)。還剩:時序差沒有歸零(同一個 email 反覆失敗時不存在的 email 慢 0–1.5 ms,原因未定位;攻擊者照「新帳號試 5 次」量分不出來);
  **序列化只在單一行程內**(M5 若跑多個實例,要改成 db 層的鎖或交易);**反向代理後面不設
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` 的話,一個人就能讓所有人 429**(全體共用代理那一個 IP 的額度;
  開關只能在信任的代理後面開,否則來源 IP 可偽造);IP 額度擋不住分散式攻擊;鎖定/額度可被用來讓特定使用者暫時
  登不進去;`LoginThrottles` 不清理(每個嘗試過的 email 一列)。
- **repo 內容在 CI log 裡的行首注入,只擋了一般輸出**(2026-10-22 審查):設計稿圖層名、Figma 節點名、selector、
  設計值、設定檔的 route 帶 `\n::warning …` 時,原樣印出就會在行首變成 workflow command。check/lint/snapshot/baseline
  的一般輸出與警告已改成經 `ConsoleText.Safe` 跳脫控制字元;**例外訊息(最外層的 `error: …`)沒有**——它可能帶
  frame 名稱、route 等設定值,而 Playwright 的例外是刻意的多行說明,一律跳脫會讓它難讀。gate 繞不過(exit code
  與報告不受影響),危害偏低;要補的話,在最外層 catch 只對「我們自己組的訊息裡的資料」跳脫,或改成每行前綴固定字元。

- ~~**gate 盲點:全部沒配到 → 0 分卻 PASS**~~ **已補**:0 配對 / 設計端 0 節點一律 GATE FAIL(附原因,baseline 模式也不豁免);另加選配 `gate.minMatchRate` 門檻。
- ~~**Action 當消費者的流程沒實證**~~ **已實證**(2026-07-18,[parity-action-test](https://github.com/HSU-YU-MING/parity-action-test)):外部 repo 用 `@v0.2.0` 跑真 PR——✓ main 綠(PASS 路徑)✓ PR 打紅 + bot 貼還原度報告(落差/建議修法精確)✓ 再推 commit 後同一則留言原地更新不洗版。
- ~~**CI 裡的 `--baseline` 沒實跑**~~ **已實證**(2026-07-18,parity-action-test):main 留一條既有落差 + commit `parity.baseline.db` + `baseline: true` → ✓ CI 綠(舊債不擋)✓ PR 新增一條落差 → 精確只擋那條(留言列「相對基準:新增 1、不變 1」)。
- ~~**release 觸發被移動式 `v1` tag 誤觸**(1.0 才長出來的坑)~~ **已補(2026-08-26,PR #42)**:
  release.yml 原觸發條件 `v*`,`v1` 移動式 major tag(每個 1.x 發版後前移)也命中——
  每次前移都誤跑一次注定失敗的 release(1.0.0/1.1.0 各踩一次,run #27/#29)。
  **無害但吵**:0.14.0 加的「tag 必須等於 CHANGELOG 頂端版號」守門 3 秒內擋下,
  零發佈,代價只有紅 X + 失敗通知——守門第一次在真實情況立功。
  觸發改認三段式 `v[0-9]*.[0-9]*.[0-9]*`,RELEASING.md 註明「前移 v1 不觸發 release
  是刻意的」。教訓:**0.x 時代的觸發條件沒被「非版號的 tag」考驗過**,新種類的 tag
  (移動式別名)進場時要回頭檢查所有吃 tag 的自動化。

## 檢視留下的整潔項(非 bug)

**全部完成(2026-07-18)**:
- ~~GitHub Actions 升版~~ → checkout@v7 / setup-dotnet@v6 / upload-artifact@v7(脫離 Node 20 淘汰線)。
- ~~`JsonOptions` 兩處重複~~ → 抽 `ReportJson`(check 落地 / serve API / report 回讀共用)。
- ~~沒有 `parity report` 指令~~ → 已加:從既有 `report.json` 重生 Markdown(`--in`/`--md`,預設印 stdout)。
- ~~`oklch` / `display-p3` 不支援~~ → 已支援(OKLCH→OKLab→sRGB、P3 矩陣轉換,超色域 clamp;`lab`/`rec2020` 等仍不支援)。

## 全量檢視(2026-07-18)對照規畫書的決定與盲點

- ~~**`compare.position` 無作用**(規畫書 4.8「比相對位置」沒兌現)~~ **已補(0.5.0)**:相對最近可靠兄弟/父層的偏移比對,誤報防護見 README。
- **Cornhsu.Labeling 落差分類:決定不接**。規畫書 M5 原案要接,但嚴重度/維度分類引擎內建已足,為兩個 enum 欄位引套件是儀式性依賴。此為明文決定,非遺漏。
- **Figma frame PNG 疊圖**(規畫書 4.4 的 images API):可選增強。現行「實作截圖 + 設計框線」足以對位;設計師若要「看設計稿本人」再做。
- ~~**Shadow DOM / iframe 不走訪**~~ **已補(0.8.0)**:組合樹走訪(open shadow root / slot / 同源 iframe);closed shadow 與跨域 iframe 為原生限制,誠實跳過。RWD 多斷點同版補文件 + target 級 width/height。
- ~~**頁面整體 `transform: scale` 未還原**(規畫書 4.6 有提):量測會被縮放污染~~ **已補(0.10.0)**:擷取時累積祖先 transform 的縮放係數,把 box 幾何除回版面座標系(padding 等 computed style 本不受 transform 影響)。無 transform 時逐位元不變(零回歸);實測 scale(0.5) 頁對未縮放基準 100/100。限制:非 top-left transform-origin 下絕對位置差一常數(但 Parity 比相對位置+尺寸,不受影響);跨域 iframe 內縮放仍不處理。
- ~~**EF `EnsureCreated` 無 migration**:未來 baseline schema 變更時,舊 `parity.baseline.db` 需處理相容。~~
  **已補(0.10.0)**:改走正式 EF migrations;既有(`EnsureCreated` 建、無遷移史)的 db 首次開啟時
  自動接管(先標記 InitialCreate 為已套用再 Migrate),不因「表已存在」而爆。

## 更遠的

- **Chrome 擴充功能**:合理的未來本機外殼,但現在不划算——引擎是 .NET,做擴充會逼出「JS 重寫引擎(雙引擎漂移)」或「另跑本機 server」。真要做要當薄客戶端連 parity server,別重寫引擎。
- ~~**1.0.0**:目前刻意留在 0.x(不承諾 API 穩定);功能穩定後再宣告。~~
  **已發佈(2026-08-26)**:六面凍結 + 演進條款,見 CHANGELOG 1.0.0 與
  `docs/Parity 1.0 介面凍結審查.md`。

## 建議優先序

1. ~~gate 盲點(0 配對 → PASS)~~ 已補。
2. ~~實證 action 消費者流程~~ 已實證(真 PR:PASS / FAIL+留言 / 留言原地更新)。
3. ~~CI 的 `--baseline` 實跑~~ 已實證(舊債不擋、新落差精確擋)。
4. ~~接下來輪到整潔項、M5 下半 / M6。~~ 整潔項與 M5 下半已完成;M6 明文不做(理由見上)。
   **目前在做 M7 團隊儀表板外殼**(內部 M1–M4.6 完成,下一步是內部 M5:對外部署)。
   引擎面與外殼的已知盲點見上面「已知盲點 / 該補的」:auto-name 通用名假配對待更多野生樣本,
   2026-10-21 那批產品端落差 2026-10-22 已全數處理,要改 gate 結果或 semver 的幾條(failOn 白名單、soft 落差
   不擋 gate、數值旗標、只給 height 的 target、reverse 報告的方向、Node 20 下限…)拍板走 **2.0.0**
   (見 CHANGELOG 2.0.0 節);登入端點的帳號探測改成存在與否同一條路(共用節流表、序列化、額度),剩下的限制
   見上面「已知盲點」的登入那一條。
