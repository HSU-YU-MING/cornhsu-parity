/* Figma 深連結:報告的 designReference 是引擎寫的 `<設計來源>#<frame>`(FidelityEngine)。
   設計來源是 Figma 時,`#` 之前是 file key(一串英數);designFile/snapshot 時是本機絕對路徑,
   不會通過檢查,也就不給連結。node-id 的寫法與 CLI 的 Markdown 報告(Reporting.cs)一致:
   冒號換成連字號再編碼——Figma 網址列上的 node-id 就是 `10-5` 這種形式。 */
export function figmaUrl(designReference: string, designId: string): string | null {
  const hash = designReference.indexOf('#')
  const fileKey = hash >= 0 ? designReference.slice(0, hash) : designReference
  if (!designId || !/^[A-Za-z0-9]{15,}$/.test(fileKey)) return null
  return `https://www.figma.com/design/${fileKey}?node-id=${encodeURIComponent(designId.replaceAll(':', '-'))}`
}
