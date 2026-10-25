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

推 tag 之前先在本機跑一次文件檢查(CI 也會擋,但先跑比較快):

```sh
pwsh scripts/verify-readme-facts.ps1            # 只檢查
pwsh scripts/verify-readme-facts.ps1 -Update    # 確認新數字對之後,一鍵更新兩份 README
```

> 沒裝 PowerShell 7 的話,Windows PowerShell 5.1 直接跑 `.\scripts\verify-readme-facts.ps1` 也可以
> （腳本存成 UTF-8 with BOM 就是為了讓 5.1 也讀得對中文訊息）。CI 用的是 runner 內建的 `pwsh`。

`release.yml` 會自動發**兩個通路**（版號都從 tag 推導,`-p:Version` 覆蓋 csproj 裡的本機預設值）:

- **NuGet**（`Cornhsu.Parity`,dotnet tool）:build → test → pack → OIDC → `dotnet nuget push`;含 snupkg + SourceLink,可 step-in 除錯。
- **npm**（`cornhsu-parity` + 6 個 `@cornhsu/parity-<platform>` 平台子套件,支援 `npx`）:各 RID `dotnet publish --self-contained` → `prepare.mjs` 組裝 → OIDC 信任發布（已存在的版號跳過,可安全重跑）。

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

**這一版有兩個硬條件,任一個沒滿足就不推 tag**——tag 推出去就上 nuget.org、npm 與 `@v2`,沒有反悔的機會:

- [ ] **指導教授(或倫理審查)已確認「預設打開、含 CI」可以。** 預設打開等於沒有經過使用者主動同意
      (`D:\擴充功能\nuget\稽核-2026-10-22\統計功能規劃.md` 最上面那個 ⚠)。審查若要求主動同意:把
      `UsageStats.SendByDefault` 改成 `false`,補上「第一次在終端機詢問」(協定 §3,目前沒有實作),Action 的
      `share-stats` 預設值與兩份 README、CHANGELOG 的說法一起改。
- [ ] **收集站已上線**:`https://stats.cornhsu.com/` 打得開資料說明頁,對 `POST /v1/parity` 送一份**故意不合法**的本文
      (例如 `{}`)回 400。**不要拿合法的資料去試正式站**——那一筆會被當成真實使用存進研究資料。收集站本身收、拒、上限的
      行為,在 stats-collector 用 `npm run dev` + `npm run smoke` 對本機模擬驗。工具先發、收集站還沒上線的話,每個使用者
      每次 check 都白等最多 3 秒,資料全丟。部署收集站是對外的動作,由維護者自己做。

接著照「每次發布」:CHANGELOG 的 `## 未發佈` 改成 `## 2.1.0 — <日期>`(**刪掉最上面那段給維護者的 HTML 註解**,
保留給使用者的 ⚠ 說明)、`verify-readme-facts.ps1` 綠燈、推 `v2.1.0`、前移 `v2`。發完用
`parity check --stats-preview` 對 `samples/demo` 跑一次,確認 `toolVersion` 是 `2.1.0` 且 `would be sent`。

## 本機乾跑（不發佈,驗證封裝可裝可跑）

```sh
v=0.0.0-local.$(date +%s)
dotnet pack Parity.slnx -c Release -o ./artifacts -p:Version=$v
dotnet tool install --tool-path ./artifacts/tool --add-source ./artifacts Cornhsu.Parity --version $v
./artifacts/tool/parity version && ./artifacts/tool/parity help
rm -rf ./artifacts    # 用完整個刪掉(.gitignore 已排除 artifacts/)
```

2026-10-23 照上面跑過一次:pack 出 `Cornhsu.Parity.0.0.0-local.<時間戳>.nupkg`(約 237 MB),裝到 `./artifacts/tool`,
`parity version` 印 `parity 0.0.0-local.<時間戳>+<commit>`。三個坑(舊版指令三個都踩):

- **一定要帶 `--version`**:不帶的話,本地這個預覽版號不會被選,會從 nuget.org 裝到**已發佈的正式版**——
  看起來裝成功了,測的卻不是你剛 pack 的東西。舊版指令寫死 `--version 0.1.0`,裝到的正是 nuget.org 上的 0.1.0
  (pack 不帶 `-p:Version` 時產物是 csproj 的 `0.0.0-dev`,根本沒有 0.1.0)。
- **每次用新的版本號**(上面用時間戳):同一個版本號第二次 pack 再裝,可能拿到先前裝過、快取住的那一份。
- **用 `--tool-path`,不要 `--global`**:`--global` 會跟你已經裝好的 parity 衝突(已安裝就失敗,或把你日常用的那一份換掉)。

npm 通路沒有本機乾跑的指令(要各 RID `dotnet publish` 再跑 `npm/prepare.mjs`),發佈前靠 release.yml 的 npm job。

> NuGet 套件約 237 MB（`PlaywrightPlatform=all` 收齊五平台 driver,近 nuget.org 250 MB 上限);npm 平台子套件各約 129 MB（已刪掉 Playwright 自帶 Node、改用使用者現成 Node）。兩者第一次都仍需 `parity install-browser` 下載 Chromium 本體。
