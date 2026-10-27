// XamlContrast「跟上次比」的列文字。執行:npm test(同 figma.test.ts,不進 tsc / vite)。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { describe, where } from './contrastChanges.ts'
import type { ContrastChange } from './types.ts'

const pair = (over: Partial<ContrastChange>): ContrastChange => ({
  file: 'MainWindow.xaml', element: 'TextBlock', fg: '#9A9A9A', bg: '{Surface}',
  category: 'fail', prevCategory: 'fail', count: 1, prevCount: 1, worst: 2.5, prevWorst: 2.81, lines: [6],
  ...over,
})

test('新增只講這一次', () => {
  assert.equal(describe(pair({ prevCategory: null, prevCount: 0, prevWorst: null }), 'new'), 'fail · worst 2.50:1')
  assert.equal(describe(pair({ prevCategory: null, prevCount: 0, prevWorst: null, count: 3 }), 'new'),
    'fail · worst 2.50:1 · 3 places')
})

test('修好只講上一次', () => {
  assert.equal(describe(pair({ category: null, count: 0, worst: null, prevCategory: 'warn', prevWorst: 4.1 }), 'fixed'),
    'was warn · worst 4.10:1')
})

test('變差與好轉只列有變的那幾項', () => {
  assert.equal(describe(pair({}), 'worsened'), 'worst 2.81:1 → 2.50:1')
  assert.equal(describe(pair({ prevCategory: 'warn', count: 2, worst: 2.81 }), 'worsened'), 'warn → fail · 1 place → 2')
  assert.equal(describe(pair({ count: 1, prevCount: 2, worst: 2.81 }), 'improved'), '2 places → 1')
})

test('行號超過 20 個時說還有幾處;修好的看上一次的處數', () => {
  assert.equal(where(pair({ lines: [6, 9], count: 2 }), 'worsened'), 'MainWindow.xaml:6, 9')
  assert.equal(where(pair({ lines: [1, 2], count: 5 }), 'new'), 'MainWindow.xaml:1, 2 (+3 more)')
  assert.equal(where(pair({ lines: [30], count: 0, prevCount: 1 }), 'fixed'), 'MainWindow.xaml:30')
})
