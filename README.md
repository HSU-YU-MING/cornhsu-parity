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

## 比什麼、不比什麼

只比「不管版面怎麼流動都該一樣」的東西:

- **自身尺寸**(寬高;TEXT 節點例外——文字框量測天生不同,比了狂誤報)
- **內距 / 間距**:padding 四邊、auto-layout `itemSpacing` ↔ 實際子元素 gap
- **字體**:size / weight / line-height / letter-spacing 精確比;font-family 是**軟落差**(不擋 gate)
- **顏色**:CIEDE2000 (ΔE) 設門檻,不是 hex 全等
- **刻意不比絕對位置 x/y**:彈性版面下本來就會不同,比了 = 誤報 = 失去信任

## 配對策略(以設計端為錨)

1. **自動文字錨定**:設計 TEXT 文字 ↔ 頁面文字(唯一才配,不硬湊)
2. **圖層名 ↔ id / class / aria-label**:`CTA Button` 自動對上 `class="cta-button"`
3. **手動補漏**:HTML 加 `data-parity="圖層名"`,或 `parity.map.json` 寫 `{ "圖層名": "CSS selector" }`
4. 配不到 → **誠實列進未配對清單**

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

## 里程碑

- [x] **M1** 引擎 + CLI 雛形:設計端與實作端兩棵數值樹
- [x] **M2** 比對引擎:配對 + 數值 diff + 容差 + 未配對清單 + gate exit code
- [x] **M3** 本機報告 UI(`parity serve --watch`,Kestrel 綁 127.0.0.1)+ `parity map` 互動配對
- [ ] **M4** GitHub Action:CI 還原度把關
- [ ] **M5** EF Core + SQLite baseline / 歷史;`ImageDesignSource` 驗證抽象層
- [ ] **M6**(選配)雲端外殼:公開網址掃描 + SSRF 防護

## 安全

- Figma token 走環境變數(`env:FIGMA_TOKEN`),不進 log、不進 URL(用 `X-Figma-Token` header)
- 抓過的 frame 存 `.parity/cache`(已 gitignore),重跑不再打 Figma、可離線比對

## License

MIT
