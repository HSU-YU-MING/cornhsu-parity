# 發布 Cornhsu.Parity（NuGet + npm）

用 **Trusted Publishing（OIDC）**——不需要長效 API key、不需要 repo secret。推一個 `v*` tag 就同時發佈 **NuGet（dotnet tool）與 npm（`npx`）兩個通路**。

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
> README 裡兩處 `@vX.Y.Z` 與「發佈 N 版」的數字都對著它比(`scripts/verify-readme-facts.ps1`),
> `release.yml` 再確認 tag 與 CHANGELOG 頂端一致。

## 每次發布

```sh
# 版號由 tag 推導(release.yml 用 -p:Version 覆蓋 csproj 與 package.json 裡的佔位值)
git tag v0.1.0
git push origin v0.1.0
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
- [ ] 之後**每發一個 1.x**,把 `v1` 前移到最新:`git tag -f v1 v1.x.y && git push -f origin v1`

## 本機乾跑（不發佈,驗證封裝可裝可跑）

```sh
dotnet pack Parity.slnx -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts Cornhsu.Parity --version 0.1.0
parity version && parity help
dotnet tool uninstall --global Cornhsu.Parity
```

> NuGet 套件約 237 MB（`PlaywrightPlatform=all` 收齊五平台 driver,近 nuget.org 250 MB 上限);npm 平台子套件各約 129 MB（已刪掉 Playwright 自帶 Node、改用使用者現成 Node）。兩者第一次都仍需 `parity install-browser` 下載 Chromium 本體。
