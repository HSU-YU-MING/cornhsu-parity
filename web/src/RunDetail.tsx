import { useEffect, useMemo, useRef, useState } from 'react'
import { fetchRunMeta, fetchRunReport } from './api'
import type { FidelityReport, NodeResult, ReportDocument, RunMeta, Severity } from './types'
import Blueprint from './Blueprint'

/* 落差詳情——取代 PPT 那一頁的畫面(網頁外殼規畫書 6)。
   左:藍圖(在哪裡);右:數值清單(差多少)。點任一邊,另一邊跟著亮。 */

type HardSeverity = Exclude<Severity, 'none'>
const SEVERITIES: HardSeverity[] = ['critical', 'serious', 'medium', 'minor']

export default function RunDetail({ id }: { id: string }) {
  const [meta, setMeta] = useState<RunMeta | null>(null)
  const [doc, setDoc] = useState<ReportDocument | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [pageIdx, setPageIdx] = useState(0)
  const [selected, setSelected] = useState<string | null>(null)
  const [sevOn, setSevOn] = useState<Set<Severity>>(new Set(SEVERITIES))
  const [softOn, setSoftOn] = useState(true)
  const [cleanOn, setCleanOn] = useState(false)

  useEffect(() => {
    Promise.all([fetchRunMeta(id), fetchRunReport(id)])
      .then(([m, d]) => { setMeta(m); setDoc(d) }, e => setError(String(e)))
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
        {meta.commitSha && <div className="cell"><span className="tag">commit</span><span className="v">{meta.commitSha.slice(0, 7)}</span></div>}
      </div>

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
              <div className="layer">{n.designLayer}</div>
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

          {report.unmatched.length > 0 && (
            <div className="unmatched-note">
              {report.unmatched.length} design node(s) never matched:{' '}
              {report.unmatched.slice(0, 8).map(u => `${u.designLayer} (${u.reason})`).join(', ')}
              {report.unmatched.length > 8 ? ` … +${report.unmatched.length - 8}` : ''}
            </div>
          )}
        </section>
      </div>
    </>
  )
}
