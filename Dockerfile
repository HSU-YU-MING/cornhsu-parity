# Parity.Server 的部署映像(網頁外殼 M5)。
# 鐵則「雲端不跑瀏覽器」在這裡的樣子:映像裡沒有 Chromium、沒有 Playwright、沒有引擎——只有伺服器、
# 它的資料層與報告契約(Parity.Report)。伺服器只收 parity push / xamlcontrast --push 送上來的已完成報告。
#
# 建置(repo 根目錄):  docker build -t parity-server .
# 執行:                docker run -d -p 8080:8080 -v parity-data:/data \
#                         -e Parity__PublicUrl=https://parity.example.com parity-server
# 管理指令(同一個容器):docker exec <容器> dotnet Parity.Server.dll create-project <名稱>
#                        docker exec <容器> dotnet Parity.Server.dll create-invite <專案> <email> owner
#                        docker exec <容器> dotnet Parity.Server.dll backup /data/backup-<日期>.db
# 資料全在 /data:SQLite 資料庫與登入 cookie 的金鑰。掛 volume,重新部署才不會清空資料、登出所有人。
# TLS 由前面的反向代理或平台處理;代理後面一定要加 -e ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
# (只在信任的代理後面開,見 README 的部署一節)。
#
# 基底映像用 major.minor 的標籤(node 22、dotnet 10.0),不釘 digest:重建時自動拿到作業系統與執行環境的修補版。
# 要逐位元可重現的映像,把三行 FROM 改成 @sha256:… 自己維護。

# ── 1. 前端(web/ → wwwroot)。vite 的 outDir 是 ../src/Parity.Server/wwwroot,所以產物落在 /src/src/… ──
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# ── 2. 伺服器。只複製伺服器那張專案參考圖(Server → Server.Data → Report),引擎與 CLI 根本不進建置環境 ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Parity.Report/Parity.Report.csproj src/Parity.Report/
COPY src/Parity.Server.Data/Parity.Server.Data.csproj src/Parity.Server.Data/
COPY src/Parity.Server/Parity.Server.csproj src/Parity.Server/
RUN dotnet restore src/Parity.Server/Parity.Server.csproj
COPY src/Parity.Report/ src/Parity.Report/
COPY src/Parity.Server.Data/ src/Parity.Server.Data/
COPY src/Parity.Server/ src/Parity.Server/
RUN dotnet publish src/Parity.Server/Parity.Server.csproj -c Release --no-restore -o /app
COPY --from=web /src/src/Parity.Server/wwwroot /app/wwwroot

# ── 3. 執行環境:只有 ASP.NET Core runtime,非 root 執行 ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# /data 先建好並交給 app 使用者:named volume 第一次掛上時會沿用這個目錄的擁有者
RUN mkdir -p /data/keys && chown -R "$APP_UID" /data
ENV ASPNETCORE_URLS=http://+:8080 \
    PARITY_SERVER_DB=/data/parity-server.db \
    Parity__KeysDirectory=/data/keys
USER $APP_UID
VOLUME /data
EXPOSE 8080
# 平台的健康檢查打 GET /healthz(匿名;資料庫連得上回 ok,否則 503)。映像裡沒有 curl,所以這裡不寫 HEALTHCHECK
ENTRYPOINT ["dotnet", "Parity.Server.dll"]
