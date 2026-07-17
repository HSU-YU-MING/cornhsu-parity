# Parity

> 數值級設計還原度檢查工具——抓 **Figma 與實作端的真實數值**做程式比對,不是疊圖給人眼看。
> 本機 / CI / 內網都能跑,在東西公開之前就把「顏色不對、字太大、間距跑掉」擋下來。

|  | 疊圖工具(Pixelay 等) | Parity |
|---|---|---|
| 做法 | 疊兩張圖給人眼看 | 抓兩邊真實數值做程式比對 |
| 結果 | 「這裡怪怪的」 | 「paddingLeft 8px,設計 20px」 |
| 跑在哪 | 瀏覽器外掛 / 雲端(碰不到 localhost) | **你的機器 / CI runner**,localhost 天生連得到 |
| 進 CI | 大多不行 | PR 落差超門檻就擋 |

## 快速開始

```sh
dotnet tool install -g Cornhsu.Parity   # (發佈後)
parity init                # 產生 parity.config.json 範本
parity install-browser     # 第一次:下載 Playwright Chromium
export FIGMA_TOKEN=...     # scope 只需 file_content:read
parity check               # 比對,輸出報告 + exit code
```

在這個 repo 裡開發時:

```sh
dotnet run --project src/Parity.Cli -- check --config samples/demo/parity.config.json
```

`samples/demo` 是一組**離線示範**(設計來源用本機 JSON,不需要 Figma token):
`index.html` 刻意做壞了幾個地方,`parity check` 會精確報出每一條落差與未配對清單,
並因 serious 級落差回傳 exit code 1(GATE FAIL)——這正是 CI 把關的行為。

## 設定:`parity.config.json`

```jsonc
{
  "figmaFileKey": "abcd1234",
  "designToken": "env:FIGMA_TOKEN",
  "mapFile": "parity.map.json",              // 手動補漏的對應檔(圖層名 → selector)
  "targets": [
    { "route": "/", "frame": "10:2", "url": "http://localhost:8080/" }
  ],
  "compare": { "position": "relative" },     // 預設不比絕對 x/y
  "tolerances": { "sizePx": 2, "spacingPx": 2, "colorDeltaE": 2.0 },
  "ignore": ["[data-parity-ignore]"],
  "gate": { "failOn": ["critical", "serious"] }
}
```

## 目標:網頁或 Electron 桌面 app

`target.url` 決定實作端是什麼,指令與報告完全一樣:

| url 形式 | 對應 |
|---|---|
| `http(s)://…` | 一般網頁 / 內網 staging |
| `file://…`(或相對路徑) | 本機 HTML 檔 |
| `cdp:http://host:port` | **已在跑的 Electron 桌面 app**(抓活視窗的 DOM) |

Electron:啟動時加遠端偵錯埠,再把 `url` 指過去即可——Parity 會 attach 進去讀當下畫面,不導頁、不干擾 app:

```sh
electron . --remote-debugging-port=9222      # 你的 app,加這個旗標
```
```jsonc
{ "route": "/", "frame": "20:5", "url": "cdp:http://localhost:9222" }
```

> 為什麼 Electron 幾乎免費:它的畫面就是一個 Chromium renderer,跟網頁同一套 DOM/CSS 量測。手機原生 / Flutter / 原生桌面不走 DOM,留待 v2.0。

## 比什麼、不比什麼

只比「不管版面怎麼流動都該一樣」的東西:

- **自身尺寸**(寬高;TEXT 節點例外——文字框量測天生不同,比了狂誤報)
  - auto-layout 的 **HUG(隨內容)/ FILL(隨父層)** 那一軸也跳過——Figma 量的寬 ≠ 瀏覽器渲染寬是必然的,只比 **FIXED** 的軸
- **內距 / 間距**:padding 四邊、auto-layout `itemSpacing` ↔ 實際子元素 gap
- **字體**:size / weight / line-height / letter-spacing 精確比;font-family 是**軟落差**(不擋 gate)
- **顏色**:CIEDE2000 (ΔE) 設門檻,不是 hex 全等
- **刻意不比絕對位置 x/y**:彈性版面下本來就會不同,比了 = 誤報 = 失去信任

## 配對策略(以設計端為錨)

1. **自動文字錨定**:設計 TEXT 文字 ↔ 頁面文字(唯一才配;多個同文字時用圖層名消歧,仍不硬湊)
2. **圖層名 ↔ id / class / aria-label**:`CTA Button` 自動對上 `class="cta-button"`
3. **容器推論**:配不到的容器,用「已配對子孫的最近共同祖先」反推(純結構,不猜)
4. **手動補漏**:HTML 加 `data-parity="圖層名"`,或 `parity.map.json` 寫 `{ "圖層名": "CSS selector" }`
5. 配不到 → **誠實列進未配對清單**(真正需要人補的才補)

## 架構

一顆引擎(`Parity.Engine`,純函式庫)+ 多個外殼。引擎只比對「兩棵正規化的樹」:

```
IDesignSource ──→ DesignNode 樹 ──┐
  (Figma / JSON / 未來 XD、Sketch)  ├─→ Normalizer → Matcher → DiffEngine → FidelityReport
IImplementationSource → RenderedNode ┘
  (Playwright / 未來 WPF、桌面)
```

```
src/Parity.Engine/        引擎:唯一進入點 FidelityEngine
  DesignSources/          IDesignSource + Figma(REST + 本機快取)/ Json
  ImplementationSources/  IImplementationSource + Web(Playwright)
  Comparison/             Normalizer / Matcher / DiffEngine / ColorDelta(CIEDE2000)
src/Parity.Cli/           dotnet tool 外殼:parity check / init / install-browser
tests/Parity.Tests/       單元測試(含 CIEDE2000 標準測資)
samples/demo/             離線示範:刻意做壞的頁面 + 設計 JSON
```

## 本機報告 UI(M3)

```sh
parity serve --watch    # http://127.0.0.1:4321,檔案變更自動重掃(SSE 即時更新)
parity map              # 互動配對模式:點未配對圖層 → 點截圖上的元素 → 寫入 parity.map.json
```

- 落差清單(依嚴重度排序,精確數值 + 色票 + ΔE)
- 截圖疊框視圖:實線 = 實作框(顏色 = 嚴重度)、藍虛線 = 設計框、紅虛線 = 未配對
- **只綁 127.0.0.1**:報告含站點結構,不讓區網掃到
- UI 是零建置的靜態 SPA,dotnet tool 不需要 node 工具鏈

## 進 CI(M4)

Parity 的差異化就在「進 CI 把關」。這個 repo 本身就是一個 composite action:

```yaml
# .github/workflows/design-check.yml(你的專案)
name: Design fidelity
on: [pull_request]
jobs:
  parity:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      # 起你的站(或改成部署到 preview URL,讓 config 的 url 指過去)
      - run: |
          npm ci && npm run build
          npm run preview &   # 例:serve 在 localhost:8080
      - uses: Cornhsu/Parity@v1
        with:
          config: parity.config.json
          figma-token: ${{ secrets.FIGMA_TOKEN }}   # 設計來源用本機 JSON 時可省略
```

落差超過 `gate.failOn` 門檻 → `parity check` 回傳 exit 1 → PR 被打紅;`report.json` 會當 artifact 上傳供下載。action 輸入:`config` / `target` / `working-directory` / `version` / `figma-token` / `upload-report`。

> action 透過 `dotnet tool install -g Cornhsu.Parity` 安裝,需先把套件發佈到 nuget.org(發佈是 release 步驟,尚未做)。本 repo 的 `.github/workflows/ci.yml` 則是**直接從原始碼建置**並跑離線示範自我把關,不依賴發佈。

## 里程碑

- [x] **M1** 引擎 + CLI 雛形:設計端與實作端兩棵數值樹
- [x] **M2** 比對引擎:配對 + 數值 diff + 容差 + 未配對清單 + gate exit code
- [x] **M3** 本機報告 UI(`parity serve --watch`,Kestrel 綁 127.0.0.1)+ `parity map` 互動配對
- [x] **M4** GitHub Action:可重用 composite action(`action.yml`)+ 本 repo CI(build / test / 離線示範自我把關)
- [ ] **M5** EF Core + SQLite baseline / 歷史;`ImageDesignSource` 驗證抽象層
- [ ] **M6**(選配)雲端外殼:公開網址掃描 + SSRF 防護

## 安全

- Figma token 走環境變數(`env:FIGMA_TOKEN`),不進 log、不進 URL(用 `X-Figma-Token` header)
- 抓過的 frame 存 `.parity/cache`(已 gitignore),重跑不再打 Figma、可離線比對

## License

MIT
