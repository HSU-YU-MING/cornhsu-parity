import { useEffect, useState } from 'react'
import { fetchOverview, fetchRuns, fetchTrend } from './api'
import type { OverviewCard, RunListItem, TrendPoint } from './types'
import { navigate } from './App'
import TrendChart from './TrendChart'

export default function RunsList() {
  const [runs, setRuns] = useState<RunListItem[] | null>(null)
  const [cards, setCards] = useState<OverviewCard[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    fetchRuns().then(setRuns, e => setError(String(e)))
    fetchOverview().then(setCards, () => setCards([]))
  }, [])

  if (error) return <div className="error">Could not load runs ({error}). Is the server running?</div>
  if (!runs) return <div className="loading">Loading…</div>
  if (runs.length === 0)
    return (
      <div className="empty">
        <p>No runs yet.</p>
        <p>Push one from CI or your machine: <code>parity check</code> then <code>parity push --server …</code></p>
      </div>
    )

  return (
    <>
      {cards && cards.length > 0 && <Overview cards={cards} />}
      <h2 className="tag" style={{ margin: '1.4rem 0 0.5rem' }}>received runs</h2>
      <RunsTable runs={runs} />
    </>
  )
}

/* 總覽(M4):每個 專案×route 一張 stat tile——現在幾分、比上次如何、多久前查的。
   點卡片展開該頁的趨勢折線(單開)。 */
function Overview({ cards }: { cards: OverviewCard[] }) {
  const [open, setOpen] = useState<string | null>(null)
  const [trend, setTrend] = useState<TrendPoint[] | null>(null)

  const toggle = (c: OverviewCard) => {
    const key = `${c.projectId}|${c.route}`
    if (open === key) { setOpen(null); return }
    setOpen(key); setTrend(null)
    fetchTrend(c.projectId, c.route).then(setTrend, () => setTrend([]))
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

function RunsTable({ runs }: { runs: RunListItem[] }) {
  return (
    <table className="runs">
      <thead>
        <tr>
          <th className="tag">received (utc)</th>
          <th className="tag">project</th>
          <th className="tag">score</th>
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
            <td className="m">{r.score}/100</td>
            <td><span className={`gate ${r.gateFailed ? 'fail' : 'pass'}`}>{r.gateFailed ? 'FAIL' : 'PASS'}</span></td>
            <td className="m">{r.pages}</td>
            <td className="m">{r.branch ?? '—'}</td>
            <td className="m">{r.commitSha ? r.commitSha.slice(0, 7) : '—'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
