import { useEffect, useMemo, useRef, useState } from 'react'
import { deleteRun, fetchChanges, fetchRunMeta, fetchRunReport } from './api'
import type { FidelityReport, Me, NodeResult, ReportDocument, RunChanges, RunMeta, Severity } from './types'
import Blueprint from './Blueprint'
import { navigate } from './App'

/* Figma 深連結:designReference 是 Figma file key 時(不含路徑分隔符的一串英數),
   圖層名可以直接跳回 Figma 的那個節點——CLI 的 Markdown 報告本來就會這樣連,儀表板跟上。 */
function figmaUrl(designReference: string, designId: string): string | null {
  if (!/^[A-Za-z0-9]{15,}$/.test(designReference)) return null // 路徑/快照檔 → 不是 Figma
  return `https://www.figma.com/design/${designReference}?node-id=${encodeURIComponent(designId)}`
}

/* 落差詳情——取代 PPT 那一頁的畫面(網頁外殼規畫書 6)。
   左:藍圖(在哪裡);右:數值清單(差多少)。點任一邊,另一邊跟著亮。 */

type HardSeverity = Exclude<Severity, 'none'>
const SEVERITIES: HardSeverity[] = ['critical', 'serious', 'medium', 'minor']

export default function RunDetail({ id, me }: { id: string; me: Me }) {
  const [meta, setMeta] = useState<RunMeta | null>(null)
  const [doc, setDoc] = useState<ReportDocument | null>(null)
  const [changes, setChanges] = useState<RunChanges | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [pageIdx, setPageIdx] = useState(0)
  const [selected, setSelected] = useState<string | null>(null)
  const [sevOn, setSevOn] = useState<Set<Severity>>(new Set(SEVERITIES))
  const [softOn, setSoftOn] = useState(true)
  const [cleanOn, setCleanOn] = useState(false)

  useEffect(() => {
    Promise.all([fetchRunMeta(id), fetchRunReport(id)])
      .then(([m, d]) => { setMeta(m); setDoc(d) }, e => setError(String(e)))
    fetchChanges(id).then(setChanges, () => setChanges(null)) // 比較失敗不擋主畫面
  }, [id])

  if (error) return <div className="error">Could not load this run ({error}).</div>
  if (!meta || !doc) return <div className="loading">Loading…</div>

  const report = doc.reports[Math.min(pageIdx, doc.reports.length - 1)]

  return (
    <>
      <div className="runhead">
        <div className="cell">
          <span className="tag">score</span>
          <span className="score">{meta.score}<span style={{ fontSize: '0.9rem', color: 'var(--ink-2)' }}>/100</span></span>
        </div>
        <div className="cell"><span className="tag">gate</span>
          <span className={`gate ${meta.gateFailed ? 'fail' : 'pass'}`}>{meta.gateFailed ? 'FAIL' : 'PASS'}</span></div>
        <div className="cell"><span className="tag">project</span><span className="v">{meta.project}</span></div>
        <div className="cell"><span className="tag">received</span>
          <span className="v">{meta.createdAt.replace('T', ' ').slice(0, 19)} UTC</span></div>
        {meta.branch && <div className="cell"><span className="tag">branch</span><span className="v">{meta.branch}</span></div>}
        {meta.commitSha && (
          <div className="cell"><span className="tag">commit</span>
            <span className="v">
              {meta.repoUrl
                ? <a href={`${meta.repoUrl}/commit/${meta.commitSha}`} target="_blank" rel="noreferrer">
                    {meta.commitSha.slice(0, 7)}</a>
                : meta.commitSha.slice(0, 7)}
            </span>
          </div>
        )}
        {me.memberships.some(m => m.projectId === meta.projectId && m.role === 'owner') && (
          <button className="btn-danger" onClick={async () => {
            if (!confirm('Delete this run? Its data leaves the overview and trends permanently.')) return
            await deleteRun(id)
            navigate('/')
          }}>Delete run</button>
        )}
      </div>

      {changes && <ChangesStrip changes={changes} />}

      {doc.reports.length > 1 && (
        <div className="pagetabs">
          {doc.reports.map((r, i) => (
            <button key={r.route} className={i === pageIdx ? 'on' : ''}
              onClick={() => { setPageIdx(i); setSelected(null) }}>
              {r.route}
            </button>
          ))}
        </div>
      )}

      <PageView key={report.route} report={report}
        selected={selected} setSelected={setSelected}
        sevOn={sevOn} setSevOn={setSevOn}
        softOn={softOn} setSoftOn={setSoftOn}
        cleanOn={cleanOn} setCleanOn={setCleanOn} />
    </>
  )
}

/* 「畫了但沒做出來」——落差的第三類(M4.6 必補 #3)。設計稿有、頁面上配不到的節點,
   對 PM 很可能就是漏做的功能,值得一個正式區塊而不是一行註腳。
   點一列 → 藍圖上亮出該設計框(紅虛線加粗 + 標註)。 */
const UNMATCHED_RENDER_CAP = 100 // 野生極端值:Codex 總表 406 個——上限保住渲染,其餘明講

function UnmatchedSection({ report, selected, select }: {
  report: FidelityReport
  selected: string | null
  select: (id: string | null) => void
}) {
  const [open, setOpen] = useState(report.unmatched.length <= 20) // 少量直接展開,海量預設收合
  if (report.unmatched.length === 0) return null

  return (
    <div>
      <div className="unmatched-note" style={{ cursor: 'pointer' }} onClick={() => setOpen(!open)}>
        {open ? '▾' : '▸'} <strong>{report.unmatched.length}</strong> design node(s) were never
        matched on the page — possibly not built yet
      </div>
      {open && report.unmatched.slice(0, UNMATCHED_RENDER_CAP).map(u => (
        <div key={u.designId}
          className={`unmatched-row ${selected === u.designId ? 'sel' : ''}`}
          onClick={() => select(selected === u.designId ? null : u.designId)}>
          <span>{u.designLayer}</span>
          <span className="reason">{u.reason}</span>
          <span className="m dim">{Math.round(u.designBox.width)}×{Math.round(u.designBox.height)}</span>
        </div>
      ))}
      {open && report.unmatched.length > UNMATCHED_RENDER_CAP && (
        <div className="unmatched-note">
          …and {report.unmatched.length - UNMATCHED_RENDER_CAP} more (showing the first {UNMATCHED_RENDER_CAP})
        </div>
      )}
    </div>
  )
}

/* 「跟上次比變了什麼」——PM 每天真正的問題(M4.5 必補 #3)。
   同專案同分支的前一筆;第一筆就誠實說是第一筆。 */
function ChangesStrip({ changes }: { changes: RunChanges }) {
  const [open, setOpen] = useState(false)

  if (changes.prevRunId === null)
    return <p className="changes-strip m dim">first run on this branch — nothing to compare against yet</p>

  const total = changes.regressions.length + changes.worsened.length + changes.fixed.length
  return (
    <div className="changes-strip">
      <button className="changes-summary m" onClick={() => setOpen(!open)}
        aria-expanded={open} disabled={total === 0}>
        vs previous run ({changes.prevCreatedAt!.replace('T', ' ').slice(5, 16)}):{' '}
        <span className={changes.regressions.length ? 'chg-new' : 'dim'}>🔴 new {changes.regressions.length}</span> ·{' '}
        <span className={changes.worsened.length ? 'chg-worse' : 'dim'}>🟠 worsened {changes.worsened.length}</span> ·{' '}
        <span className={changes.fixed.length ? 'chg-fixed' : 'dim'}>🟢 fixed {changes.fixed.length}</span> ·{' '}
        <span className="dim">unchanged {changes.unchanged}</span>
        {total > 0 && <span className="dim"> {open ? '▾' : '▸'}</span>}
      </button>
      {open && (
        <table className="diffs changes-table"><tbody>
          {([['new', changes.regressions], ['worsened', changes.worsened], ['fixed', changes.fixed]] as const)
            .flatMap(([kind, list]) => list.map((d, i) => (
              <tr key={`${kind}${i}`}>
                <td className={`m chg-${kind === 'new' ? 'new' : kind === 'worsened' ? 'worse' : 'fixed'}`}>{kind}</td>
                <td>{d.designLayer}<span className="dim m"> {d.route}</span></td>
                <td className="d-prop">{d.prop}</td>
                <td className="d-vals"><span className="exp">{d.expected}</span>
                  <span className="d-arrow"> → </span><span className="act">{d.actual}</span></td>
                <td className={`sev ${d.severity}`}>{d.severity}</td>
              </tr>
            )))}
        </tbody></table>
      )}
    </div>
  )
}

interface PageProps {
  report: FidelityReport
  selected: string | null
  setSelected: (id: string | null) => void
  sevOn: Set<Severity>; setSevOn: (s: Set<Severity>) => void
  softOn: boolean; setSoftOn: (v: boolean) => void
  cleanOn: boolean; setCleanOn: (v: boolean) => void
}

function PageView({ report, selected, setSelected, sevOn, setSevOn, softOn, setSoftOn, cleanOn, setCleanOn }: PageProps) {
  const listRef = useRef<HTMLDivElement>(null)

  // 節點可見性:嚴重度過濾(soft 落差跟 soft 開關走);「乾淨節點」預設收起,只當背景畫在藍圖上
  const nodeVisible = useMemo(() => (n: NodeResult) => {
    const hard = n.diffs.filter(d => !d.soft)
    const soft = n.diffs.filter(d => d.soft)
    if (hard.length === 0 && soft.length === 0) return true // 乾淨節點在藍圖上永遠是背景
    const hardVisible = hard.some(d => sevOn.has(d.severity as Severity))
    const softVisible = softOn && soft.length > 0
    return hardVisible || softVisible
  }, [sevOn, softOn])

  const faulty = report.nodes.filter(n => n.diffs.length > 0 && nodeVisible(n))
  const clean = report.nodes.filter(n => n.diffs.length === 0)

  const select = (id: string | null) => {
    setSelected(id)
    if (id !== null)
      listRef.current?.querySelector(`[data-node="${CSS.escape(id)}"]`)
        ?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  }

  const toggleSev = (s: Severity) => {
    const next = new Set(sevOn)
    if (next.has(s)) next.delete(s); else next.add(s)
    setSevOn(next)
  }

  const counts = report.summary
  return (
    <>
      <div className="filters">
        <span className="tag">show</span>
        {SEVERITIES.map(s => (
          <button key={s} className={`chip c-${s} ${sevOn.has(s) ? 'on' : ''}`} onClick={() => toggleSev(s)}>
            {s} {{ critical: counts.critical, serious: counts.serious, medium: counts.medium, minor: counts.minor }[s]}
          </button>
        ))}
        <button className={`chip ${softOn ? 'on' : ''}`} onClick={() => setSoftOn(!softOn)}>soft</button>
      </div>

      <div className="bench">
        <section className="pane pane-blueprint">
          <div className="pane-h">
            <span className="tag">where — drawn from measured boxes</span>
            <span className="m" style={{ fontSize: '0.78rem', color: 'var(--ink-2)' }}>{report.route}</span>
          </div>
          <Blueprint report={report} selected={selected} onSelect={select}
            visible={nodeVisible} showUnmatched={true} />
          <div className="unmatched-note">
            solid = measured on the page (color = severity) · dashed blue = design intent of the
            selected node · dotted red = designed but never matched ({report.summary.unmatched})
          </div>
        </section>

        <section className="pane" ref={listRef}>
          <div className="pane-h">
            <span className="tag">how far off — expected → actual</span>
            <span className="m" style={{ fontSize: '0.78rem', color: 'var(--ink-2)' }}>
              {report.summary.matched}/{report.summary.designNodes} matched
            </span>
          </div>

          {faulty.length === 0 && (
            <div className="empty" style={{ border: 0 }}>
              Nothing to show at these filters{report.summary.nodesWithDiffs > 0 ? ' — widen them above.' : ' — this page matches its design. 🎉'}
            </div>
          )}

          {faulty.map(n => (
            <div key={n.designId} data-node={n.designId}
              className={`node-card ${selected === n.designId ? 'sel' : ''}`}
              onClick={() => select(selected === n.designId ? null : n.designId)}>
              <div className="layer">
                {n.designLayer}
                {figmaUrl(report.designReference, n.designId) && (
                  <a className="figma-link" href={figmaUrl(report.designReference, n.designId)!}
                    target="_blank" rel="noreferrer" onClick={e => e.stopPropagation()}
                    title="open this layer in Figma">↗ figma</a>
                )}
              </div>
              <div className="sel-path">{n.selector}</div>
              <table className="diffs"><tbody>
                {n.diffs
                  .filter(d => d.soft ? softOn : sevOn.has(d.severity as Severity))
                  .map((d, i) => (
                    <tr key={i}>
                      <td className="d-prop">{d.prop}</td>
                      <td className="d-vals">
                        <span className="exp">{d.expected}</span>
                        <span className="d-arrow"> → </span>
                        <span className="act">{d.actual}{d.unit ?? ''}</span>
                      </td>
                      <td className={`sev ${d.severity}`}>{d.severity}{d.soft && <span className="soft"> · soft</span>}</td>
                    </tr>
                  ))}
              </tbody></table>
            </div>
          ))}

          {clean.length > 0 && (
            <div className="unmatched-note" style={{ cursor: 'pointer' }} onClick={() => setCleanOn(!cleanOn)}>
              {cleanOn ? '▾' : '▸'} {clean.length} node(s) match their design exactly
              {cleanOn && clean.map(n => (
                <div key={n.designId} className="sel-path" style={{ paddingTop: '0.2rem' }}>
                  ✓ {n.designLayer} — {n.selector}
                </div>
              ))}
            </div>
          )}

          <UnmatchedSection report={report} selected={selected} select={select} />
        </section>
      </div>
    </>
  )
}
