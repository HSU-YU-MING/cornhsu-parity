# 旗艦專案規劃書 v2.2 — Parity(設計還原度檢查工具)
## 本機優先版・名稱定案・DX 修正

> **產品名:Parity**。命令列指令 `parity`,NuGet 套件 id `Cornhsu.Parity`(理由見第 9 節)。
>
> v1 的錯誤:把架構押在「雲端服務去掃一個公開網址」,而那只碰得到開發流程**最後、最不重要**的公開階段。設計還原度檢查是**上線前**的活——本機刻版、內網 staging 複核時就要抓落差。所以這版把方向倒過來:**工具跑在開發者所在的地方。**
>
> **起源**:實習時 PM 每天用人眼抓「顏色不對、字太大、間距跑掉」。這工具把它自動化,而且塞進本機與 CI,在東西公開之前就擋下來。
>
> **兩扇 adapter 門**(本版新增第二扇):設計端 `IDesignSource`(Figma 現做,XD/Sketch 之後)、實作端 `IImplementationSource`(網頁現做,WPF/桌面之後)。引擎只比對「兩棵正規化的樹」,不管樹從哪來。

---

## 0. 核心原則:引擎 + 外殼,本機優先

**一顆引擎**(純函式庫,不假設跑在哪):設計來源抓數值 → 正規化 → 實作來源抓數值 → 正規化 → 配對 → 比對。(v1 的兩個來源=Figma + 網頁,但引擎不綁死。)

**多個外殼**,照開發流程由前到後、由主到輔:

| 開發階段 | 誰在用 | 外殼 | 跑在哪 / 連得到什麼 | 優先序 |
|---|---|---|---|---|
| **本機** | 自己刻版時 | CLI + 本機報告 UI | 你的機器 → localhost 連得到 | ★ 最先做 |
| **內網 / 測試機** | PR 審查、PM 複核 | CI / GitHub Action(或 VPN 內跑 CLI) | 網路內的 runner → 內網 staging 連得到 | ★ 差異化支柱 |
| **公開** | 對外監測、非技術人員 | 雲端網頁服務(**選配**) | 雲端 → 只碰公開網址 | 選配 / v2 |

**這解決了 localhost/內網 的根本問題**:掃描程序跑在你的機器/你的網路裡,「localhost」就是你的 localhost,內網網段就在同一網段,沒有任何外部伺服器需要連進來。SSRF 防護只綁在選配的雲端外殼上,本機與 CI 外殼沒這問題。

---

## 0.1 決策總表

| 項目 | 決定 | 為什麼 |
|---|---|---|
| 架構 | 引擎(函式庫)+ 多外殼,**本機優先** | 設計 QA 是上線前的活,工具得跑在開發者所在的地方 |
| 引擎語言 | C# / .NET 10 | 展示核心;引擎抽乾淨、可單元測試、跟外殼無關 |
| 主外殼 | **CLI**,發成 `dotnet tool` | 本機、CI、內網都能跑;dotnet tool = NuGet,接回你的 Cornhsu.* 品牌 |
| 本機 UI | **ASP.NET Core(Kestrel 綁 127.0.0.1)** + React | 本機視覺化報告;localhost 由本機伺服器托管,天生連得到 |
| CI 外殼 | GitHub Action(CLI 包裝) | 內網/測試機的還原度把關,PR 超標就擋 |
| 雲端服務 | **選配 / v2** | 只服務公開階段 + 報告分享 + 非技術人員;不是centrepiece |
| 設計來源(v1) | Figma REST API,藏在 `IDesignSource` 後 | XD/Sketch/PNG 之後當 adapter |
| 實作來源(v1) | 網頁 = Playwright,藏在 `IImplementationSource` 後 | WPF/桌面之後當 adapter(你自己的 WPF app 剛好能當白老鼠) |
| 比對方式 | 確定性數值級 diff(非疊圖) | 跟 Pixelay/OverlayQA 的唯一差異化 |
| 配對策略 | 以設計端為錨、自動優先、只補漏;補漏用圖層名或 `parity map`,**不貼節點 ID** | 「可靠」不能以人手貼天書 ID 為代價,否則沒人用 |
| 比對基準 | 比尺寸/內距/相對間距/字體/顏色;**預設不比絕對位置** | 絕對 x/y 在彈性版面下本來就會不同,拿來比 = 狂誤報 = 失去信任 |
| 設定 | repo 裡的 `parity.config.json` | 設定即程式碼,team + CI 共用同一份 |
| 輸出 | JSON + 非零 exit code + 人看的報告 | 機器與人都要 |
| 容差 | 每維度可設,預設非零 | 子像素與字體渲染差異必然存在 |
| 資料庫 | EF Core + SQLite(本機檔案) | 存 baseline / 歷史;Cornhsu.Labeling 有家 |
| 授權 | MIT | |
| 名稱 | **Parity**(指令 `parity`,套件 `Cornhsu.Parity`) | 名如其功:檢查實作與設計是否「對等」;見第 9 節 |

**時程**:M1–M5 約 8–10 週(業餘)。最大變數是**配對品質與誤報率**(即 4.7、4.8 這兩塊 DX),不是寫程式量。

---

## 1. 這是什麼,跟現有工具的界線

Pixelay、OverlayQA、UI Match、Perfect Pixel 都在做,你不能靠「第一個」贏,只能靠「做法不同」贏。

| | 現有主流 | Parity |
|---|---|---|
| 做法 | 疊圖給**人眼**看(或 AI 比兩張截圖) | 抓**兩邊真實數值**做程式比對 |
| 結果 | 「這裡怪怪的」 | 「內距 8px,設計 12px」——精確到數字 |
| 準確度 | 模糊 | 確定性 |
| 跑在哪 | 多為瀏覽器外掛/雲端(碰不到你的 localhost) | **本機 / CI / 內網**,碰得到你正在開發的東西 |
| 進 CI | 大多不行 | 可以:PR 超標就擋 |

差異化守死兩條線:**(1) 數值級、不是疊圖;(2) 本機優先、碰得到 dev 環境。** 任何「疊圖 + 人眼」的功能都只能是輔助視圖。

### 1.1 誰在什麼階段用、摩擦多高(DX 定位,誠實版)

**最低摩擦、最高價值的用法是 CI 把關 + 給 PM/設計讀的報告;「開發者本人高頻在內圈用」是選配、摩擦較高。** 這點要標清楚,不假裝零負擔。

| 用法 | 誰 | 摩擦 | 價值 | 定位 |
|---|---|---|---|---|
| CI 還原度把關 | 團隊(設定一次) | 極低(之後全自動) | 高 | ★ 主打 |
| 給 PM/設計的驗收報告 | PM、設計師 | 低(讀報告) | 高(在意像素的就是他們) | ★ 主打 |
| pre-commit 一次性檢查 | 開發者 | 中(每次幾秒) | 中 | 選用 |
| 本機內圈高頻(watch) | 開發者 | 較高(要配對、要等) | 中 | 選配 |

這剛好把你實習那個迴圈補完整:工具**服務工程師**(告訴他改哪個 selector)、也**服務 PM**(幫他驗收),PM 不用再用眼睛電你。

---

## 2. 名詞對照

| 中文 | 英文 | 說明 |
|---|---|---|
| 引擎 | Engine | 純比對邏輯的函式庫,不管跑在哪 |
| 外殼 | Shell | 包住引擎的入口:CLI / 本機 UI / CI / 雲端 |
| 設計節點 | Design node | 從設計來源正規化出的元素(box、色、字) |
| 實作節點 | Rendered node | 從實作端讀出的元素(box、實際樣式);網頁來自 DOM,桌面來自 UI 樹 |
| 配對 | Match | 設計節點 ↔ 實作節點是同一個東西 |
| 落差 | Diff | 配對上、但某些數值對不上 |
| 容差 | Tolerance | 每維度允許的誤差 |
| 設計來源 | Design source | 提供設計數值的 adapter(v1=Figma;未來 XD/Sketch/PNG) |
| 實作來源 | Implementation source | 提供實作數值的 adapter(v1=網頁/Playwright;未來 WPF/桌面) |
| 還原度把關 | Fidelity gate | CI 裡落差超門檻就讓 build 失敗 |
| 設計錨定 | Design-anchored | 以設計節點為基準去找頁面對應,不反向掃整頁 |
| 對應檔 | Map file | `parity map` 存的「設計節點 ↔ 頁面元素」手動對應,不污染程式碼 |
| 未配對 | Unmatched | 只在一邊找得到的節點(誠實列出) |

---

## 3. 範圍邊界

**v1 要做:**
- 引擎:Figma frame(REST API)抓數值 + Playwright 渲染 + **以設計端為錨的配對** + 尺寸/內距/字體/顏色 diff(**預設不比絕對位置**)+ 容差
- CLI(`dotnet tool`):本機/CI/內網都能跑,讀 `parity.config.json`,輸出 JSON + exit code + 報告
- `parity map` 互動配對(補自動配不到的漏)
- 本機報告 UI(`parity serve --watch`,Kestrel 綁 127.0.0.1)
- GitHub Action:還原度把關
- 目標在 **frame 設計寬度**下渲染;單一 frame ↔ 單一路由

**v1 不做(刻意):**
- 不做 RWD / 多斷點(先單一寬度)
- 不做反向掃整頁湊配對(以設計端為錨,範圍受控)
- 不比絕對位置(彈性版面下會狂誤報)
- 不做 XD / Sketch(先留 `IDesignSource`,adapter 之後補)
- 不做 WPF / 桌面(先留 `IImplementationSource`,adapter 之後補)
- 不做自動修復(只報落差)
- 不做元件狀態(hover/focus 等)
- **雲端公開服務推到 v2**(見第 6 節 M6,選配)

---

## 4. 系統架構

### 4.1 引擎資料流(跟外殼無關)

```
輸入:{ Figma fileKey+nodeId, 目標 URL(可為 localhost/內網), 容差 }
  │
  ├─ 1. IDesignSource(FigmaDesignSource):Figma REST API → DesignNode 樹
  │       (抓過的 frame 存本機快取,之後跑不再打 Figma)
  ├─ 2. IImplementationSource(WebImplementationSource / 未來 WpfImplementationSource)
  │       → RenderedNode 樹(網頁走 Playwright,視窗寬=frame 寬)
  ├─ 3. Normalizer:兩邊座標/單位換算到「相對各自 root 原點」
  ├─ 4. Matcher:DesignNode ↔ RenderedNode 配對
  ├─ 5. DiffEngine:三維度比對 + 套容差 → 落差清單(+ 未配對清單)
  ▼
輸出:Report(可序列化成 JSON / 餵給 UI / 決定 exit code)
```

引擎是一個 `class library`,對外只暴露一個進入點:

```csharp
public sealed class FidelityEngine {
    // 兩邊都是插進來的 adapter:設計來源 + 實作來源
    public FidelityEngine(IDesignSource design, IImplementationSource impl, EngineOptions opts) { … }
    public Task<FidelityReport> RunAsync(ScanRequest req, CancellationToken ct);
}
```

四個外殼都只是「準備 `ScanRequest` → 呼叫 `RunAsync` → 處理 `FidelityReport`」。

### 4.2 外殼(照工作流由主到輔)

**A. CLI(`dotnet tool`)— 最先做、最常用**
```sh
dotnet tool install -g Cornhsu.Parity
parity check                       # 一次性:讀 parity.config.json,對 localhost 跑,輸出報告 + exit code
parity map                         # 互動配對:把配不到的設計節點手動連到頁面元素,存 parity.map.json
parity serve --watch               # 本機報告 UI;watch 下存檔就重測(瀏覽器熱著,秒級回饋)
```
本機跑 → 直接連得到 `localhost:8080`、`127.0.0.1`、`192.168.x.x`。CI 裡也是同一支。

**速度**:一次 `check` 要開 Chromium + 抓 Figma,好幾秒。所以內圈用 `parity serve --watch` 讓瀏覽器**熱著**、存檔就重測;一次性的 `check` 留給 pre-commit / CI。Figma 端有本機快取,不會每次重抓。

**B. 本機報告 UI(`parity serve`)**
Kestrel 綁 `127.0.0.1:<port>`,托管 React 報告 + 一個本機 scan API。因為伺服器就在你機器上,它去載入 `localhost:8080` 毫無障礙。這是 ASP.NET Core 的用武之地——只是綁本機,不對外。

**C. CI / GitHub Action — 差異化支柱**
把 CLI 包成 Action:build app → 起在 runner 的 localhost → `parity check` → baseline + 門檻 → pass/fail + 報告 artifact。內網 staging 用**網路內的 runner / self-hosted runner** 就連得到。這正是你實習那個痛的自動化:PR 讓實作偏離設計稿就擋。

**D. 雲端網頁服務(選配 / v2)**
同一顆引擎換上「雲端外殼」:公開部署、SSRF 防護、非 root 容器,服務公開網址掃描 + 報告分享 + 給不寫程式的 PM/設計用。**只有這個外殼有 SSRF 問題,因為只有它是對外、多人共用的伺服器。**

### 4.3 設計來源抽象 `IDesignSource`(留給 XD 的門)

```csharp
public record DesignNode(
    string Id, string Name, DesignNodeType Type,
    Box Box,                 // 相對 frame 原點的 x,y,w,h
    Rgba? Fill, Typography? Text, Insets? Padding,
    double? ItemSpacing, double? CornerRadius,
    IReadOnlyList<DesignNode> Children);

public interface IDesignSource {
    Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct);
}
public sealed class FigmaDesignSource : IDesignSource { /* Figma REST API */ }
// 未來:XdDesignSource / SketchDesignSource / ImageDesignSource
```
M5 用一個最簡單的第二 adapter(`ImageDesignSource`:PNG + 手動標註)**證明這層設計成立**——這就是「未來做 XD」落到架構上的兌現。

### 4.4 Figma 端:抓真實數值

- `GET /v1/files/:key/nodes?ids=:id` → 節點 JSON(box、fills、style、padding、itemSpacing、cornerRadius…)
- `GET /v1/images/:key?ids=:id&format=png` → frame 的 PNG(疊圖輔助視圖用)
- 抓到的 frame **存本機快取**(依 file+node+version),之後本機重跑不再打 Figma、也能離線比對。

```csharp
static Rgba FromFigma(FigmaColor c) => new(
    (byte)Math.Round(c.R*255), (byte)Math.Round(c.G*255), (byte)Math.Round(c.B*255), c.A);
// style.fontSize/fontWeight/letterSpacing/lineHeightPx 直接對應 CSS
// fontFamily 要小心:Figma 名 ≠ CSS font stack(見風險表)
```
**Token**:走環境變數 / 本機 secret(如 `FIGMA_TOKEN`),scope 只要 `file_content:read`,不進 log、不進 URL。

### 4.5 實作來源抽象 `IImplementationSource`(留給 WPF/桌面的門)

跟設計端對稱。介面吐出的一樣是正規化的 `RenderedNode` 樹,引擎不管它從網頁還是桌面來:

```csharp
public interface IImplementationSource {
    Task<RenderedNode> CaptureAsync(ImplRef reference, CancellationToken ct);
}
public sealed class WebImplementationSource : IImplementationSource { /* Playwright */ }   // v1
// 未來:WpfImplementationSource / WinUiImplementationSource
```

**v1:網頁 = Playwright 讀 DOM**
```csharp
await page.SetViewportSizeAsync(frameWidth, frameHeight);
await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle }); // url 可為 localhost
var rendered = await page.EvaluateAsync<RenderedNode[]>(@"
() => [...document.querySelectorAll('*')].map(el => {
  const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
  return { selector: cssPath(el),
    text: el.childElementCount ? '' : el.textContent.trim(),
    box: { x:r.x+scrollX, y:r.y+scrollY, w:r.width, h:r.height },
    color:s.color, background:s.backgroundColor,
    fontFamily:s.fontFamily, fontSize:s.fontSize, fontWeight:s.fontWeight,
    lineHeight:s.lineHeight, letterSpacing:s.letterSpacing,
    paddingTop:s.paddingTop /* …四邊… */,
    explicitMatch: el.getAttribute('data-parity') };
})");
```
本機跑 Playwright:第一次 `playwright install` 下載 Chromium;之後直接載入你自己的 localhost。

**未來:WPF/桌面 = `WpfImplementationSource`(這扇門怎麼開)**

對應關係很乾淨,配對/比對邏輯完全不用重寫:

| 網頁 | WPF/桌面 |
|---|---|
| DOM 樹 | UI Automation 元素樹 / visual tree |
| `data-parity` 手動錨點 | `AutomationId` |
| `getComputedStyle` 拿精確樣式 | 見下方「好消息/壞消息」 |

- **好消息**:元件的框框座標(位置/大小)用 Windows UI Automation 直接拿得到;配對與幾何比對照搬。
- **壞消息**:UI Automation **不會**乾脆給你精確的顏色與字級。最乾淨的解法是——**因為 app 是你自己寫的,`WpfImplementationSource` 直接掛進你的 WPF 行程、走 visual tree 讀真正的 `FontSize`、筆刷顏色、`Margin`**。拿到的數字 100% 精確,代價是它得跑在你的 app 裡(比對網址侵入性高一點)。
- **白老鼠**:QuillNest 設計稿畫在 Figma、程式是你的 WPF,正好完整驗一次「桌面版還原度」——這是別人難做、你因為同時握有設計稿與原始碼而做得動的角度。

### 4.6 座標/單位正規化(容易錯,先講清楚)

- Figma `absoluteBoundingBox` 是檔案絕對座標 → 減掉 frame 原點,變「相對 frame 左上」。
- DOM `getBoundingClientRect` 相對視窗 → 加 scroll 位移,變「相對頁面 root」。
- 注意 device pixel ratio、頁面整體 `transform: scale` 要先還原。
- 顏色統一轉 sRGB hex;字級把 `px` 字串 parse 成數字。
- **目標是算出「相對」度量**(自身尺寸、內距、跟兄弟的 gap)拿去比,**不是拿絕對 x/y 直接比**(見 4.8)。座標對齊只是中間步驟。
- 這一層抽成獨立、可單元測試的函式,算錯整份報告全錯。

### 4.7 核心難題:節點配對(成敗所在,DX 是關鍵)

**方向:以設計端為錨。** 設計稿就那幾十個有意義的節點,拿它們一個個去頁面上找對應——**不是**反過來走訪整頁上千個 DOM 元素去湊。這讓報告長度 = 設計節點數,有界又有意義。

核心原則:**自動先吃掉大部分,只有配不到的才要人介入;要人介入時也別叫人貼天書 ID。**

1. **自動錨定(主力,不用人做任何事)**:用文字、按鈕文案、標題這些天然錨點,把設計節點對到頁面元素。多數節點靠這關就配上。
2. **圖層名對應**:設計節點的**圖層名**(如 Figma 圖層 `cta-button`)去對頁面上人看得懂的錨點。
3. **只補漏,而且用人看得懂的名字**:剩下自動配不到的那幾個才要人標,標的是 `data-parity="cta-button"`(對**圖層名**),**不是** `data-parity="12:345"`(不透明節點 ID)。名字穩定、改版不失效、看得懂。
4. **`parity map` 互動配對(更好的補漏)**:本機畫面把「配不到的設計節點」與「頁面候選元素」左右並排,你**點一下連起來**,存進一份**對應檔** `parity.map.json`——完全不動正式程式碼。

配不到 → 進**未配對清單**,誠實呈現,不硬湊、不假裝全對上。

> DX 重點:**「可靠」不能以「人手貼天書 ID」為代價**,否則九成的人不會做,就只剩不可靠的自動配對。所以是「自動優先 → 只補漏 → 補漏用圖層名或點選,不貼 ID」。

### 4.8 比對維度 + 容差(比「該一樣的」,不比「本來就會不一樣的」)

**關鍵決策:預設不比絕對位置(x/y)。** 設計師用固定寬度畫、工程師用彈性版面(flex/grid)刻,就算兩邊都對,元素的絕對座標也幾乎不會一致。把絕對 x/y 當主要指標 = 幾何維度整片紅 = 開發者發現它老為沒錯的東西報警 → 不再信它。**工具一旦「喊狼來了」就死了。**

所以比的是**不管版面怎麼流動都該一樣**的東西:

- **元素自身尺寸**:寬、高(它自己多大,跟擺哪無關)。
- **內距 / 間距**:padding 四邊、跟相鄰元素的 gap(相對父層/兄弟,**不是**相對整頁)。
- **字體**:font-size、weight、line-height、letter-spacing 精確比;**font-family 當「軟落差」**。
- **顏色**:設計 fill vs 實際 color/background,用 **CIEDE2000 (ΔE)** 設門檻(比 hex 全等聰明;重用你的色彩數學)。

真要比位置,比**相對位置**(相對父層/兄弟),不比相對整頁的絕對座標。這些正是設計師真正在意的(「這顆按鈕內距不對」),也正是不被 RWD 干擾的。

```jsonc
{ "designLayer":"cta-button", "selector":"main > button.cta", "matchedBy":"auto-text",
  "diffs":[
    { "prop":"paddingLeft","expected":20,"actual":8,"unit":"px","status":"mismatch" },
    { "prop":"color","expected":"#2563EB","actual":"#3B82F6","deltaE":4.1,"status":"mismatch" },
    { "prop":"fontSize","expected":16,"actual":15,"unit":"px","status":"mismatch" } ],
  "severity":"medium" }
```

### 4.9 設定即程式碼:`parity.config.json`

放 repo 根目錄,team + CI 共用同一份:

```jsonc
{
  "figmaFileKey": "abcd1234",
  "designToken": "env:FIGMA_TOKEN",
  "mapFile": "parity.map.json",                 // parity map 存的手動對應
  "targets": [
    { "route": "/", "frame": "10:2", "url": "http://localhost:8080/" },
    { "route": "/pricing", "frame": "12:345", "url": "http://localhost:8080/pricing" }
  ],
  "compare": { "position": "relative" },        // 預設不比絕對 x/y
  "tolerances": { "sizePx": 2, "spacingPx": 2, "colorDeltaE": 2.0 },
  "ignore": ["[data-parity-ignore]"],
  "gate": { "failOn": ["critical","serious"] }
}
```

---

## 5. Repo 結構

根命名空間 `Parity`(套件 `Cornhsu.Parity`)。

```
parity/
├── src/
│   ├── Parity.Engine/               # 純函式庫:對外只有 FidelityEngine
│   │   ├── DesignSources/           # IDesignSource + DesignNode + Figma/(未來 Xd/Sketch/Image/)
│   │   ├── ImplementationSources/   # IImplementationSource + Web/(Playwright)(未來 Wpf/)
│   │   ├── Comparison/              # Normalizer / Matcher / DiffEngine / ColorDelta
│   │   └── FidelityEngine.cs
│   ├── Parity.Cli/                  # `dotnet tool`:parity check / serve(外殼 A、B 進入點)
│   ├── Parity.LocalUi/              # ASP.NET Core(Kestrel 127.0.0.1)+ wwwroot(外殼 B)
│   └── Parity.Cloud/                # 選配:雲端外殼 + SSRF 防護(外殼 D,v2)
├── web/                             # React + Vite(報告 UI 原始碼)
├── action/                          # GitHub Action(外殼 C)
├── tests/Parity.Tests/             # Normalizer / Matcher / DiffEngine / ColorDelta
├── parity.config.json               # 範例設定
├── README.md
└── LICENSE
```

---

## 6. 里程碑(本機優先,每站可用)

### M1 — 引擎 + CLI 雛形,對 localhost 跑得起來
- `Engine` 骨架 + `FigmaDesignSource`(REST + 本機快取)+ `WebImplementationSource`(Playwright)
- `parity check` 對一個 localhost URL 跑,輸出兩棵正規化的樹
- **不碰 Docker、不碰雲端、不碰 SSRF**
- **DoD**:在你自己機器上,給 Figma frame + `http://localhost:8080`,回傳設計端與實作端兩棵數值樹。

### M2 — 比對引擎(配對 + 數值 diff)
- Normalizer(4.6)+ Matcher(4.7,設計端為錨、自動優先 + `parity map` 補漏)+ DiffEngine(4.8,比尺寸/內距/字體/顏色,**不比絕對位置**)+ 容差
- `parity.config.json` / `parity.map.json` 讀取;輸出 JSON + 非零 exit code + 未配對清單
- **DoD**:`parity check` 對 localhost 回傳「內距 8px、設計 12px」這種精確落差 + 誠實的未配對清單,且**不因彈性版面誤報一堆位置**。

### M3 — 本機報告 UI(`parity serve`)
- ASP.NET Core(Kestrel 127.0.0.1)+ React:落差清單 + 疊圖輔助視圖,點擊高亮
- **DoD**:`parity serve` 在瀏覽器開本機報告,掃 localhost 頁面完全正常。

### M4 — CI 還原度把關(差異化支柱)
- GitHub Action 包 CLI:build → 起 localhost → check → baseline+門檻 → pass/fail + artifact
- 內網 staging 用 self-hosted runner
- **DoD**:PR 讓設計落差超門檻就擋,產出報告 artifact。

### M5 — 持久化 + 分類 + 驗證抽象層
- EF Core + SQLite(本機檔案):baseline / 歷史
- **Cornhsu.Labeling**:落差分類(依維度/嚴重度)
- `ImageDesignSource`(PNG + 手動標註)證明 `IDesignSource` 成立
- **DoD**:baseline 可存可比;落差用 Cornhsu.Labeling 分類;抽象層被第二 adapter 驗證。

### M6 —(選配 / v2)雲端外殼:服務公開階段
- 同一顆引擎換雲端外殼:Docker(Playwright base image)、非 root + seccomp、`UrlGuard`(SSRF)、Fly.io + cornhsu.com 子網域
- 用途:公開網址掃描 + 不記名報告分享 + 給非技術人員
- **DoD**:公開網頁貼 URL 出報告;內網/惡意網址被擋;證明引擎/外殼分離成立。

> 順序重點:**M1–M4 全程在你自己的機器/CI 裡跑,localhost 與內網從第一天就涵蓋。** 雲端(M6)最後、且選配。這正好對上「本機 → 內網 → 公開」的真實開發流程。

---

## 7. 風險表

| 風險 | 影響 | 對策 |
|---|---|---|
| **節點配對不準** | 報告不可信 | 以設計端為錨、自動優先、只補漏(圖層名 / `parity map`),配不到誠實列 |
| **絕對位置誤報**(RWD 打架) | 幾何整片紅 → 失去信任 | 預設不比絕對 x/y,改比尺寸/內距/相對間距(4.8) |
| 字型對不上 | 假警報 | font-family 當軟落差,聚焦 size/weight/spacing |
| 子像素/渲染差異 | 到處微小落差 | 容差必需、預設非零、可調 |
| 內圈太慢沒人用 | 開發者不開它 | `serve --watch` 熱瀏覽器秒級回饋;一次性 `check` 留 CI |
| 引擎/外殼沒切乾淨 | 加雲端外殼時要大改 | M1 就把引擎抽成純函式庫,外殼只做 I/O |
| Figma rate limit / token 外洩 | 限流 / 安全 | 本機快取 frame;token 走 env、不落地不進 log |
| (僅雲端外殼)SSRF | 安全 | UrlGuard + 非 root + seccomp,**只綁在 M6** |
| 紅海(Pixelay/OverlayQA…) | 當產品難出頭 | 定位作品集;守住「數值級 + 本機優先 + 進 CI」三個差異化 |

---

## 8. 這個專案幫你證明了什麼

- **工程判斷力**:做出**貼合真實開發流程(本機→內網→公開)**的工具,而不是一個碰不到 dev 環境的雲端玩具——這比技術本身更能區分資深與否。
- **後端/系統工程**:引擎裡的 Figma 串接、瀏覽器編排、配對/比對演算法、持久化,是伺服器級的真功夫;ASP.NET Core 出場於本機 UI 與選配的雲端部署。
- **一段硬核心**:節點配對 + 座標正規化 + 數值 diff,一看就不是拼湊。
- **乾淨的架構**:引擎/外殼分離、`IDesignSource` 抽象、設定即程式碼、`dotnet tool` 發佈——都是內行才做得出的取捨。
- **有靈魂的起源**:「PM 用人眼抓設計落差,我把它自動化並擋進 CI。」
- **兩扇 adapter 門 + 品牌**:設計端留給 XD/Sketch(`IDesignSource`)、實作端留給 WPF/桌面(`IImplementationSource`);dotnet tool 以 `Cornhsu.Parity` 接回你的 NuGet 品牌家族。

---

## 9. 命名:Parity(定案)

- **產品/品牌名**:Parity
- **命令列指令**:`parity`(例:`parity check`)
- **NuGet 套件 id**:`Cornhsu.Parity`

**為什麼是 Parity**:parity 就是「對等、相等」。這工具整件事就是檢查「實作有沒有跟設計對等」——名字直接等於功能,這是最強的一種命名。它短、好打(CLI 指令很吃這個)、在工程圈本來就是熟詞(feature parity),而且不綁任何一個設計工具或平台,撐得過 Figma→XD、網頁→WPF 的擴張。誠實提醒:它是常見英文字,拿去當商標/商業品牌會有撞名(如區塊鏈的 Parity Technologies);當作品集/開源工具則無妨。

**為什麼指令 `parity` 但套件 `Cornhsu.Parity`**:兩者不衝突,反而各取所長。使用者敲的是乾淨的 `parity`(產品識別);上架 nuget.org 的套件 id 用 `Cornhsu.Parity`——加了你的前綴,**不會跟別人撞、又把它收進 Cornhsu.* 家族**(跟 Cornhsu.Labeling 同一掛),累積個人品牌。這也吻合你既有慣例:產品用獨立名(像 Ramp),函式庫/套件掛 `Cornhsu.*` 前綴。

---

## 下一步

名稱已定(Parity / `Cornhsu.Parity`),剩下就是開工。等你說一聲,我補上可直接複製的 M1–M2 程式碼骨架:

1. `Parity.Engine` 的 `FidelityEngine` / `IDesignSource` / `IImplementationSource` / `DesignNode` / `RenderedNode` 定義。
2. `FigmaDesignSource`(REST + 本機快取)、`WebImplementationSource`(Playwright)、`Normalizer` / `Matcher` / `DiffEngine` / `ColorDelta` 的骨架。
3. `parity.config.json` schema、`Parity.Cli` 進入點(`check` / `serve`)、CI workflow、Dockerfile(給 M6)。
4. 然後你開 repo、跑通 M1:在自己機器上,對一個 localhost 頁面撈到兩邊數值。最容易卡的是 Playwright 首次安裝與座標正規化,過了就順。
