// 儀表板的 Figma 深連結。執行:npm test(Node 內建 test runner + 型別剝除,不另裝測試框架)。
// 這支檔案不進 tsc / vite(tsconfig 的 exclude),所以可以用 node:test 與 .ts 副檔名的 import。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { figmaUrl } from './figma.ts'

test('Figma 來源:designReference 是引擎寫的 <fileKey>#<frame>,連到該節點', () => {
  // 引擎(FidelityEngine)寫的是 `${Design.Source}#${Design.NodeId}`;原本拿整串比對
  // ^[A-Za-z0-9]{15,}$,裡面有 # 所以永遠 false,連結從沒出現過
  assert.equal(
    figmaUrl('qRzJ3fxF1g09NczsrugiXD#10:2', '10:5'),
    'https://www.figma.com/design/qRzJ3fxF1g09NczsrugiXD?node-id=10-5')
})

test('node-id 照 CLI 的 Markdown 報告:冒號換成連字號再編碼', () => {
  assert.equal(
    figmaUrl('qRzJ3fxF1g09NczsrugiXD#1:1', 'I12:34;56:78'),
    'https://www.figma.com/design/qRzJ3fxF1g09NczsrugiXD?node-id=I12-34%3B56-78')
})

test('designFile / snapshot 來源(本機絕對路徑)沒有連結', () => {
  assert.equal(figmaUrl('D:\site\parity.snapshot.json#/', '#hero'), null)
  assert.equal(figmaUrl('/home/ci/site/design.json#', 'body > h1'), null)
})

test('沒有 designId 就沒有連結', () => {
  assert.equal(figmaUrl('qRzJ3fxF1g09NczsrugiXD#10:2', ''), null)
})
