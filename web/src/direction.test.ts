// report.json 的 direction 讀法——與 Parity.Report 的 ReportDirectionConverter 同一套規則(審查第 6 條)。
// 執行:npm test(node:test + 型別剝除;不進 tsc / vite)
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readDirection } from './direction.ts'

test('只收小寫的 forward / reverse', () => {
  assert.equal(readDirection({ direction: 'forward' }), 'forward')
  assert.equal(readDirection({ direction: 'reverse' }), 'reverse')
})

test('欄位缺少(2.0.0 之前的報告)讀成 forward', () => {
  assert.equal(readDirection({}), 'forward')
})

test('其他值一律當壞報告:大小寫不同、未知字串、數字、null', () => {
  for (const bad of ['Reverse', 'REVERSE', 'sideways', '', 1, 5, null, true]) {
    assert.throws(() => readDirection({ direction: bad }), /"direction" must be "forward" or "reverse"/, String(bad))
  }
})
