import { useEffect, useState } from 'react'
import { fetchRuns } from './api'
import type { RunListItem } from './types'
import { navigate } from './App'

export default function RunsList() {
  const [runs, setRuns] = useState<RunListItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    fetchRuns().then(setRuns, e => setError(String(e)))
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
