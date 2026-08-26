import { useMemo } from 'react'
import type { FidelityReport, NodeResult, Severity } from './types'

/* 藍圖窗格——本頁的簽名元素:不放截圖,直接把報告裡的量測座標畫成工程線框。
   實線 = 實作量到的框(顏色 = 嚴重度);選中節點時,虛線 = 設計期望的框,
   並拉一條尺寸標註線寫出「expected → actual」——工程圖講尺寸的方式。 */

interface Props {
  report: FidelityReport
  selected: string | null            // designId
  onSelect: (designId: string | null) => void
  visible: (n: NodeResult) => boolean
  showUnmatched: boolean
}

const PAD = 24

export default function Blueprint({ report, selected, onSelect, visible, showUnmatched }: Props) {
  const bounds = useMemo(() => {
    let maxX = 0, maxY = 0
    for (const n of report.nodes) {
      maxX = Math.max(maxX, n.renderedBox.x + n.renderedBox.width, n.designBox.x + n.designBox.width)
      maxY = Math.max(maxY, n.renderedBox.y + n.renderedBox.height, n.designBox.y + n.designBox.height)
    }
    for (const u of report.unmatched) {
      maxX = Math.max(maxX, u.designBox.x + u.designBox.width)
      maxY = Math.max(maxY, u.designBox.y + u.designBox.height)
    }
    return { w: Math.max(maxX, 100) + PAD * 2, h: Math.max(maxY, 100) + PAD * 2 }
  }, [report])

  const sel = report.nodes.find(n => n.designId === selected) ?? null
  // 標註字級跟著座標系縮放:viewBox 越大,使用者空間的字要越大才維持螢幕上可讀
  const fs = Math.max(11, bounds.w / 55)

  return (
    <div className="blueprint">
      <svg viewBox={`0 0 ${bounds.w} ${bounds.h}`} role="img"
        aria-label={`wireframe of ${report.route} drawn from measured boxes`}
        onClick={() => onSelect(null)}>
        <defs>
          <pattern id="grid" width="48" height="48" patternUnits="userSpaceOnUse">
            <path d="M 48 0 L 0 0 0 48" fill="none" stroke="#58789b" strokeOpacity="0.12" strokeWidth="1" />
          </pattern>
        </defs>
        <rect width={bounds.w} height={bounds.h} fill="url(#grid)" />

        {report.nodes.map(n => {
          const b = n.renderedBox
          if (b.width <= 0 || b.height <= 0) return null
          const cls = [
            'bp-node',
            n.severity !== 'none' ? `sev-${n.severity satisfies Severity}` : '',
            selected !== null && n.designId !== selected ? 'dim' : '',
            n.designId === selected ? 'sel' : '',
          ].join(' ')
          return (
            <rect key={n.designId} className={cls}
              x={b.x + PAD} y={b.y + PAD} width={b.width} height={b.height}
              onClick={e => { e.stopPropagation(); onSelect(n.designId) }}
              style={visible(n) ? undefined : { display: 'none' }}>
              <title>{`${n.designLayer} — ${n.selector}`}</title>
            </rect>
          )
        })}

        {showUnmatched && report.unmatched.map(u => {
          const b = u.designBox
          if (b.width <= 0 || b.height <= 0) return null
          return <rect key={u.designId} className="bp-unmatched"
            x={b.x + PAD} y={b.y + PAD} width={b.width} height={b.height} />
        })}

        {sel && <Callout node={sel} fs={fs} boundsW={bounds.w} />}
      </svg>
    </div>
  )
}

/* 選中節點:設計框(虛線)+ 尺寸標註(最重的一條硬落差)。 */
function Callout({ node, fs, boundsW }: { node: NodeResult; fs: number; boundsW: number }) {
  const d = node.designBox
  const r = node.renderedBox
  const worst = [...node.diffs].filter(x => !x.soft)
    .sort((a, b) => rank(b.severity) - rank(a.severity))[0]

  const label = worst
    ? `${worst.prop}  ${worst.expected} → ${worst.actual}${worst.unit ?? ''}`
    : 'no hard diffs'
  const labelW = label.length * fs * 0.62 + fs
  // 標註放在框上方;貼近上緣時放下方;水平方向夾在畫布內
  const above = r.y + 24 > fs * 3
  const ly = above ? r.y + 24 - fs * 1.1 : r.y + 24 + r.height + fs * 1.8
  const lx = Math.min(Math.max(r.x + 24, 4), Math.max(4, boundsW - labelW - 4))

  return (
    <g className="bp-callout">
      {d.width > 0 && d.height > 0 && (
        <rect className="bp-design" x={d.x + 24} y={d.y + 24} width={d.width} height={d.height} />
      )}
      <line className="bp-callout-line" x1={r.x + 24} y1={above ? r.y + 24 : r.y + 24 + r.height}
        x2={lx} y2={above ? ly + fs * 0.3 : ly - fs * 0.9} />
      <rect x={lx - fs * 0.4} y={ly - fs * 1.15} width={labelW} height={fs * 1.7}
        fill="#1b1e22" rx={2} />
      <text x={lx} y={ly} fill="#f6f7f4" fontSize={fs}>{label}</text>
    </g>
  )
}

function rank(s: Severity): number {
  return { none: 0, minor: 1, medium: 2, serious: 3, critical: 4 }[s]
}
