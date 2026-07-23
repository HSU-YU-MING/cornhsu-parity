# Roadmap / 未完成與已知盲點

「已完成」看 [README](README.md) 里程碑與 [CHANGELOG](CHANGELOG.md)。這裡只記**還沒做的、已知的缺口、與建議的優先序**。

## 里程碑層級

- ~~**M5 下半 — `ImageDesignSource`**~~ **完成(2026-07-18)**,且超出原案:除了「圖片+標註+像素取樣」(其他設計工具的萬用轉接頭),還加了 `parity snapshot`(把現況凍結成基準,重構守門,selector 身分配對)。**M5 全部完成,M1–M5 收官。**
- **M6 — 雲端外殼:明文不做**(2026-07-18 決定,除非出現真實使用者)。理由:設計 QA 是上線前的活,雲端只碰得到公開網址(最不重要的階段);「報告分享給非技術人」PR 留言 + Markdown 已覆蓋;架伺服器的成本(Docker/Chromium/SSRF/月費/維運)換不到新價值。

## 其他設計工具的決定(2026-07-18)

- **Adobe XD:不做**——Adobe 已停止開發(維護模式、不再單售),幫死掉的工具寫 adapter 是負資產。
- **Sketch:先不做,門留著**——Mac 限定、市占萎縮;檔案格式可解析,有真實使用者要求再做(工作量 ≈ FigmaDesignSource)。
- **任何工具匯出 PNG 都能走 `designImage`**——ImageDesignSource 就是萬用轉接頭,小眾工具的底線用法已覆蓋。
- 未來若加第三個「活」來源,優先考慮 **Penpot**(開源、有 API、社群成長中),不是 XD。

## 已知盲點 / 該補的

- ~~**gate 盲點:全部沒配到 → 0 分卻 PASS**~~ **已補**:0 配對 / 設計端 0 節點一律 GATE FAIL(附原因,baseline 模式也不豁免);另加選配 `gate.minMatchRate` 門檻。
- ~~**Action 當消費者的流程沒實證**~~ **已實證**(2026-07-18,[parity-action-test](https://github.com/HSU-YU-MING/parity-action-test)):外部 repo 用 `@v0.2.0` 跑真 PR——✓ main 綠(PASS 路徑)✓ PR 打紅 + bot 貼還原度報告(落差/建議修法精確)✓ 再推 commit 後同一則留言原地更新不洗版。
- ~~**CI 裡的 `--baseline` 沒實跑**~~ **已實證**(2026-07-18,parity-action-test):main 留一條既有落差 + commit `parity.baseline.db` + `baseline: true` → ✓ CI 綠(舊債不擋)✓ PR 新增一條落差 → 精確只擋那條(留言列「相對基準:新增 1、不變 1」)。

## 檢視留下的整潔項(非 bug)

**全部完成(2026-07-18)**:
- ~~GitHub Actions 升版~~ → checkout@v7 / setup-dotnet@v6 / upload-artifact@v7(脫離 Node 20 淘汰線)。
- ~~`JsonOptions` 兩處重複~~ → 抽 `ReportJson`(check 落地 / serve API / report 回讀共用)。
- ~~沒有 `parity report` 指令~~ → 已加:從既有 `report.json` 重生 Markdown(`--in`/`--md`,預設印 stdout)。
- ~~`oklch` / `display-p3` 不支援~~ → 已支援(OKLCH→OKLab→sRGB、P3 矩陣轉換,超色域 clamp;`lab`/`rec2020` 等仍不支援)。

## 全量檢視(2026-07-18)對照規畫書的決定與盲點

- ~~**`compare.position` 無作用**(規畫書 4.8「比相對位置」沒兌現)~~ **已補(0.5.0)**:相對最近可靠兄弟/父層的偏移比對,誤報防護見 README。
- **Cornhsu.Labeling 落差分類:決定不接**。規畫書 M5 原案要接,但嚴重度/維度分類引擎內建已足,為兩個 enum 欄位引套件是儀式性依賴。此為明文決定,非遺漏。
- **Figma frame PNG 疊圖**(規畫書 4.4 的 images API):可選增強。現行「實作截圖 + 設計框線」足以對位;設計師若要「看設計稿本人」再做。
- ~~**Shadow DOM / iframe 不走訪**~~ **已補(0.8.0)**:組合樹走訪(open shadow root / slot / 同源 iframe);closed shadow 與跨域 iframe 為原生限制,誠實跳過。RWD 多斷點同版補文件 + target 級 width/height。
- ~~**頁面整體 `transform: scale` 未還原**(規畫書 4.6 有提):量測會被縮放污染~~ **已補(0.10.0)**:擷取時累積祖先 transform 的縮放係數,把 box 幾何除回版面座標系(padding 等 computed style 本不受 transform 影響)。無 transform 時逐位元不變(零回歸);實測 scale(0.5) 頁對未縮放基準 100/100。限制:非 top-left transform-origin 下絕對位置差一常數(但 Parity 比相對位置+尺寸,不受影響);跨域 iframe 內縮放仍不處理。
- **EF `EnsureCreated` 無 migration**:未來 baseline schema 變更時,舊 `parity.baseline.db` 需處理相容。

## 更遠的

- **Chrome 擴充功能**:合理的未來本機外殼,但現在不划算——引擎是 .NET,做擴充會逼出「JS 重寫引擎(雙引擎漂移)」或「另跑本機 server」。真要做要當薄客戶端連 parity server,別重寫引擎。
- **1.0.0**:目前刻意留在 0.x(不承諾 API 穩定);功能穩定後再宣告。

## 建議優先序

1. ~~gate 盲點(0 配對 → PASS)~~ 已補。
2. ~~實證 action 消費者流程~~ 已實證(真 PR:PASS / FAIL+留言 / 留言原地更新)。
3. ~~CI 的 `--baseline` 實跑~~ 已實證(舊債不擋、新落差精確擋)。
4. 接下來輪到整潔項、M5 下半 / M6。**已知盲點全數清空。**
