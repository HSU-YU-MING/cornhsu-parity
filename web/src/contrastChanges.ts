// XamlContrast「跟上次比」每一列的文字(純函式,npm test 測;不 import React,node 直接跑得動)。
import type { ContrastChange } from './types'

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
