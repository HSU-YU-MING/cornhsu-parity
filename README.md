# Parity

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.Parity.svg?label=Cornhsu.Parity)](https://www.nuget.org/packages/Cornhsu.Parity)
[![Downloads](https://img.shields.io/nuget/dt/Cornhsu.Parity.svg)](https://www.nuget.org/packages/Cornhsu.Parity)
[![CI](https://github.com/HSU-YU-MING/cornhsu-parity/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-parity/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**[作品介紹與開發故事](https://cornhsu.com/parity) · [NuGet](https://www.nuget.org/packages/Cornhsu.Parity) · MIT**

> 數值級設計還原度檢查工具——抓 **Figma 與實作端的真實數值**做程式比對,不是疊圖給人眼看。
> 本機 / CI / 內網都能跑,在東西公開之前就把「顏色不對、字太大、間距跑掉」擋下來。

|  | 疊圖工具(Pixelay 等) | Parity |
|---|---|---|
| 做法 | 疊兩張圖給人眼看 | 抓兩邊真實數值做程式比對 |
| 結果 | 「這裡怪怪的」 | 「paddingLeft 8px,設計 20px」 |
| 跑在哪 | 瀏覽器外掛 / 雲端(碰不到 localhost) | **你的機器 / CI runner**,localhost 天生連得到 |
| 進 CI | 大多不行 | PR 落差超門檻就擋 |

## 畫面

**報告 UI(`parity serve --watch`)**——左欄逐項列出落差與一致的節點,右側把設計框與實作框直接疊在真實畫面上;點任一項即定位,存檔後自動重掃。

![Parity 報告 UI:左欄落差清單,右側設計框與實作框疊在畫面上](https://raw.githubusercontent.com/HSU-YU-MING/cornhsu-parity/master/docs/serve-ui.png)

**還原度報告**——每一列是「期望 → 實際」的具體數值、嚴重度與建議修法(含對應的 design token)。GitHub Action 以此原文自動貼成 PR 留言,同一則原地更新、不洗版。

![Parity 還原度報告:每列為期望→實際的數值落差與建議修法](https://raw.githubusercontent.com/HSU-YU-MING/cornhsu-parity/master/docs/report.png)

## 成果一覽

| | |
|---|---|
| 發佈 | NuGet 共 13 版(v0.1.0 → v0.9.3),推 tag 即以 OIDC Trusted Publishing 自動上架,repo 內零長效金鑰 |
| 比對維度 | 尺寸、內距、間距、字體、顏色(CIEDE2000 ΔE)、相對位置——**刻意不比絕對座標**(彈性版面下必然誤報) |
| 設計來源 | 4 種:Figma API、畫面快照、圖片 + 標註(像素取樣,任何工具匯出 PNG 即可)、JSON |
| 實作端 | 網頁(含 **Shadow DOM / 同源 iframe / RWD 多斷點**)+ **Electron**(以 CDP attach 活視窗) |
| 測試 | **154 條**,涵蓋 CIEDE2000 標準測資集(Sharma)、配對消歧、位置誤報防護、圖片取樣 |
| CI 實證 | GitHub Action 以**外部 repo 跑真實 PR** 驗證:擋 PR、自動留言(原地更新不洗版)、baseline 回歸把關 |
| 真實驗證 | **cornhsu.com 全站 20 頁由 Parity 自己守門**——首日 dogfooding 即揪出並修掉兩個 flaky 根因 |

## 快速開始

```sh
dotnet tool install -g Cornhsu.Parity   # (發佈後)
parity init                # 產生 parity.config.json 範本
parity install-browser     # 第一次:下載 Playwright Chromium
export FIGMA_TOKEN=...     # scope 只需 file_content:read
parity check               # 比對,輸出報告 + exit code
parity report              # 從既有 report.json 重生 Markdown 報告(免重掃;--md 寫檔,預設印 stdout)
parity snapshot            # 把「現在跑著的畫面」凍結成設計基準——重構/改版守門,不需要 Figma
parity lint                # design lint:設計稿的值是否落在 design token 允許集合(只看設計,不比實作)
parity check --reverse     # 反向檢視:設計師照現有頁面重畫時,看自己的稿跟現況差在哪(不做把關)
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
  "tokensFile": "tokens.json",               // 選配:design token(名→值);建議修法會提示對應 token
  "targets": [
    { "route": "/", "frame": "10:2", "url": "http://localhost:8080/" }
  ],
  "compare": { "position": "relative" },     // relative = 比相對位置(預設);none = 不比位置
  "tolerances": { "sizePx": 2, "spacingPx": 2, "colorDeltaE": 2.0, "positionPx": 4 },
  "ignore": ["[data-parity-ignore]"],
  "gate": {
    "failOn": ["critical", "serious"],
    "minMatchRate": 0                        // 選配:配對率低於此值(0–1)直接 FAIL;0 = 不設門檻
  }
}
```

> gate 除了看落差,也驗**配對可信度**:完全 0 配對(或設計端 0 節點)一律 GATE FAIL——
> 沒配到就沒落差可擋,沉默 PASS 會是假的通過(通常是 url/frame 指錯)。`--baseline` 模式也不豁免。

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

## 設計來源:Figma、快照、或一張圖

| 設計來源 | config | 適合誰 |
|---|---|---|
| **Figma**(主力) | `figmaFileKey` + `FIGMA_TOKEN` | 有 Figma 檔的正規流程 |
| **快照**(`parity snapshot`) | `designFile` 指向產出的快照 JSON | **重構/改版守門**:現在的畫面是對的,存成基準,之後 check 保證不跑版(visual regression 的數值版)。配對走 selector 身分,100% 確定性 |
| **一張圖 + 標註** | `designImage`(PNG/JPG)+ `designFile`(標註) | 只有圖的場景:外包 PNG、老專案只剩截圖。**XD / Sketch / PS 等其他工具匯出圖片就能走這條**(萬用轉接頭)。標註 = DesignNode JSON,`fill` 可省略——顏色由引擎從圖片對應區域取樣(TEXT 字色刻意不取樣:反鋸齒混色取不準,可手填) |
| 手寫 JSON | `designFile` | 離線示範/測試 |

```sh
# 重構守門三步:
parity snapshot            # 1. 凍結現在的畫面(產出 parity.snapshot.json + 參考截圖)
#    config 改 designFile 指向它、target.frame 填 route
parity check               # 2. 大膽重構;3. check 保證與快照一致
```

## 比什麼、不比什麼

只比「不管版面怎麼流動都該一樣」的東西:

- **自身尺寸**(寬高;TEXT 節點例外——文字框量測天生不同,比了狂誤報)
  - auto-layout 的 **HUG(隨內容)/ FILL(隨父層)** 那一軸也跳過——Figma 量的寬 ≠ 瀏覽器渲染寬是必然的,只比 **FIXED** 的軸
- **內距 / 間距**:padding 四邊、auto-layout `itemSpacing` ↔ 實際子元素 gap
- **相對位置**(`offsetX`/`offsetY`):自由擺放(非 auto-layout)容器的子元素,比「相對最近可靠兄弟/父層邊」的偏移——抓得到「尺寸顏色全對但擺錯位置」。參照只用可靠的邊(TEXT/HUG 的框不當參照、TEXT 不當目標、上方全是文字時 Y 誠實跳過),流動版面的行高漂移不會誤報。`compare.position: "none"` 可關閉
- **字體**:size / weight / line-height / letter-spacing 精確比;font-family 是**軟落差**(不擋 gate)
- **顏色**:CIEDE2000 (ΔE) 設門檻,不是 hex 全等;解析含現代語法(`rgb(37 99 235 / .5)`、`color(srgb …)`、`oklch()`、`color(display-p3 …)`)

> 設計來源是 Figma 時,報告(Markdown 與本機 UI)裡的圖層名會**連回 Figma 的那個節點**——設計師點一下直接跳到圖層,不用自己翻。
- **刻意不比絕對位置 x/y**:彈性版面下本來就會不同,比了 = 誤報 = 失去信任

## 給設計師的兩個方向

**新頁面守設計系統:`parity lint`**——只看設計稿這一邊,驗每個節點的值是否落在 `tokensFile` 的允許集合:顏色(ΔE 容差內命中即過)、fontSize / padding / itemSpacing / cornerRadius(等於任一尺寸 token 即過;間距/字級/圓角共用同一份 scale)。違規附「最近的 token」——訊息是「改成這個」,不是只有「你錯了」。有違規 exit 1,可進 CI。沒定義某維度的 token 就不 lint 該維度。

**照現況重畫/改版:`parity check --reverse`**——方向反過來:「期望」= 現況(實作)、「實際」= 你的設計稿。給設計師一張「我的稿跟現在線上差在哪」的清單;不做把關,永遠 exit 0。

## RWD 多斷點

同一個 URL、不同斷點,各對一個 Figma frame 即可——**渲染視窗 = frame 尺寸**,手機 frame 畫 375 寬,media query 自然生效:

```jsonc
"targets": [
  { "route": "/desktop", "frame": "10:2",  "url": "http://localhost:8080/" },
  { "route": "/mobile",  "frame": "10:99", "url": "http://localhost:8080/" }   // frame 是 375 寬的手機版
]
```

route 只是報告上的標籤,取好認的名字就行。frame 寬 ≠ 想測的視窗寬時,才需要在 target 加 `width` / `height` 覆蓋。

## Shadow DOM / iframe

擷取走**組合樹**:open shadow root、`<slot>` 塞進來的內容、同源 iframe(含 `srcdoc`)都看得到、都會比——web components 網站不再整塊隱形。shadow / iframe 內的元素 selector 以 `host >>> 內部路徑` 表示。

限制(誠實列):closed shadow root 與跨域 iframe 拿不到,跳過;map 檔的 selector 搆不到 shadow 內(`data-parity` 屬性不受限,照常可用)。

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
    permissions:
      contents: read
      pull-requests: write   # 讓 action 把還原度報告貼成 PR 留言
    steps:
      - uses: actions/checkout@v4
      # 起你的站(或改成部署到 preview URL,讓 config 的 url 指過去)
      - run: |
          npm ci && npm run build
          npm run preview &   # 例:serve 在 localhost:8080
      - uses: HSU-YU-MING/cornhsu-parity@v1
        with:
          config: parity.config.json
          figma-token: ${{ secrets.FIGMA_TOKEN }}   # 設計來源用本機 JSON 時可省略
```

行為:
- **PR 留言**:自動貼一則還原度報告(分數 + 落差表 + **建議修法**),同一則反覆更新不洗版——PM/reviewer 不用碰工具就看得到。
- **擋 PR**:落差超過 `gate.failOn` → exit 1 → PR 打紅(**留言會先貼、再擋**)。
- **artifact**:`report.json` + Markdown 報告上傳供下載。

action 輸入:`config` / `target` / `working-directory` / `version` / `figma-token` / `baseline`(回歸模式) / `comment`(關掉 PR 留言) / `upload-report`。

> action 透過 `dotnet tool install -g Cornhsu.Parity` 安裝,需先把套件發佈到 nuget.org(發佈是 release 步驟,尚未做)。本 repo 的 `.github/workflows/ci.yml` 則是**直接從原始碼建置**並跑離線示範自我把關,不依賴發佈。

## 回歸把關:baseline(M5)

已經有一堆落差的專案,不可能一開就「零落差才給過」。baseline 讓你**只擋新增/惡化**:

```sh
parity baseline save     # 把當前落差 + 還原度分數存成基準快照(SQLite,存 parity.baseline.db,自動標 git commit)
parity check --baseline  # 比對現況 vs 最新基準:只有「新增或惡化」才 GATE FAIL;並顯示分數走勢(基準 75 → 現在 83 ↑)
parity baseline list     # 看歷史快照(含分數欄 = 還原度走勢,給 PM 看方向)
```

> **CI 要用 `--baseline`,記得 `git add parity.baseline.db` 一起 commit**——它刻意放 repo 根(不放 `.parity/`,那裡通常被 gitignore),否則 CI 找不到基準會靜默退回一般 gate。路徑可用 config `baselineFile` 改。

- **新增**(基準沒有、現在有)或**惡化**(嚴重度變高)→ exit 1 擋 PR
- **修好**(基準有、現在沒了)會列出來鼓勵;**不變**的既有落差不擋
- 適合漸進導入:先 `baseline save` 記錄現況,之後 CI 用 `check --baseline`,團隊只需「不要讓還原度更差」

儲存層是獨立的 `Parity.Storage`(EF Core + SQLite,`Pooling=False` 即時釋放檔案),引擎裡的 `BaselineComparer` 是純函式、可單元測試。

## 里程碑

- [x] **M1** 引擎 + CLI 雛形:設計端與實作端兩棵數值樹
- [x] **M2** 比對引擎:配對 + 數值 diff + 容差 + 未配對清單 + gate exit code
- [x] **M3** 本機報告 UI(`parity serve --watch`,Kestrel 綁 127.0.0.1)+ `parity map` 互動配對
- [x] **M4** GitHub Action:可重用 composite action(`action.yml`)+ 本 repo CI(build / test / 離線示範自我把關)
- [x] **M5** EF Core + SQLite baseline / 歷史(回歸把關 + 分數走勢)+ `ImageDesignSource`(圖片+標註+像素取樣)+ `parity snapshot`(凍結現況當基準)

> 未完成、已知盲點與下一步優先序見 [ROADMAP.md](ROADMAP.md);版本變更見 [CHANGELOG.md](CHANGELOG.md)。
- [ ] **M6**(選配)雲端外殼:公開網址掃描 + SSRF 防護

## 安全

- Figma token 走環境變數(`env:FIGMA_TOKEN`),不進 log、不進 URL(用 `X-Figma-Token` header)
- 抓過的 frame 存 `.parity/cache`(已 gitignore),重跑不再打 Figma、可離線比對

## License

MIT
