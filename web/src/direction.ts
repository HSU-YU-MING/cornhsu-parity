// report.json 的 direction(凍結面 4,封閉集)。與 Parity.Report 的 ReportDirectionConverter 同一套規則:
// 只收小寫字串 "forward" / "reverse";欄位缺少(2.0.0 之前的報告)讀成 forward;
// 其他值——大小寫不同、未知字串、數字、null——一律當壞報告,不猜。
export type ReportDirection = 'forward' | 'reverse'

export function readDirection(doc: { direction?: unknown }): ReportDirection {
  if (!('direction' in doc) || doc.direction === undefined) return 'forward'
  if (doc.direction === 'forward' || doc.direction === 'reverse') return doc.direction
  throw new Error(`"direction" must be "forward" or "reverse" (got ${JSON.stringify(doc.direction)})`)
}
