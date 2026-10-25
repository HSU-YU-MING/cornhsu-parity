/* XamlContrast 報告裡的前景/底色是「原文」:{DynamicResource Fg}、#888、#CC000000、合成底的描述……
   只有開頭就是色碼的,儀表板才畫色塊——資源鍵在報告裡沒有解析後的色值,不猜(XamlContrast 的鐵則)。
   WPF 的 alpha 在前(#ARGB / #AARRGGBB),CSS 在後(#RGBA / #RRGGBBAA)。 */
export function literalColour(value: string): string | null {
  const m = value.match(/^#([0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})(?![0-9a-f])/i)
  if (!m) return null
  const hex = m[1]
  if (hex.length === 4) return `#${hex.slice(1)}${hex[0]}`
  if (hex.length === 8) return `#${hex.slice(2)}${hex.slice(0, 2)}`
  return `#${hex}`
}
