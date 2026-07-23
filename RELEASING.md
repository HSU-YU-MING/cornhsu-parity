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

## 每次發布

```sh
# 版號由 tag 推導(release.yml 用 -p:Version 覆蓋 csproj 的預設 0.1.0)
git tag v0.1.0
git push origin v0.1.0
```

`release.yml` 會自動發**兩個通路**（版號都從 tag 推導,`-p:Version` 覆蓋 csproj 佔位的 0.9.3）:

- **NuGet**（`Cornhsu.Parity`,dotnet tool）:build → test → pack → OIDC → `dotnet nuget push`;含 snupkg + SourceLink,可 step-in 除錯。
- **npm**（`cornhsu-parity` + 6 個 `@cornhsu/parity-<platform>` 平台子套件,支援 `npx`）:各 RID `dotnet publish --self-contained` → `prepare.mjs` 組裝 → OIDC 信任發布（已存在的版號跳過,可安全重跑）。

## 1.0.0 首發（介面凍結,一次性)

1.0 不只是升版,是**對外承諾介面凍結**（破壞這些介面之後要升 major）。發之前:

- [ ] dogfooding 真專案連用滿 **2–4 週**,期間沒有再想改五個契約面（config / CLI / action inputs / `report.json` / baseline schema——見 `Parity 1.0 介面凍結審查.md`）
- [ ] CHANGELOG 的 1.0.0 條目**明列「以下介面自此凍結」**（不是列功能）
- [ ] `npx cornhsu-parity` 端到端裝過一次（安裝路徑穩 = 承諾的一部分）

發布 + 建立移動式 major tag:

```sh
git tag v1.0.0 && git push origin v1.0.0     # 觸發 release.yml,發 NuGet + npm

# GitHub Action 使用者用 @v1 引用 —— 這時介面已凍結,移動式 tag 才安全
git tag v1 v1.0.0 && git push origin v1
```

收尾:

- [ ] README 兩處 `uses: …@v0.9.x` → **`@v1`**（0.x 期間刻意 pin 版本,1.0 起才切移動式）
- [ ] 之後**每發一個 1.x**,把 `v1` 前移到最新:`git tag -f v1 v1.x.y && git push -f origin v1`

## 本機乾跑（不發佈,驗證封裝可裝可跑）

```sh
dotnet pack Parity.slnx -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts Cornhsu.Parity --version 0.1.0
parity version && parity help
dotnet tool uninstall --global Cornhsu.Parity
```

> NuGet 套件約 237 MB（`PlaywrightPlatform=all` 收齊五平台 driver,近 nuget.org 250 MB 上限);npm 平台子套件各約 129 MB（已刪掉 Playwright 自帶 Node、改用使用者現成 Node）。兩者第一次都仍需 `parity install-browser` 下載 Chromium 本體。
