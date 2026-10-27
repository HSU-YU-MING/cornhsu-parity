// XamlContrast 前景/底色原文 → CSS 色碼。執行:npm test(同 figma.test.ts,不進 tsc / vite)。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { literalColour } from './colour.ts'

test('三位與六位色碼原樣', () => {
  assert.equal(literalColour('#888'), '#888')
  assert.equal(literalColour('#1E1E1E'), '#1E1E1E')
})

test('WPF 的 alpha 在前,換成 CSS 的 alpha 在後', () => {
  assert.equal(literalColour('#CC000000'), '#000000CC')
  assert.equal(literalColour('#8FFF'), '#FFF8')
})

test('元素透明度的後綴不影響色塊(色塊畫的是原文的色碼)', () => {
  assert.equal(literalColour('#FFFFFF ×0.6'), '#FFFFFF')
})

test('資源鍵、合成底的描述、長度不對的都不畫', () => {
  assert.equal(literalColour('{DynamicResource Fg}'), null)
  assert.equal(literalColour('{Bg}'), null)
  assert.equal(literalColour('#12345'), null)
  assert.equal(literalColour('#1234567'), null)
  assert.equal(literalColour('Transparent'), null)
})
