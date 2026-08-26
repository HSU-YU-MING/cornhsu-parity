import { useRef, useState } from 'react'
import type { TrendPoint } from './types'

/* 趨勢折線(M4)——dataviz 紀律:單序列免圖例;線 2px、點 ≥8px;
   點的顏色是 gate 狀態(status 色,tooltip 併文字講,不靠顏色單獨傳達);
   FAIL 點帶 2px 表面色圈(overlap ring);y 固定 0–100(分數是絕對量表,不裁軸);
   grid 退隱;十字準線 + tooltip,命中區比點大;最新一點直接標值(選擇性直標)。
   色盤 #3e6fae/#b3261e 已跑 validate_palette 六檢全過(light surface #fdfdfc)。 */

const W = 560, H = 180
const PAD_L = 34, PAD_R = 46, PAD_T = 12, PAD_B = 22

export default function TrendChart({ points }: { points: TrendPoint[] }) {
  const [hover, setHover] = useState<number | null>(null)
  const wrapRef = useRef<HTMLDivElement>(null)

  if (points.length === 0) return <div className="loading">No runs for this page yet.</div>

  const x = (i: number) => points.length === 1
    ? (PAD_L + W - PAD_R) / 2
    : PAD_L + (i * (W - PAD_L - PAD_R)) / (points.length - 1)
  const y = (score: number) => PAD_T + ((100 - score) * (H - PAD_T - PAD_B)) / 100

  const line = points.map((p, i) => `${x(i)},${y(p.score)}`).join(' ')
  const last = points[points.length - 1]
  const h = hover !== null ? points[hover] : null

  return (
    <div className="trend" ref={wrapRef}>
      <svg viewBox={`0 0 ${W} ${H}`} role="img"
        aria-label={`fidelity score over ${points.length} runs, latest ${last.score}/100`}
        onMouseLeave={() => setHover(null)}>
        {[0, 25, 50, 75, 100].map(v => (
          <g key={v}>
            <line x1={PAD_L} x2={W - PAD_R} y1={y(v)} y2={y(v)} className="t-grid" />
            <text x={PAD_L - 6} y={y(v) + 3} textAnchor="end" className="t-axis">{v}</text>
          </g>
        ))}

        {h && <line x1={x(hover!)} x2={x(hover!)} y1={PAD_T} y2={H - PAD_B} className="t-crosshair" />}

        <polyline points={line} className="t-line" />

        {points.map((p, i) => (
          <g key={p.runId}>
            {/* 命中區比點大(dataviz:hit target > mark) */}
            <rect x={x(i) - 12} y={PAD_T} width={24} height={H - PAD_T - PAD_B}
              fill="transparent" onMouseEnter={() => setHover(i)} />
            <circle cx={x(i)} cy={y(p.score)} r={p.gateFailed ? 4.5 : 4}
              className={p.gateFailed ? 't-dot fail' : 't-dot'} />
          </g>
        ))}

        {/* 最新一點的選擇性直標(文字用 ink,不穿序列色) */}
        <text x={x(points.length - 1) + 8} y={y(last.score) + 4} className="t-label">
          {last.score}
        </text>

        <text x={PAD_L} y={H - 6} className="t-axis">{fmt(points[0].at)}</text>
        <text x={W - PAD_R} y={H - 6} textAnchor="end" className="t-axis">{fmt(last.at)}</text>
      </svg>

      {h && (
        <div className="t-tip" style={{ left: `${(x(hover!) / W) * 100}%` }}>
          <div className="m">{fmt(h.at)} · <strong>{h.score}/100</strong> · {h.gateFailed ? 'FAIL' : 'PASS'}</div>
          {h.commitSha && <div className="m dim">{h.commitSha.slice(0, 7)}</div>}
        </div>
      )}
    </div>
  )
}

const fmt = (iso: string) => iso.slice(5, 16).replace('T', ' ')
