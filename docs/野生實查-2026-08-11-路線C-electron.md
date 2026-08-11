# 野生實查(路線 C):Electron CDP 路徑 × 真實第三方應用

**日期**:2026-08-11。**動機**:Electron 路徑(`cdp:` target)此前只有文件宣稱,沒有
第三方應用的實測。挑最大的 Electron 應用——VS Code(Chrome/148 renderer)——驗證
「附掛活視窗、讀現況、不打擾應用」的全部宣稱。

**方法**:VS Code 以獨立暫存 profile 啟動(`--user-data-dir` 指到暫存目錄,不碰使用者
正式設定)+ `--remote-debugging-port=9333`;config 的 target url 設
`cdp:http://localhost:9333`。跑 `snapshot --stabilize` → `check` ×2 → 改變 UI 後再 check。

## 結果:全部宣稱成立

| 驗證項 | 結果 |
|---|---|
| CDP 附掛 | ✓ 一次成功(Electron 對 `remote-debugging-port` 只給 warning,照常生效) |
| 擷取 | ✓ 567 節點(歡迎頁 workbench) |
| `--stabilize` 三連拍 | ✓ 全穩定(桌面應用沒有廣告/輪播,天生是 snapshot 模式的好客戶) |
| check ×2 | ✓ 566/566 全配對、0 落差、100/100,兩次 report **逐位元一致** |
| **讀的是活視窗** | ✓ 用 `code --reuse-window` 開一個檔案改變 UI 後再 check:立刻掉到 467/566、39 個落差節點、76 分——讀的是即時 DOM,不是快取 |
| 不打擾應用 | ✓ 全程不導航、應用照常可用,結束後關閉的是我們自己開的暫存 profile 實例 |

註:unmatched 清單裡出現的 `path` 是 VS Code 最近清單的 class 名(`<span class="path">`),
不是 SVG 內部節點——F3 的 SVG 葉子化沒有漏。

## 結論

- Electron 路徑與 web 路徑共用同一條 DOM/CSS 量測管線,實測零額外問題——「Electron
  幾乎免費」的架構判斷(README)在最大的真實應用上成立。
- 桌面應用 UI 沒有廣告與第三方動畫,穩定性比野生網站好一個量級;snapshot 模式 +
  Electron 是天作之合。
- 實務提示(值得放進文件):給使用者的建議是用**獨立 `--user-data-dir`** 啟動待測
  Electron 應用——CI 環境乾淨、不受個人設定/外掛影響,也避免附掛到使用者正在用的實例。

## 路線 B(Figma 方言實查)前置

路線 B(公開設計系統的 Figma community 檔 vs 官方實作)需要 `FIGMA_TOKEN`(帳號憑證,
須由帳號主人建立)。候選配對已備妥於實測工作區:Shopify Polaris、IBM Carbon、
Ant Design——三者都同時有公開 Figma UI kit 與官方 Storybook/文件站。待 token 就位即可執行。
