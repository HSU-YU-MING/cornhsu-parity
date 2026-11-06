# 發布 Cornhsu.Parity（NuGet + npm）

用 **Trusted Publishing（OIDC）**——不需要長效 API key、不需要 repo secret。推一個三段式版號 tag(`vX.Y.Z`,例如 `v2.0.1`;release.yml 只認 `v[0-9]*.[0-9]*.[0-9]*`,移動式的 `v2` 不會觸發)就同時發佈 **NuGet（dotnet tool）與 npm（`npx`）兩個通路**。

## 一次性設定

1. **把 repo 推上 GitHub**:`HSU-YU-MING/cornhsu-parity`
   （`Directory.Build.props` 的 `RepositoryUrl`、SourceLink、`release.yml` 都已指向這個路徑。）

2. **在 nuget.org 設定 Trusted Publisher**（帳號 → Trusted Publishing）:
   - Package：`Cornhsu.Parity`（新 ID 可在此政策下由首次發佈建立）
   - Repository owner：`HSU-YU-MING`
   - Repository：`cornhsu-parity`
   - Workflow：`release.yml`
   - **範圍(Scope)要涵蓋 `Cornhsu.Parity` 與九個平台包 `Cornhsu.Parity.<rid>`,也要允許建立新套件**(見下面「每個平台一包的第一次發版」)

3. 確認 `release.yml` 裡 `NuGet/login` 的 `user:` = 你的 **nuget.org 使用者名稱**（目前 `Cornhsu`）。

4. **npm 發布**已設定（OIDC 信任發布,見 `release.yml` 的 npm job,scope `@cornhsu`）——同樣無長效 token。

## 版號的唯一真相源:git tag

**發版不需要、也不應該去改任何檔案裡的版號。** 全部由 tag 推導:

| 檔案 | 應該長怎樣 | 為什麼 |
|---|---|---|
| `src/Parity.Cli/Parity.Cli.csproj` 的 `<Version>` | **固定 `0.0.0-dev`** | 寫真版號一定會跟 tag 漂開(2026-08 就發生過:csproj 停在 0.13.0、tag 已 v0.13.1),而從原始碼建出來的執行檔會自稱那個舊版號。留空更糟——退回 MSBuild 預設 `1.0.0`,等於謊稱介面已凍結。 |
| `npm/cornhsu-parity/package.json` 的 `version` | **固定 `0.0.0-placeholder`** | 同理,由 `prepare.mjs` 在發布時以 tag 版號改寫。 |

`release.yml` 用 `-p:Version=${GITHUB_REF_NAME#v}` 覆蓋這兩處。`parity version` 印的是
InformationalVersion,所以本機建置顯示 `0.0.0-dev+<commit>`——一眼看得出不是發行版,
而且 bug 回報裡直接帶著 commit。

> 唯一要人手維護版號的地方是 **`CHANGELOG.md`**,而且它是「還沒推 tag 之前」的版本真相源:
> README 的 `@vN`(N = CHANGELOG 最新版號的 major)、「發佈 N 版」與最新版號都對著它比
> (`scripts/verify-readme-facts.ps1`),`release.yml` 再確認 tag 與 CHANGELOG 頂端一致。
> 標題寫 `## 2.0.0` 或 `## 2.0.0 — 2026-10-22` 都可以(release.yml、verify 腳本與 `ActionYmlTests` 都只取開頭的版號;別加方括號)。

## 每次發布

```sh
# 版號由 tag 推導(release.yml 用 -p:Version 覆蓋 csproj 與 package.json 裡的佔位值)
git tag v0.1.0
git push origin v0.1.0
```

發完之後把移動式 major tag 前移到這一版(現在是 `v2`,見下面「2.0.0」一節):

```sh
git tag -f v2 v2.x.y && git push -f origin v2
```

**tag 要打在 master 上**:先 `git switch master && git pull`,確認本機等於 `origin/master` 再打(2.2.0 忘了切回來,tag 打在 PR 分支的 commit 上;內容相同所以沒事,但不該靠運氣)。

**這一版開始量新屬性的話**,回 stats-collector 的 `PROTOCOL.md` 7.2 填「開始量的工具版本」。舊版的 0 不代表沒有落差,不填就分不出來。

推 tag 之前還有兩件事要查,**CI 都不會擋**(2.2.0 發版前都抓到問題):

- **相依變動**:`git diff v<上一版>..HEAD -- '*.csproj'`。dependabot 升版不會寫 CHANGELOG,也不會改 `THIRD-PARTY-NOTICES.md`。
  Playwright 換版會連 Chromium 一起換:要標「升級必做」(重跑 `parity install-browser`,否則 `check` exit 2),
  並比對新舊版量測:nuget.org 上的舊版與這一版跑同一批頁面(作品集 26 頁 + 離線示範的乾淨複本),`report.json` 除了路徑要相同。
  **舊版是正式版號,跑的時候一定要設 `DO_NOT_TRACK=1`**,否則測試資料會送進研究資料。
- **照 release.yml 的順序用真版號跑一次**:`dotnet build -p:Version=<真版號>` → `dotnet test --no-build`(要有瀏覽器)→ `dotnet pack`。
  CI 平常是 `0.0.0-dev`,發版機器又沒裝瀏覽器,寫死開發版號的測試兩邊都抓不到。NuGet 快取要隔離(`NUGET_PACKAGES`),本機做的真版號包不能留在你的快取裡。

推 tag 之前先在本機跑一次文件檢查(CI 也會擋,但先跑比較快):

```sh
pwsh scripts/verify-readme-facts.ps1            # 只檢查
pwsh scripts/verify-readme-facts.ps1 -Update    # 確認新數字對之後,一鍵更新兩份 README
```

> 沒裝 PowerShell 7 的話,Windows PowerShell 5.1 直接跑 `.\scripts\verify-readme-facts.ps1` 也可以
> （腳本存成 UTF-8 with BOM 就是為了讓 5.1 也讀得對中文訊息）。CI 用的是 runner 內建的 `pwsh`。

`release.yml` 會自動發**兩個通路**（版號都從 tag 推導,`-p:Version` 覆蓋 csproj 裡的本機預設值）:

- **NuGet**（指標包 `Cornhsu.Parity` + 九個平台包 `Cornhsu.Parity.<rid>`,dotnet tool）:build → test → pack → 檢查每個包的內容 → OIDC → 推平台包 → 等 nuget.org 查得到 → 重新 OIDC → 推指標包;平台包含 snupkg + SourceLink,可 step-in 除錯。
- **npm**（`cornhsu-parity` + 6 個 `@cornhsu/parity-<platform>` 平台子套件,支援 `npx`）:各 RID `dotnet publish --self-contained` → `prepare.mjs` 組裝 → OIDC 信任發布平台子套件 → 等每個壓縮檔都下載得到(最多 3 小時)→ 發入口套件（`scripts/publish-npm-packages.sh`;已存在的版號跳過,可安全重跑）。

## 1.0.0 首發（介面凍結,一次性)

1.0 不只是升版,是**對外承諾介面凍結**（破壞這些介面之後要升 major）。發之前:

- [ ] dogfooding 真專案連用滿 **2–4 週**,期間沒有再想改五個契約面（config / CLI / action inputs / `report.json` / baseline schema——見 [Parity 1.0 介面凍結審查.md](<docs/Parity 1.0 介面凍結審查.md>)）
- [ ] CHANGELOG 的 1.0.0 條目**明列「以下介面自此凍結」**（不是列功能）
- [ ] `npx cornhsu-parity` 端到端裝過一次（安裝路徑穩 = 承諾的一部分）

發布 + 建立移動式 major tag:

```sh
git tag v1.0.0 && git push origin v1.0.0     # 觸發 release.yml,發 NuGet + npm

# GitHub Action 使用者用 @v1 引用 —— 這時介面已凍結,移動式 tag 才安全
git tag v1 v1.0.0 && git push origin v1
```

收尾:

- [ ] README 兩份共四處 `uses: …@v0.x.y` → **`@v1`**（0.x 期間刻意 pin 版本,1.0 起才切移動式）；
      同時把 `scripts/verify-readme-facts.ps1` 的 pin 檢查改成認 `@v1`，否則它會擋下這次變更
- [x] 1.x 期間每發一版就把 `v1` 前移(最後停在 v1.1.0)
      （移動式 tag 的推送**不會**觸發 release——workflow 只認三段式 `v*.*.*` tag,這是刻意的：
      `v1` 若觸發,守門會因 tag ≠ CHANGELOG 版號而必紅一次,徒增失敗通知;`v2` 同理）

## 2.0.0(第一個 major)

這個分支合併之後,**推 tag 就能發**——版號相關的檔案都已就位:CHANGELOG 頂端是 `## 2.0.0 — 2026-10-22`、
兩份 README 引用 `@v2`(28 版、最新 v2.0.0)、`action.yml` 在 `version` 留空時裝 `"2.*"`、SECURITY.md 的
支援版本表是 2.x。發版當天:

- [ ] 實際發版日不是 2026-10-22 的話,改 CHANGELOG 的標題日期(只改日期,版號不動)
- [ ] `pwsh scripts/verify-readme-facts.ps1` 綠燈(CI 也會跑)
- [ ] **建立 `misjudgment` 標籤**(誤判/漏判範本 `1-misjudgment.yml` 要掛的標籤;repo 上沒有的標籤 GitHub 會**靜靜略過**,
      回報就不帶標籤)。`bug`、`enhancement` 已經在 repo 上(GitHub 的預設標籤,2026-10-23 查過),不用建:

```sh
gh label create misjudgment -R HSU-YU-MING/cornhsu-parity --description "Parity reported a gap that isn't real, missed a real one, or graded/matched it wrong" --color D93F0B
```

- [ ] issue 範本(`.github/ISSUE_TEMPLATE/`)要 **merge 進 master 之後**才生效:在那之前,報告裡的「Report a misjudgment」
      與 `parity bug-report` 印的預填網址(`issues/new?template=…`)都找不到範本,只會開到一般的新 issue 頁,
      預填與標籤都不會生效。
      所以順序是:這個分支 merge 進 master → 建標籤 → 推版號 tag。
- [ ] 推版號 tag,等 release.yml 發完 NuGet + npm,再建移動式 `v2`:

```sh
git tag v2.0.0 && git push origin v2.0.0     # 觸發 release.yml(守門比對 CHANGELOG 頂端 = 2.0.0)
git tag v2 v2.0.0 && git push origin v2      # @v2 的使用者從這裡開始拿到 2.x;這個推送不觸發 release
```

- [ ] 之後**每發一個 2.x**,把 `v2` 前移:`git tag -f v2 v2.x.y && git push -f origin v2`
- [ ] **`v1` 不再前移,也不再發 1.x。** 已知副作用(2026-10-22 接受):`v1` 停在 v1.1.0,那份 `action.yml`
      在 `version` 留空時執行不帶範圍的 `dotnet tool install`,裝的是**不限 major** 的最新版——限制 major 的
      `"1.*"` 是 1.1.0 **之後**才加的,從沒發佈過。所以 2.0.0 一上 nuget.org,沒填 `version` 的 `@v1` workflow
      就會用 1.1.0 的 action 步驟跑 2.x 的工具。原本的避法是先發一個含 `"1.*"` 的 1.x 並把 `v1` 前移,再發
      2.0.0;這次決定不做(還沒有外部使用者需要過渡期),改由 CHANGELOG 2.0.0 的「升級必做」第 1 點與兩份 README
      請使用者改用 `@v2`,或給 `version: 1.1.0` 留在 1.x。**之後不要為了這個補發 1.x。**

**下一個 major(3.0.0)**:2.x 的每一版 `action.yml` 都帶 `"2.*"`,所以 `v2` 停在哪一版都不會跨 major,
`v1` 的問題不會重演。屆時要改的:`action.yml` 的 `"2.*"` → `"3.*"`(`ActionYmlTests` 會在 CHANGELOG
頂端變成 3.x 時紅燈)、兩份 README 的 `@v2` → `@v3`(`verify-readme-facts.ps1` 依最新版號的 major 檢查,
同樣會紅)、SECURITY.md 的支援版本表、建 `v3` tag。

## 2.1.0(匿名使用統計,預設打開)

**這一版有三個硬條件,任一個沒滿足就不推 tag**——tag 推出去就上 nuget.org、npm 與 `@v2`,沒有反悔的機會:

- [x] **研究倫理已確認「預設打開、含 CI」可以**(2026-10-28,維護者確認)。預設打開等於沒有經過使用者主動同意
      (`D:\擴充功能\nuget\稽核-2026-10-22\統計功能規劃.md` 最上面那個 ⚠)。日後審查若改要求主動同意:把
      `UsageStats.SendByDefault` 改成 `false`,補上「第一次在終端機詢問」(協定 §3,目前沒有實作),Action 的
      `share-stats` 預設值與兩份 README、CHANGELOG 的說法一起改。
- [x] **收集站已上線**(2026-10-28 部署;說明頁 200、兩條入口對 `{}` 回 400、正式資料庫 0 筆):`https://stats.cornhsu.com/` 打得開資料說明頁,對 `POST /v1/parity` 送一份**故意不合法**的本文
      (例如 `{}`)回 400。**不要拿合法的資料去試正式站**——那一筆會被當成真實使用存進研究資料。收集站本身收、拒、上限的
      行為,在 stats-collector 用 `npm run dev` + `npm run smoke` 對本機模擬驗。工具先發、收集站還沒上線的話,每個使用者
      每次 check 都白等最多 3 秒,資料全丟。部署收集站是對外的動作,由維護者自己做。
- [x] **`stats@cornhsu.com` 收得到信**(2026-10-28 測過:寄一封測試信,Cloudflare 的活動記錄是「已轉寄」):說明頁上寫的刪除聯絡地址(Cloudflare Email Routing 的自訂地址,轉寄到維護者的信箱)。
      寄一封測試信確認轉寄到了——公開頁面上的地址收不到信,有專案 ID 的人就沒辦法要求刪除。

接著照「每次發布」:CHANGELOG 的 `## 未發佈` 改成 `## 2.1.0 — <日期>`(**刪掉最上面那段給維護者的 HTML 註解**,
保留給使用者的 ⚠ 說明)、`verify-readme-facts.ps1` 綠燈、推 `v2.1.0`、前移 `v2`。發完用
`parity check --stats-preview` 對 `samples/demo` 跑一次,確認 `toolVersion` 是 `2.1.0` 且 `would be sent`。

## 每個平台一包的第一次發版(2.2.0)

**這一版有一個硬條件,沒確認就不推 tag(2026-11-04 已確認:`cornhsu.parity` 那條 Active、`Scopes: All`、`Glob Patterns and Packages: *`):**

- [x] **登入 nuget.org → Trusted Publishing,確認 Parity 那條政策的範圍涵蓋 `Cornhsu.Parity.*`,也允許建立新套件。**
      這九個都是新 ID,第一次推才會建立。官方文件(2026-09 版)只說政策有 Scope 與 glob、沒講細節,維護者以外看不到設定。
      有前例但不是保證:`Cornhsu.Parity`、`Cornhsu.XamlContrast`、`Cornhsu.Labeling` 的第一版都是這條路建立新 ID 的(2026-07)。
      `Cornhsu.` 前綴已保留,所以新 ID 別人搶不走,發出去也會有驗證勾勾。

接著照「每次發布」:CHANGELOG 的 `## 未發佈` 改成 `## 2.x.0 — <日期>`、`verify-readme-facts.ps1` 綠燈、推 tag。
**第一次發版就是新推送流程的第一次實跑**:流程不支援預覽版號,CI 只能對本機資料夾乾跑。好在指標包最後才推,失敗不會影響使用者。

發到一半失敗會留下什麼:

| 狀況 | 使用者看到 | 怎麼辦 |
|---|---|---|
| 政策不允許(第一個平台包就被拒) | 還是上一版 | 改政策,重跑 job |
| 平台包推到一半、等待逾時、指標包推失敗 | 還是上一版(指標包還沒上架) | 重跑 job;每次 push 都帶 `--skip-duplicate`,已上架的會略過 |
| 平台包上架了、指標包沒推 | 還是上一版;nuget.org 多了幾個沒人會裝到的套件(`DotnetToolRidPackage`,不能直接裝) | 同上,重跑 |
| 某個平台包在 nuget.org 驗證失敗 | 還是上一版(指標包不會推) | 看 nuget.org 的訊息;能不能重推同版號沒有查到文件,必要時改發下一個 patch |

發完之後,在 `v2` 前移之前先驗:

```sh
dotnet tool install --tool-path /tmp/parity-check Cornhsu.Parity --version 2.x.0   # 版號寫確切的,確認指標包與平台包都查得到
/tmp/parity-check/parity --version
```

再對 `samples/demo` 跑一次真的 `check`(預期 exit 1),看一眼 nuget.org:`Cornhsu.Parity` 的頁面正常、平台包不會出現在
`dotnet tool search parity`。**九個新 ID 建立之後刪不掉**,只能取消列出(unlist),所以平台清單可以加、不能減。

**實驗時驗到的**(draft PR #61,2026-11-04):六個平台真機裝起來跑 check,報告計數跟 build-test 一字不差;從 nuget.org 的 2.1.0
`dotnet tool update` 上來再退回去都成功,舊的基準資料庫讀得到;.NET 8/9 SDK 新舊形式都裝不起來(沒有變得更挑)。發行版自己打包的 SDK 真的在容器裡裝過,都配到對的包(run 41788557499):
Ubuntu 24.04(SDK 10.0.114,回報 `ubuntu.24.04-x64` → `linux-x64`)、Fedora 43(10.0.113,`fedora.43-x64` → `linux-x64`)、
Alpine 3.23(10.0.110,`alpine.3.23-x64` → `linux-musl-x64`)。那個 job 只跑一次就拿掉了:裝不起來也修不了,留著只會因為外部套件庫而紅。

**2.2.0 實際發版的結果(2026-11-04,run 41802855420):一次成功,不用重跑。**

- 政策(`Scopes: All`、`*`)真的建立了九個新 ID,設定不用改。
- 平台包推完到 flat container 查得到花了 **1138 秒(約 19 分鐘)**,推送本身只要 21 秒;入口套件與 npm 再約 6 分鐘才看得到,別誤判成失敗。
- 平台包查 ID 搜得到,帶驗證勾勾,類型是 `DotnetToolRidPackage`,帳號底下也列得出來;`dotnet tool search` 只列入口套件。
- 發佈後在 Windows 從 nuget.org 與 npm 真的裝過:`check`、基準、`--version "2.*"`、從 2.1.0 更新都正常。其他平台是發版前 CI 用打包好的包驗的。

**還沒查的**:九個 snupkg 的符號驗證結果(pdb 內容相同;失敗的話主套件照常,只差符號)。某個平台包驗證失敗後能不能重推同版號,這次沒遇到。

**2.3.0 實際發版的結果(2026-11-06,run 42157284408):NuGet 跟上面差不多,npm 的平台子套件慢很多。**

- 平台包推完到查得到花了約 14 分半,入口套件再約 6 分鐘。
- npm 的入口套件發佈後 4 分鐘就查得到,平台子套件各平台不同:快的 8 分鐘,`darwin-arm64` 55 分鐘,`win32-arm64` 超過 1.5 小時才查得到(原因沒查到,發版紀錄顯示有發佈成功)。2.3.1 也一樣,`win32-x64` 57 分鐘才查得到。
- 入口套件先到、子套件還沒到的這段時間,該平台的 `npm install` 不報錯(選用相依解析不到就跳過),執行時才印 `Reinstall: npm install cornhsu-parity`、exit 2。別的平台的子套件查不到,不影響你這個平台。
  **所以發完驗 npm 要真的裝、真的跑。** 看到這個訊息先查子套件在 registry 上有沒有這一版,不要直接當成套件壞了。
- **查得到不等於裝得起來**(2.3.1 實測):套件清單與 `/<版本>` 回 200 之後,`.tgz` 還 404 了幾分鐘,一樣默默跳過。`npm install --loglevel=http` 看得到是哪一步。
- **空窗期跑過 `npx` 的人,同步完成後還是壞的**:沒有子套件的半成品留在 `npm-cache/_npx/<hash>/`,要刪掉那個資料夾。
- **這個空窗 2.3.1 之後已修**(`scripts/publish-npm-packages.sh`):入口套件等壓縮檔都下載得到才發,所以 npm 會比 NuGet 晚上架,發版 job 最長多跑 3 小時,別誤判成卡住。
  逾時的話入口套件沒發、使用者還是裝到上一版,晚一點重跑 job 即可。還沒實際發過版:等待之後才發入口套件,靠的是 npm CLI 在 publish 當下換 OIDC 金鑰;如果等太久換不到,一樣是重跑。
- 本機的 `python` 是 Windows 的佔位捷徑:跑腳本會 exit 49 而且沒有任何輸出,看起來不像失敗。`verify-tool-packages.py` 要用 `py -3`。

## ImageSharp 的授權金鑰

`src/Parity.Engine/sixlabors.lic` 是 Six Labors 核發的開源社群金鑰,**有效到 2028-02-02**。到期後 Parity 建置不起來,也就發不了版(已發佈的版本不受影響)。

- **續期:2027-11-04 起**到 <https://licensing.sixlabors.com/> 用**同一個信箱**重送表單,Kind 選 `OpenSource`,Assembly names 填 `Parity.Engine`,把新檔案蓋掉舊的。
- 只能放**限定組件**的金鑰(內容有 `Key=Assembly:`),官方明寫這種可以公開。不限組件的金鑰與付費金鑰必須保密(`RepoInvariantsTests` 擋著)。

## 本機乾跑（不發佈,驗證封裝可裝可跑）

```sh
v=0.0.0-local.$(date +%s)
dotnet pack src/Parity.Cli/Parity.Cli.csproj -c Release -o ./artifacts -p:Version=$v
python3 scripts/verify-tool-packages.py ./artifacts $v  # 看每個包的內容;機器上沒有 Python 就略過,CI 的 pack-smoke 與 release 都會跑
dotnet tool install --tool-path ./artifacts/tool --add-source ./artifacts Cornhsu.Parity --version $v
./artifacts/tool/parity version && ./artifacts/tool/parity help
rm -rf ./artifacts    # 用完整個刪掉(.gitignore 已排除 artifacts/)
```

`parity version` 印 `parity 0.0.0-local.<時間戳>+<commit>`。四個坑(前三個舊版指令都踩過):

- **一定要帶 `--version`**:不帶的話,本地這個預覽版號不會被選,會從 nuget.org 裝到**已發佈的正式版**——
  看起來裝成功了,測的卻不是你剛 pack 的東西。舊版指令寫死 `--version 0.1.0`,裝到的正是 nuget.org 上的 0.1.0
  (pack 不帶 `-p:Version` 時產物是 csproj 的 `0.0.0-dev`,根本沒有 0.1.0)。
- **每次用新的版本號**(上面用時間戳):同一個版本號第二次 pack 再裝,可能拿到先前裝過、快取住的那一份。
- **用 `--tool-path`,不要 `--global`**:`--global` 會跟你已經裝好的 parity 衝突(已安裝就失敗,或把你日常用的那一份換掉)。
- **Windows 上工具目錄別太深**:路徑長到 SQLite 原生檔超過約 250 字元時,`any` 包的 `baseline` 指令會報
  `The type initializer for 'Microsoft.Data.Sqlite.SqliteConnection' threw an exception`(2026-11-04 踩到,252 字元失敗、242 字元正常)。
  是路徑太長,不是包壞了;一般安裝路徑只有約 115 字元。

想看別的平台會配到哪一包,安裝時設 `DOTNET_RUNTIME_ID`:`DOTNET_RUNTIME_ID=linux-arm dotnet tool install …` 會裝到 `any`。
SDK 對照表裡沒有的名稱(微軟版 SDK 上的 `ubuntu.24.04-x64`)會裝失敗,那是模擬的假象:發行版自己打包的 SDK 帶著自己的對照表,實測裝得起來(見上一節)。

npm 通路沒有本機乾跑的指令(要各 RID `dotnet publish` 再跑 `npm/prepare.mjs`),發佈前靠 release.yml 的 npm job。

> NuGet 平台包:六個完整平台各 44～54 MB、Alpine 各 4 MB、`any` 30 MB,指標包 0.1 MB,一次發版總共上傳約 331 MB。單一套件時代到 2.1.0 是 243 MB,
> **離 nuget.org 250 MB 上限只剩幾 MB**,這是改成每個平台一包的原因;2.1.0 在 NuGet 的驗證也比小套件慢上半小時以上。
> npm 平台子套件各約 129 MB（已刪掉 Playwright 自帶 Node、改用使用者現成 Node）。兩者第一次都仍需 `parity install-browser` 下載 Chromium 本體。
