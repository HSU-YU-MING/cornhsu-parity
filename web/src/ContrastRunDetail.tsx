import { useEffect, useMemo, useState } from 'react'
import { fetchContrastChanges, fetchContrastReport } from './api'
import type {
  ContrastCategory, ContrastChange, ContrastChanges, ContrastFinding, ContrastReport, ContrastRunMeta, Me,
} from './types'
import { DeleteRunButton, MetaCells } from './RunHead'
import { literalColour } from './colour'
import { describe, where, type ChangeKind } from './contrastChanges'

/* XamlContrast 的一次稽核(合併伺服器)——給工程師與 PM 看的問題清單:
   哪個檔案第幾行、什麼元素、前景疊在什麼底上、深淺兩個主題各是幾比一、門檻是多少。
   摘要讀伺服器的 ContrastResult;逐筆讀 /report 的原文(與 parity 的藍圖同一個做法,關聯表不存逐筆)。
   色塊只畫「原文就是色碼」的值(#888、#RRGGBB、WPF 的 #AARRGGBB):{DynamicResource X} 這類鍵,
   報告裡沒有解析後的色值,這裡不猜——那是 XamlContrast 自己的鐵則,儀表板照守。 */

const CATEGORIES: ContrastCategory[] = ['fail', 'warn', 'ok', 'decorative']
const RENDER_CAP = 500 // 大 repo 的 ok 可能上千筆;上限保住渲染,其餘明講

export default function ContrastRunDetail({ meta, me }: { meta: ContrastRunMeta; me: Me }) {
  const [report, setReport] = useState<ContrastReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [on, setOn] = useState<Set<ContrastCategory>>(new Set(['fail', 'warn']))
  const [unresolvedOpen, setUnresolvedOpen] = useState(false)
  const [changes, setChanges] = useState<ContrastChanges | null>(null)

  useEffect(() => {
    fetchContrastReport(meta.id).then(setReport, e => setError(String(e)))
    fetchContrastChanges(meta.id).then(setChanges, () => setChanges(null)) // 比較失敗不擋主畫面
  }, [meta.id])

  const rows = useMemo(() => {
    if (!report) return []
    const rank = (c: ContrastCategory) => CATEGORIES.indexOf(c)
    return report.findings
      .filter(f => on.has(f.category))
      .sort((a, b) => rank(a.category) - rank(b.category) || worst(a) - worst(b)
        || a.file.localeCompare(b.file) || a.line - b.line)
  }, [report, on])

  const c = meta.contrast
  const toggle = (cat: ContrastCategory) => {
    const next = new Set(on)
    if (next.has(cat)) next.delete(cat)
    else next.add(cat)
    setOn(next)
  }

  return (
    <>
      <div className="runhead">
        <div className="cell">
          <span className="tag">result</span>
          <span className="score counts">
            <span className={c.fail > 0 ? 'n-fail' : 'n-zero'}>{c.fail}</span>
            <span style={{ fontSize: '0.9rem', color: 'var(--ink-2)' }}> fail · </span>
            {c.warn}<span style={{ fontSize: '0.9rem', color: 'var(--ink-2)' }}> warn</span>
          </span>
        </div>
        <div className="cell"><span className="tag">gate</span>
          <span className={`gate ${meta.gateFailed ? 'fail' : 'pass'}`}>{meta.gateFailed ? 'FAIL' : 'PASS'}</span></div>
        <div className="cell"><span className="tag">tool</span>
          <span className="v">XamlContrast {c.toolVersion ?? ''}</span></div>
        <MetaCells meta={meta} />
        <DeleteRunButton meta={meta} me={me} />
      </div>

      {changes && <ContrastChangesStrip changes={changes} toolVersion={c.toolVersion} />}

      {c.exitCode === 3 && (
        <div className="empty" style={{ padding: '0.8rem', marginBottom: '1rem', textAlign: 'left' }}>
          <strong>The audit marked this result as untrustworthy (exit 3)</strong> — too few colour pairs could be
          resolved, no pairs were found, or the palette degraded under <code>--strict-palette</code>.
          Fix the configuration before reading the counts.
        </div>
      )}

      <div className="summary-strip">
        <span><span className="tag">pairs</span><span className="m">{c.pairs}</span></span>
        <span><span className="tag">coverage</span><span className="m">{c.coverage}%</span></span>
        <span><span className="tag">unresolved</span><span className="m">{c.unresolved}</span></span>
        <span><span className="tag">files</span><span className="m">{c.files}</span></span>
        <span><span className="tag">gate</span><span className="m">{c.failOn === 'warn' ? 'below AA fails' : `failOn ${c.failOn}`}</span></span>
        <span><span className="tag">palette</span><span className="m">{c.paletteMode}</span>
          {c.paletteDegraded && <span className="warn-note"> (degraded)</span>}</span>
        <span><span className="tag">suppressed</span><span className="m">{c.suppressed}</span></span>
        <span><span className="tag">decorative</span><span className="m">{c.decorative}</span></span>
        {c.parseErrors > 0 && (
          <span className="warn-note"><span className="tag">parse errors</span><span className="m">{c.parseErrors}</span></span>
        )}
      </div>

      {error && <div className="error">Could not load the findings ({error}).</div>}
      {!error && !report && <div className="loading">Loading…</div>}
      {report && (
        <>
          <div className="filters">
            <span className="tag">show</span>
            {CATEGORIES.map(cat => (
              <button key={cat} className={`chip ${on.has(cat) ? 'on' : ''}`} onClick={() => toggle(cat)}>
                {cat} <span className="dim">{report.findings.filter(f => f.category === cat).length}</span>
              </button>
            ))}
            {report.summary.paletteDetail && <span className="dim" style={{ fontSize: '0.78rem' }}>{report.summary.paletteDetail}</span>}
          </div>

          <div className="pane">
            {rows.length === 0
              ? <div className="loading">{on.size === 0 ? 'Pick a category above.' : 'Nothing in the selected categories.'}</div>
              : <FindingsTable rows={rows.slice(0, RENDER_CAP)} />}
            {rows.length > RENDER_CAP && (
              <div className="unmatched-note">
                Showing the first {RENDER_CAP} of {rows.length}. The full list is in the raw report
                (<a href={`/api/runs/${meta.id}/report`}>download JSON</a>).
              </div>
            )}
          </div>

          {report.unresolvedSites.length > 0 && (
            <div className="pane" style={{ marginTop: '1rem' }}>
              <div className="unmatched-note" style={{ cursor: 'pointer', borderTop: 0 }}
                onClick={() => setUnresolvedOpen(!unresolvedOpen)}>
                {unresolvedOpen ? '▾' : '▸'} <strong>{report.unresolvedSites.length}</strong> pair(s) could not be
                resolved statically — bindings, gradients, unknown keys. They are not counted above.
                <span className="dim"> {Object.entries(report.summary.unresolvedBy).map(([r, n]) => `${r} ${n}`).join(' · ')}</span>
              </div>
              {unresolvedOpen && report.unresolvedSites.slice(0, RENDER_CAP).map((u, i) => (
                <div key={i} className="unmatched-row">
                  <span className="loc">{u.file}:{u.line}</span>
                  <span className="reason">{u.reason}</span>
                  <span className="colour dim">{u.value}</span>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </>
  )
}

/* 「跟上次比」——同專案、同分支的前一筆 XamlContrast run(伺服器 ContrastCompare)。比法跟 XamlContrast 自己的
   --baseline 一樣:一組配對 = 檔案|元素|前景|底色,不含行號(行號會隨編輯漂移);處數變多或最差比值變低 = 變差。
   比 baseline 多一格「好轉」:處數變少或比值變好、但還低於 AA。第一筆就誠實說是第一筆;
   兩筆的工具版本不同、或上一筆不可信時明講,不讓人把工具的變化讀成程式碼的變化。 */
function ContrastChangesStrip({ changes, toolVersion }: { changes: ContrastChanges; toolVersion: string | null }) {
  const [open, setOpen] = useState(false)

  if (changes.prevRunId === null)
    return <p className="changes-strip m dim">first XamlContrast run on this branch — nothing to compare against yet</p>

  const groups: [ChangeKind, ContrastChange[], string][] = [
    ['new', changes.regressions, 'chg-new'],
    ['worsened', changes.worsened, 'chg-worse'],
    ['fixed', changes.fixed, 'chg-fixed'],
    ['improved', changes.improved, 'chg-fixed'],
  ]
  const total = groups.reduce((n, [, list]) => n + list.length, 0)
  const notes: string[] = []
  if (changes.prevToolVersion !== toolVersion)
    notes.push(`The previous run used XamlContrast ${changes.prevToolVersion ?? '(unknown)'} and this one `
      + `${toolVersion ?? '(unknown)'} — some of these changes may come from the tool, not from your XAML.`)
  if (changes.prevExitCode === 3)
    notes.push('The previous run was marked untrustworthy (exit 3), so it is a shaky baseline.')

  return (
    <div className="changes-strip">
      <button className="changes-summary m" onClick={() => setOpen(!open)}
        aria-expanded={open} disabled={total === 0}>
        vs previous run ({changes.prevCreatedAt!.replace('T', ' ').slice(5, 16)}):{' '}
        <span className={changes.regressions.length ? 'chg-new' : 'dim'}>🔴 new {changes.regressions.length}</span> ·{' '}
        <span className={changes.worsened.length ? 'chg-worse' : 'dim'}>🟠 worsened {changes.worsened.length}</span> ·{' '}
        <span className={changes.fixed.length ? 'chg-fixed' : 'dim'}>🟢 fixed {changes.fixed.length}</span> ·{' '}
        <span className={changes.improved.length ? 'chg-fixed' : 'dim'}>improved {changes.improved.length}</span> ·{' '}
        <span className="dim">unchanged {changes.unchanged}</span>
        {total > 0 && <span className="dim"> {open ? '▾' : '▸'}</span>}
      </button>
      {notes.map(n => <p key={n} className="changes-note dim">{n}</p>)}
      {open && (
        <table className="diffs changes-table"><tbody>
          {groups.flatMap(([kind, list, cls]) => list.map((ch, i) => (
            <tr key={`${kind}${i}`}>
              <td className={`m ${cls}`}>{kind}</td>
              <td className="loc">{where(ch, kind)}</td>
              <td className="m">{ch.element}</td>
              <td><Colour value={ch.fg} /> <span className="dim">on</span> <Colour value={ch.bg} /></td>
              <td className="m">{describe(ch, kind)}</td>
            </tr>
          )))}
        </tbody></table>
      )}
    </div>
  )
}

function FindingsTable({ rows }: { rows: ContrastFinding[] }) {
  return (
    <table className="findings">
      <thead>
        <tr>
          <th className="tag">result</th>
          <th className="tag">where</th>
          <th className="tag">element</th>
          <th className="tag">foreground</th>
          <th className="tag">on</th>
          <th className="tag" style={{ textAlign: 'right' }}>dark</th>
          <th className="tag" style={{ textAlign: 'right' }}>light</th>
          <th className="tag" style={{ textAlign: 'right' }}>needs</th>
          <th className="tag">note</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((f, i) => (
          <tr key={`${f.file}:${f.line}:${i}`}>
            <td><span className={`cat ${f.category}`}>{f.category}</span></td>
            <td className="loc">{f.file}:{f.line}</td>
            <td className="m">{f.element}</td>
            <td><Colour value={f.fg} /></td>
            <td><Colour value={f.bg} /></td>
            <Ratio value={f.ratioDark} threshold={f.threshold} />
            <Ratio value={f.ratioLight} threshold={f.threshold} />
            <td className="m num">{f.threshold === 0 ? '—' : `${f.threshold}:1`}</td>
            <td className="dim" style={{ fontSize: '0.78rem' }}>{note(f)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function Ratio({ value, threshold }: { value: number | null; threshold: number }) {
  if (value === null) return <td className="m num dim">—</td>
  return <td className={`m num ${threshold > 0 && value < threshold ? 'below' : ''}`}>{value.toFixed(2)}</td>
}

function Colour({ value }: { value: string }) {
  // 具名色(White、Gray…)也是原文色值;WPF 與 CSS 的具名色都出自 X11 那一套,交給瀏覽器認
  const css = literalColour(value)
    ?? (/^[A-Za-z]+$/.test(value) && CSS.supports('color', value) ? value : null)
  return (
    <span className="colour">
      {css && <span className="swatch" style={{ background: css }} />}
      {value}
    </span>
  )
}

/** 兩個主題裡比較差的那個(只存在一個主題時就是那一個);都沒有就排最後 */
function worst(f: ContrastFinding): number {
  const ratios = [f.ratioDark, f.ratioLight].filter((r): r is number => r !== null)
  return ratios.length === 0 ? Infinity : Math.min(...ratios)
}

function note(f: ContrastFinding): string {
  const parts: string[] = []
  if (f.symmetry === 'dark-fails') parts.push('dark theme only')
  else if (f.symmetry === 'light-fails') parts.push('light theme only')
  else if (f.symmetry === 'both-low') parts.push('both themes')
  if (f.theme) parts.push(`exists in ${f.theme} only`)
  if (f.isLargeText) parts.push('large text')
  if (f.baseline) parts.push(`baseline: ${f.baseline}`)
  return parts.join(' · ')
}
