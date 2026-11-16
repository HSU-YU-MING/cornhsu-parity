// XamlContrast「跟上次比」每一列的文字(純函式,npm test 測;不 import React,node 直接跑得動)。
import type { ContrastChange, ContrastChanges } from './types'

export type ChangeKind = 'new' | 'worsened' | 'improved' | 'fixed'

const ratio = (r: number | null) => (r === null ? '—' : `${r.toFixed(2)}:1`)
const places = (n: number) => `${n} place${n === 1 ? '' : 's'}`

/** 檔案:行號。行號只給到 20 個,處數更多時明講還有幾處 */
export function where(c: ContrastChange, kind: ChangeKind): string {
  const total = kind === 'fixed' ? c.prevCount : c.count
  const more = total > c.lines.length ? ` (+${total - c.lines.length} more)` : ''
  return `${c.file}:${c.lines.join(', ')}${more}`
}

/** 這一組變了什麼:新增與修好只講一邊,變差與好轉講「之前 → 現在」的那幾項 */
export function describe(c: ContrastChange, kind: ChangeKind): string {
  if (kind === 'new')
    return [c.category, `worst ${ratio(c.worst)}`, c.count > 1 ? places(c.count) : null].filter(Boolean).join(' · ')
  if (kind === 'fixed')
    return [`was ${c.prevCategory}`, `worst ${ratio(c.prevWorst)}`, c.prevCount > 1 ? places(c.prevCount) : null]
      .filter(Boolean).join(' · ')
  const parts: string[] = []
  if (c.prevCategory !== c.category) parts.push(`${c.prevCategory} → ${c.category}`)
  if (c.prevCount !== c.count) parts.push(`${places(c.prevCount)} → ${c.count}`)
  if (c.prevWorst !== c.worst) parts.push(`worst ${ratio(c.prevWorst)} → ${ratio(c.worst)}`)
  return parts.join(' · ')
}

/**
 * 看這份比對前要先知道的事。順序是「最會誤導的先講」:
 *
 * 1. 鍵換版(伺服器的 ContrastCompare.KeyVersionOf)—— 同一組配對在兩筆 run 裡的身分不同,
 *    所以整排「新增」與「修好」多半只是換了名字。這句要講得比第 2 句重,因為數字看起來
 *    完全像是程式碼變差了。
 * 2. 只是工具版本不同 —— 稽核語意每個 minor 都可能改進,所以部分變化可能來自工具。
 * 3. 上一筆被判不可信(exit 3)—— 拿它當基準本來就不穩。
 *
 * 1 與 2 互斥:鍵都換版了就不必再說「版本不同」。
 */
export function notes(c: ContrastChanges, toolVersion: string | null): string[] {
  const out: string[] = []
  const v = (s: string | null) => s ?? '(unknown)'
  if (c.keyVersion !== null && c.prevKeyVersion !== null && c.keyVersion !== c.prevKeyVersion)
    out.push(`XamlContrast ${v(c.prevToolVersion)} and ${v(toolVersion)} identify pairs differently `
      + `(baseline key version ${c.prevKeyVersion} → ${c.keyVersion}), so most of the new and fixed rows `
      + `below are the same pairs under new names, not real changes — compare two runs of the same version.`)
  else if (c.prevToolVersion !== toolVersion)
    out.push(`The previous run used XamlContrast ${v(c.prevToolVersion)} and this one `
      + `${v(toolVersion)} — some of these changes may come from the tool, not from your XAML.`)
  if (c.prevExitCode === 3)
    out.push('The previous run was marked untrustworthy (exit 3), so it is a shaky baseline.')
  return out
}
