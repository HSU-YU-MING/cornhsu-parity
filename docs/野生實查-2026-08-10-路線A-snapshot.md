# 野生實查(路線 A):snapshot 模式 × 8 個公開網站

**日期**:2026-08-10。**動機**:對照 XamlContrast 2026-08-10 的外部專案實查(8 個公開 WPF 專案、
1909 個 XAML 檔)——「實查的樣本必須包含非作者的專案」。Parity 的 dogfooding 樣本
(cornhsu.com 21 頁)全部同一位作者:無隨機 id、無廣告版位、DOM 淺、無第三方動畫。
本輪用 `parity snapshot`(不需 Figma)把野生網站凍結成基準再 `check`,考驗**擷取與配對的
野外強健性**,不是比對設計稿。

**方法**:每站 `snapshot` → `check` ×2(驗證決定論,report.json 逐位元比對);另將 HN 首頁
鏡像到本地(file://)做變異測試(改一條 CSS,驗證精確抓漏)與純淨決定論對照。
視窗一律預設 1280×800。CLI 為 master @ 38a5587 本機 Release build。

## 總表

| 站 | 型態 | 節點 | 配對(run1 → run2) | 落差節點 | 分數 | 決定論 |
|---|---|---|---|---|---|---|
| news.ycombinator.com | 老派 table 版面 | 805 | 804/804 → 804/804 | 0 → 1 | 100 | 內容漂移 1 筆(見 F4) |
| en.wikipedia.org(條目頁) | 深巢狀內容站 | 4719(擷取成功) | — | — | — | **snapshot 落地 crash(F1)** |
| developer.mozilla.org(文件頁) | shadow DOM + 廣告 | 6190 | 6141/6189(兩次同) | 4(兩次同) | 99 | 分數穩定;48 個 unmatched(F2) |
| shoelace.style | web component 重 | 493 | 492/492 | 0 | 100 | **逐位元一致 ✓** |
| tailwindcss.com | utility-class + 動畫 | 2215 | 2169 → 2171 /2214 | 41 → 45 | 96 | flaky(F3、F4) |
| stripe.com | 動畫極重的行銷頁 | 3323 | 3322/3322 → **2680/3322** | 16 → 94 | 100 → **78** | 嚴重 flaky(F4) |
| github.com | 混合、lazy 媒體 | 1138 | 1126/1137(兩次同) | 26 → 29 | 97 → 96 | 輕微 flaky(F4) |
| excalidraw.com | canvas 應用 | 274 | 273/273 | 0 | 100 | 分數穩定;selector 含隨機 id,報告非逐位元決定(F2) |
| HN 本地鏡像(file://) | 靜態對照組 | 805 | 804/804 | 0 | 100 | **逐位元一致 ✓** |

## 發現(依優先序)

### F1【bug,**已修(同日)**】深 DOM 讓 snapshot 落地 crash——同一個教訓只修了一半

Wikipedia 條目頁擷取成功(4719 節點)後,序列化 `parity.snapshot.json` 時炸:
`A possible object cycle was detected … depth is larger than the maximum allowed depth of 64`。
DOM 巢狀約 31 層 × 每層 Children 佔 2 個 JSON 深度 = 超過 System.Text.Json 預設 MaxDepth 64。

擷取端**已經**學過這一課:`WebImplementationSource.cs:18` 設了 `MaxDepth = 512`,註解寫明
「真實網站 DOM 常有十幾層巢狀,預設 64 不夠」。但同一棵樹的另外兩條 JSON 路沒跟上:

- **寫**:`Program.cs` SnapshotCommand 用 `ReportJson.Indented` 落地 snapshot(預設 64)→ 本次 crash。
- **讀**:`JsonDesignSource.SerializerOptions`(預設 64)→ 即使寫得出來,回讀一樣會炸。

修法(**已落地**):`ReportJson.Compact` 與 `JsonDesignSource.SerializerOptions` 補
`MaxDepth = 512`(與擷取端同值);回歸測試 `Deep_dom_snapshot_survives_write_and_read_back`
用 60 層合成樹走「snapshot 序列化 → designFile 回讀」全程。Wikipedia 頁端到端重測:
4718/4718 全配對、100/100 PASS。殘留備忘:錯誤訊息「object cycle」對使用者是誤導
(樹沒有環,只是深),超過 512 的極端頁面仍會看到它,值得包一層看得懂的錯誤。

### F2【設計盲點】隨機 id 打破「snapshot 配對 100% 決定論」的宣稱

README 說 snapshot 模式「配對走 selector 身分,100% 決定論」。野外反例兩種:

- **MDN**:48 個 unmatched(`label-92g58lqqado (no-anchor)` ×24、`slot (no-anchor)` ×24)——
  頁面每次載入重新生成隨機 id,snapshot 凍住的 selector 在下次載入的頁面上不存在。
- **Excalidraw**:配對全數成功(273/273、0 落差),但 report.json 兩次不同——差異全部是
  selector 裡的隨機 React id(`#3jrOqJD…` → `#FgpewRbx…`)。分數決定論成立,**逐位元決定論不成立**。

React `useId`、CSS-in-JS 隨機 class、隨機 `aria-labelledby` 是現代前端常態,自家樣本完全沒有。
建議方向(擇一或並用):(a) selector 生成時偵測「高熵 token」(長隨機英數字串)降級改用
結構路徑(nth-of-type);(b) 文件揭露此限制 + 教學用 `ignore`;(c) unmatched 訊息在偵測到
隨機 id 樣態時直接提示原因,而不是籠統的 `no-anchor`。

### F3【決策點】SVG 內部節點(path/g/defs)該不該進樹

tailwindcss.com 的 unmatched 全部是 `path` / `g` / `defs`(44–45 個),且兩次配對數不同
(2169 → 2171)——SVG 內部元素被擷取進樹,但在動畫/重繪下配對不穩,而 path 的
box/padding/字體語意本來就跟設計比對關係薄弱。值得明文決策:**把 `<svg>` 當葉子**
(內部不展開),或至少文件揭露。這同時會消掉一批野外雜訊。

### F4【天生限制,該量化揭露 + 可做緩解】動態內容站的 snapshot 漂移

引擎本身是決定性的(本地鏡像與 shoelace 逐位元一致);漂移全部來自頁面本身會動:

- **HN**:兩次 check 之間新聞排序/字數變了,文字變寬把兄弟推移 5.92px → 1 筆 offsetX medium。
- **MDN**:4 筆 critical(3 色 1 寬)全落在 `mdn-placement-*`——**廣告版位輪播**。現有
  `ignore` 設定即可解,但使用者要自己想到。
- **Stripe**:snapshot 拍在動畫進行中,第一次 check 就有 16 筆(offsetY ×13);第二次配對
  直接崩到 2680/3322、94 個落差節點、78 分——hero 輪播/進場動畫換了狀態。
- **GitHub**:23–24 筆 height(lazy 載入的媒體區塊)。

緩解建議(價值由高到低):
1. **`parity snapshot --stabilize`(或 doctor 模式)**:連拍 N 次,自動 diff、列出不穩定
   區域的 selector,一鍵建議 `ignore` 清單——把「使用者要自己想到 ignore 廣告」變成工具代勞。
   這是本輪最值得做的新功能。
2. 文件:野生/含廣告/含動畫的站用 snapshot 前,先 ignore 動態區;動畫等 cornhsu.com
   0.11.1 的 scroll 動畫教訓已有先例,可歸納成一節「snapshot 模式的適用邊界」。

### F5【UX 小刺】「fidelity score: 100/100」與 GATE FAIL 同時出現

Stripe run1:分數四捨五入顯示 **100/100**,但同時有 9 筆 critical → exit 1。
「100 分卻紅燈」對 CI log 的讀者是困惑的。建議分數在「有 failOn 等級落差」時不顯示滿分
(例如封頂 99,或附註 `(9 critical)`)。

### F6【驗證正面】這些宣稱在野外站得住

- **Shadow DOM 穿透實戰過關**:shoelace(web component 重度)492/492、100/100、逐位元
  決定;MDN 的 `>>>` selector 正確指進 `mdn-placement-top` 的 shadow root。
- **變異測試(本地 HN 鏡像)**:改 `.pagetop` 色 → 精確 1 節點、期望/實際色碼分毫不差;
  `.subtext` 7pt→8pt → 180 筆 fontSize + 正確的級聯寬高位移;`.title` 加 padding-left 6px →
  246 節點的 width/offsetX(**間接**抓到——SnapshotBuilder 刻意把全零 padding 存 null,
  0→非零的 padding 回歸只會以子層位移形式現身,這是文件裡該講的取捨)。對照組(還原 CSS)
  兩次皆 0 落差、逐位元一致。無效變異(頁面沒有 `<pre>`)正確地毫無反應。
- **速度**:6190 節點的 MDN 頁 snapshot+check 都在數秒級;805 節點的 HN snapshot 4.7s、check 2.5s。
- **老派 table 版面**(HN)、**canvas 應用**(excalidraw)擷取皆無異常。

## 取樣偏誤(對照 XamlContrast 規畫書 8.3)

本輪六個發現,**沒有一個**能在 cornhsu.com 的 21 頁上量出來:自家頁面 DOM 淺(F1)、
無隨機 id(F2)、SVG 少且靜態(F3)、無廣告無第三方動畫(F4)、分數從未在有落差時滿分
(F5)。與 XamlContrast 的結論相同:**同源樣本量不出自己的盲區,野生樣本是唯一機制**。
建議把這條也寫進 Parity 的開發紀律。

## 建議修正順序

1. **F1** MaxDepth crash(小修,兩行 + 回歸測試)
2. **F2** 隨機 id(先文件揭露 + 錯誤訊息改善,偵測降級可後續)
3. **F4** `snapshot --stabilize` 自動建議 ignore(最有價值的新功能)
4. **F3** SVG 葉子化決策 + **F5** 滿分紅燈顯示(小修)

## 重現

測試工作目錄(config、snapshot、兩次 report、變異腳本輸出)在 session scratchpad
`routeA/`;各站僅需其中的 `parity.config.json` 即可重跑(snapshot 模式,無 token 需求)。
