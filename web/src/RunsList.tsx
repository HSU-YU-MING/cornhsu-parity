import { useEffect, useState } from 'react'
import { fetchBranches, fetchContrastOverview, fetchContrastTrend, fetchOverview, fetchRuns, fetchTrend } from './api'
import type { BranchInfo, ContrastCard, ContrastTrendPoint, OverviewCard, RunListItem, TrendPoint } from './types'
import { navigate } from './App'
import TrendChart from './TrendChart'
import ContrastTrendChart from './ContrastTrendChart'

/* 分支預設值:main/master 優先,否則取最近有 push 的那條——
   PR 分支的 push 不該污染 main 的總覽與趨勢(M4.5 必補 #1)。 */
function defaultBranch(branches: BranchInfo[]): string | null {
  if (branches.length === 0) return null
  return branches.find(b => b.branch === 'main')?.branch
    ?? branches.find(b => b.branch === 'master')?.branch
    ?? branches[0].branch
}

export default function RunsList() {
  const [runs, setRuns] = useState<RunListItem[] | null>(null)
  const [cards, setCards] = useState<OverviewCard[] | null>(null)
  const [contrastCards, setContrastCards] = useState<ContrastCard[] | null>(null)
  const [branches, setBranches] = useState<BranchInfo[]>([])
  const [branch, setBranch] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    fetchRuns().then(setRuns, e => setError(String(e)))
    fetchBranches().then(bs => {
      setBranches(bs)
      const def = defaultBranch(bs)
      setBranch(def)
      fetchOverview(def).then(setCards, () => setCards([]))
      fetchContrastOverview(def).then(setContrastCards, () => setContrastCards([]))
    }, () => { setCards([]); setContrastCards([]) })
  }, [])

  const pickBranch = (b: string) => {
    setBranch(b); setCards(null); setContrastCards(null)
    fetchOverview(b).then(setCards, () => setCards([]))
    fetchContrastOverview(b).then(setContrastCards, () => setContrastCards([]))
  }

  if (error) return <div className="error">Could not load runs ({error}). Is the server running?</div>
  if (!runs) return <div className="loading">Loading…</div>
  if (runs.length === 0)
    return (
      <div className="empty">
        <p>No runs yet.</p>
        <p>Push one from CI or your machine: <code>parity check</code> then <code>parity push --server …</code></p>
        <p>For XAML apps: <code>xamlcontrast &lt;root&gt; --json xc.json</code> then <code>xamlcontrast --push xc.json --server …</code></p>
      </div>
    )

  return (
    <>
      {branches.length > 1 && (
        <div className="filters" style={{ marginBottom: '0.6rem' }}>
          <span className="tag">branch</span>
          {branches.map(b => (
            <button key={b.branch} className={`chip ${branch === b.branch ? 'on' : ''}`}
              onClick={() => pickBranch(b.branch)}>
              {b.branch === '' ? '(no branch)' : b.branch}
            </button>
          ))}
        </div>
      )}
      {(cards === null || contrastCards === null) && <div className="loading">Loading…</div>}
      {cards && cards.length > 0 && <Overview cards={cards} branch={branch} />}
      {contrastCards && contrastCards.length > 0 && <ContrastOverview cards={contrastCards} branch={branch} />}
      {cards && contrastCards && cards.length === 0 && contrastCards.length === 0 && branch !== null && (
        <div className="empty">No runs on this branch yet.</div>
      )}
      <h2 className="tag" style={{ margin: '1.4rem 0 0.5rem' }}>received runs — all branches</h2>
      <RunsTable runs={runs} />
    </>
  )
}

/* 總覽(M4):每個 專案×route 一張 stat tile——現在幾分、比上次如何、多久前查的。
   點卡片展開該頁的趨勢折線(單開)。 */
function Overview({ cards, branch }: { cards: OverviewCard[]; branch: string | null }) {
  const [open, setOpen] = useState<string | null>(null)
  const [trend, setTrend] = useState<TrendPoint[] | null>(null)

  const toggle = (c: OverviewCard) => {
    const key = `${c.projectId}|${c.route}`
    if (open === key) { setOpen(null); return }
    setOpen(key); setTrend(null)
    fetchTrend(c.projectId, c.route, branch).then(setTrend, () => setTrend([]))
  }

  const openCard = cards.find(c => `${c.projectId}|${c.route}` === open)
  return (
    <>
      <h2 className="tag" style={{ margin: '0 0 0.5rem' }}>pages — latest score & direction</h2>
      <div className="cards">
        {cards.map(c => {
          const key = `${c.projectId}|${c.route}`
          const dir = c.prevScore === null ? null : Math.sign(c.lastScore - c.prevScore)
          return (
            <button key={key} className={`card ${open === key ? 'on' : ''}`} onClick={() => toggle(c)}>
              <span className="card-route m">{c.route}</span>
              <span className="card-score m">
                {c.lastScore}<span className="dim">/100</span>
                {dir !== null && (
                  <span className={`dir ${dir > 0 ? 'up' : dir < 0 ? 'down' : ''}`}>
                    {dir > 0 ? '▲' : dir < 0 ? '▼' : '→'}
                    {dir !== 0 && <span className="m"> {Math.abs(c.lastScore - (c.prevScore ?? 0))}</span>}
                  </span>
                )}
              </span>
              <span className="card-sub">
                <span className={`gate ${c.lastGateFailed ? 'fail' : 'pass'}`}>{c.lastGateFailed ? 'FAIL' : 'PASS'}</span>
                <span className="m dim"> · {c.runCount} run{c.runCount > 1 ? 's' : ''} · {c.lastAt.slice(5, 16).replace('T', ' ')}</span>
              </span>
            </button>
          )
        })}
      </div>
      {openCard && (
        <div className="pane trend-pane">
          <div className="pane-h">
            <span className="tag">score over time — {openCard.route}</span>
            <span className="m" style={{ fontSize: '0.78rem', color: 'var(--ink-2)' }}>{openCard.project}</span>
          </div>
          {trend === null ? <div className="loading">Loading…</div> : <TrendChart points={trend} />}
        </div>
      )}
    </>
  )
}

/* XamlContrast 的總覽(合併伺服器):每個專案一張——稽核的單位是整個 repo,不是頁面。
   主數字是低於 AA 的筆數(fail · warn);方向看 fail+warn 比上次多還是少——問題變少是變好,
   所以箭頭的顏色照「變好/變差」(▼ 綠、▲ 紅),跟分數卡的上下相反。點卡片展開趨勢。 */
function ContrastOverview({ cards, branch }: { cards: ContrastCard[]; branch: string | null }) {
  const [open, setOpen] = useState<string | null>(null)
  const [trend, setTrend] = useState<ContrastTrendPoint[] | null>(null)

  const toggle = (c: ContrastCard) => {
    if (open === c.projectId) { setOpen(null); return }
    setOpen(c.projectId); setTrend(null)
    fetchContrastTrend(c.projectId, branch).then(setTrend, () => setTrend([]))
  }

  const openCard = cards.find(c => c.projectId === open)
  return (
    <>
      <h2 className="tag" style={{ margin: '1.4rem 0 0.5rem' }}>xaml apps — contrast below AA (XamlContrast)</h2>
      <div className="cards">
        {cards.map(c => {
          const now = c.fail + c.warn
          const prev = c.prevFail === null || c.prevWarn === null ? null : c.prevFail + c.prevWarn
          const delta = prev === null ? null : now - prev
          return (
            <button key={c.projectId} className={`card ${open === c.projectId ? 'on' : ''}`} onClick={() => toggle(c)}>
              <span className="card-route">{c.project}</span>
              <span className="card-score counts">
                <span className={c.fail > 0 ? 'n-fail' : 'n-zero'}>{c.fail}</span>
                <span className="dim"> fail · </span>{c.warn}<span className="dim"> warn</span>
                {delta !== null && (
                  <span className={`dir ${delta < 0 ? 'up' : delta > 0 ? 'down' : ''}`}
                    title={delta < 0 ? 'fewer issues than last run' : delta > 0 ? 'more issues than last run' : 'same as last run'}>
                    {delta > 0 ? '▲' : delta < 0 ? '▼' : '→'}
                    {delta !== 0 && <span className="m"> {Math.abs(delta)}</span>}
                  </span>
                )}
              </span>
              <span className="card-sub">
                <span className={`gate ${c.gateFailed ? 'fail' : 'pass'}`}>{c.gateFailed ? 'FAIL' : 'PASS'}</span>
                <span className="m dim"> · {c.coverage}% resolved · {c.runCount} run{c.runCount > 1 ? 's' : ''} · {c.lastAt.slice(5, 16).replace('T', ' ')}</span>
              </span>
            </button>
          )
        })}
      </div>
      {openCard && (
        <div className="pane trend-pane">
          <div className="pane-h">
            <span className="tag">contrast over time — {openCard.project}</span>
          </div>
          {trend === null ? <div className="loading">Loading…</div> : <ContrastTrendChart points={trend} />}
        </div>
      )}
    </>
  )
}

function RunsTable({ runs }: { runs: RunListItem[] }) {
  return (
    <table className="runs">
      <thead>
        <tr>
          <th className="tag">received (utc)</th>
          <th className="tag">project</th>
          <th className="tag">tool</th>
          <th className="tag">result</th>
          <th className="tag">gate</th>
          <th className="tag">pages</th>
          <th className="tag">branch</th>
          <th className="tag">commit</th>
        </tr>
      </thead>
      <tbody>
        {runs.map(r => (
          <tr key={r.id} onClick={() => navigate(`/runs/${r.id}`)}>
            <td className="m">{r.createdAt.replace('T', ' ').slice(0, 19)}</td>
            <td>{r.project}</td>
            <td><span className="tool-badge">{r.tool}</span></td>
            <td className="m">
              {r.contrast
                ? <span className="counts"><span className={r.contrast.fail > 0 ? 'n-fail' : 'n-zero'}>{r.contrast.fail}</span> fail · {r.contrast.warn} warn</span>
                : r.score === null ? '—' : `${r.score}/100`}
            </td>
            <td><span className={`gate ${r.gateFailed ? 'fail' : 'pass'}`}>{r.gateFailed ? 'FAIL' : 'PASS'}</span></td>
            <td className="m">{r.contrast ? '—' : r.pages}</td>
            <td className="m">{r.branch ?? '—'}</td>
            <td className="m">
              {r.commitSha
                ? r.repoUrl
                  ? <a href={`${r.repoUrl}/commit/${r.commitSha}`} target="_blank" rel="noreferrer"
                      onClick={e => e.stopPropagation()}>{r.commitSha.slice(0, 7)}</a>
                  : r.commitSha.slice(0, 7)
                : '—'}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
