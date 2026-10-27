import { useState } from 'react'
import type { ContrastTrendPoint } from './types'

/* XamlContrast 的趨勢(合併伺服器)——兩條序列:fail 與 warn 的筆數隨 run 變化。
   dataviz 紀律:≥2 序列一律有圖例,再加最新一點的直標(數字用 ink,不穿序列色);
   y 從 0 起(計數),只有一條軸——覆蓋率是另一種量,不疊第二條 y 軸,放進 tooltip。
   色:fail = --sev-critical、warn = --chart。紅配琥珀(--sev-medium)validate_palette 實測
   deutan ΔE 5.0、正常視覺 12.3,兩項都 FAIL;紅配 #3e6fae 六檢全過(light surface #fdfdfc)。 */

const W = 560, H = 180
const PAD_L = 34, PAD_R = 70, PAD_T = 12, PAD_B = 22

type Series = { key: 'fail' | 'warn'; label: string; cls: string }
const SERIES: Series[] = [
  { key: 'fail', label: 'fail', cls: 'c-fail' },
  { key: 'warn', label: 'warn', cls: 'c-warn' },
]

/** 0 到「蓋得住最大值的整數刻度」;至少 4,免得全是 0 時軸只有一格 */
function niceMax(max: number): number {
  if (max <= 4) return 4
  const step = Math.pow(10, Math.floor(Math.log10(max)))
  for (const m of [1, 2, 2.5, 5, 10]) if (m * step >= max) return m * step
  return 10 * step
}

export default function ContrastTrendChart({ points }: { points: ContrastTrendPoint[] }) {
  const [hover, setHover] = useState<number | null>(null)

  if (points.length === 0) return <div className="loading">No XamlContrast runs for this project yet.</div>

  const top = niceMax(Math.max(...points.map(p => Math.max(p.fail, p.warn))))
  const x = (i: number) => points.length === 1
    ? (PAD_L + W - PAD_R) / 2
    : PAD_L + (i * (W - PAD_L - PAD_R)) / (points.length - 1)
  const y = (v: number) => PAD_T + ((top - v) * (H - PAD_T - PAD_B)) / top

  const last = points[points.length - 1]
  const h = hover !== null ? points[hover] : null
  // 直標:兩條線的終點太近就上下錯開,不疊字
  const labelY = { fail: y(last.fail) + 4, warn: y(last.warn) + 4 }
  if (Math.abs(labelY.fail - labelY.warn) < 12) {
    const mid = (labelY.fail + labelY.warn) / 2
    const failAbove = last.fail >= last.warn
    labelY.fail = mid + (failAbove ? -6 : 6)
    labelY.warn = mid + (failAbove ? 6 : -6)
  }

  return (
    <div className="trend">
      <div className="t-legend">
        {SERIES.map(s => (
          <span key={s.key}><span className={`t-swatch ${s.cls}`} />{s.label}</span>
        ))}
        <span className="dim">pairs below AA, per run</span>
      </div>
      <svg viewBox={`0 0 ${W} ${H}`} role="img"
        aria-label={`XamlContrast results over ${points.length} runs, latest ${last.fail} fail and ${last.warn} warn`}
        onMouseLeave={() => setHover(null)}>
        {/* 中線只在是整數時畫:計數軸上的 12.5 沒有意義 */}
        {[0, top / 2, top].filter(Number.isInteger).map(v => (
          <g key={v}>
            <line x1={PAD_L} x2={W - PAD_R} y1={y(v)} y2={y(v)} className="t-grid" />
            <text x={PAD_L - 6} y={y(v) + 3} textAnchor="end" className="t-axis">{v}</text>
          </g>
        ))}

        {h && <line x1={x(hover!)} x2={x(hover!)} y1={PAD_T} y2={H - PAD_B} className="t-crosshair" />}

        {SERIES.map(s => (
          <polyline key={s.key} className={`t-line ${s.cls}`}
            points={points.map((p, i) => `${x(i)},${y(p[s.key])}`).join(' ')} />
        ))}

        {points.map((p, i) => (
          <g key={p.runId}>
            <rect x={x(i) - 12} y={PAD_T} width={24} height={H - PAD_T - PAD_B}
              fill="transparent" onMouseEnter={() => setHover(i)} />
            {SERIES.map(s => (
              <circle key={s.key} cx={x(i)} cy={y(p[s.key])} r={4} className={`t-dot ${s.cls}`} />
            ))}
          </g>
        ))}

        {SERIES.map(s => (
          <text key={s.key} x={x(points.length - 1) + 8} y={labelY[s.key]} className="t-label">
            {last[s.key]} <tspan className="t-label-name">{s.label}</tspan>
          </text>
        ))}

        <text x={PAD_L} y={H - 6} className="t-axis">{fmt(points[0].at)}</text>
        <text x={W - PAD_R} y={H - 6} textAnchor="end" className="t-axis">{fmt(last.at)}</text>
      </svg>

      {h && (
        <div className="t-tip" style={{ left: `${(x(hover!) / W) * 100}%` }}>
          <div className="m">{fmt(h.at)} · <strong>{h.fail} fail · {h.warn} warn</strong> · {h.gateFailed ? 'FAIL' : 'PASS'}</div>
          <div className="m dim">coverage {h.coverage}% · {h.unresolved} unresolved{h.commitSha ? ` · ${h.commitSha.slice(0, 7)}` : ''}</div>
        </div>
      )}
    </div>
  )
}

const fmt = (iso: string) => iso.slice(5, 16).replace('T', ' ')
