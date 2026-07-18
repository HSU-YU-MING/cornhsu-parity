# Roadmap / 未完成與已知盲點

「已完成」看 [README](README.md) 里程碑與 [CHANGELOG](CHANGELOG.md)。這裡只記**還沒做的、已知的缺口、與建議的優先序**。

## 里程碑層級

- **M5 下半 — `ImageDesignSource`**:讓「一張圖」當設計來源,驗證 `IDesignSource` 抽象可插拔。價值低(使用者無感),可長期擱著。
- **M6 — 雲端外殼**(選配):公開網址掃描 + SSRF 防護 + 報告分享。規畫書列為「最後、選配」;有真實需求再做。做的話,只有這個對外多人共用的外殼有 SSRF 問題。

## 已知盲點 / 該補的

- ~~**gate 盲點:全部沒配到 → 0 分卻 PASS**~~ **已補**:0 配對 / 設計端 0 節點一律 GATE FAIL(附原因,baseline 模式也不豁免);另加選配 `gate.minMatchRate` 門檻。
- ~~**Action 當消費者的流程沒實證**~~ **已實證**(2026-07-18,[parity-action-test](https://github.com/HSU-YU-MING/parity-action-test)):外部 repo 用 `@v0.2.0` 跑真 PR——✓ main 綠(PASS 路徑)✓ PR 打紅 + bot 貼還原度報告(落差/建議修法精確)✓ 再推 commit 後同一則留言原地更新不洗版。
- ~~**CI 裡的 `--baseline` 沒實跑**~~ **已實證**(2026-07-18,parity-action-test):main 留一條既有落差 + commit `parity.baseline.db` + `baseline: true` → ✓ CI 綠(舊債不擋)✓ PR 新增一條落差 → 精確只擋那條(留言列「相對基準:新增 1、不變 1」)。

## 檢視留下的整潔項(非 bug)

- GitHub Actions 的 `actions/checkout@v4` 等吃 Node 20 淘汰警告 → 之後升 v5。
- `JsonOptions` 在 check / serve 兩處重複 → 抽共用。
- 沒有 `parity report` 指令(從既有 `report.json` 重生 Markdown,免重掃)。
- 現代色域:已支援 `rgb()` / `color(srgb …)`,但 `oklch` / `display-p3` 仍不支援。

## 更遠的

- **Chrome 擴充功能**:合理的未來本機外殼,但現在不划算——引擎是 .NET,做擴充會逼出「JS 重寫引擎(雙引擎漂移)」或「另跑本機 server」。真要做要當薄客戶端連 parity server,別重寫引擎。
- **1.0.0**:目前刻意留在 0.x(不承諾 API 穩定);功能穩定後再宣告。

## 建議優先序

1. ~~gate 盲點(0 配對 → PASS)~~ 已補。
2. ~~實證 action 消費者流程~~ 已實證(真 PR:PASS / FAIL+留言 / 留言原地更新)。
3. ~~CI 的 `--baseline` 實跑~~ 已實證(舊債不擋、新落差精確擋)。
4. 接下來輪到整潔項、M5 下半 / M6。**已知盲點全數清空。**
