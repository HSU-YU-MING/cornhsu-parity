# 發布 Cornhsu.Parity 到 NuGet

用 **Trusted Publishing（OIDC）**——不需要長效 API key、不需要 repo secret。推一個 `v*` tag 就發佈。

## 一次性設定

1. **把 repo 推上 GitHub**:`HSU-YU-MING/cornhsu-parity`
   （`Directory.Build.props` 的 `RepositoryUrl`、SourceLink、`release.yml` 都已指向這個路徑。）

2. **在 nuget.org 設定 Trusted Publisher**（帳號 → Trusted Publishing）:
   - Package：`Cornhsu.Parity`（新 ID 可在此政策下由首次發佈建立）
   - Repository owner：`HSU-YU-MING`
   - Repository：`cornhsu-parity`
   - Workflow：`release.yml`

3. 確認 `release.yml` 裡 `NuGet/login` 的 `user:` = 你的 **nuget.org 使用者名稱**（目前 `Cornhsu`）。

## 每次發布

```sh
# 版號由 tag 推導(release.yml 用 -p:Version 覆蓋 csproj 的預設 0.1.0)
git tag v0.1.0
git push origin v0.1.0
```

`release.yml` 會自動:build → test → pack → OIDC 換臨時金鑰 → `dotnet nuget push`。
含符號套件（snupkg）與 SourceLink，使用者可 step-in 原始碼除錯。

## 本機乾跑（不發佈,驗證封裝可裝可跑）

```sh
dotnet pack Parity.slnx -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts Cornhsu.Parity --version 0.1.0
parity version && parity help
dotnet tool uninstall --global Cornhsu.Parity
```

> 套件約 39 MB——內含 Playwright 的瀏覽器驅動;使用者第一次仍需 `parity install-browser` 下載 Chromium 本體。
