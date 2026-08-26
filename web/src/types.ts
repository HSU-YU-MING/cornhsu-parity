// report.json 的 wire 型別——鏡射 1.0 凍結面 4 的形狀(camelCase、字串 enum)。
// severity/status 是封閉集;reason/matchedBy 是開放集合 → 一律 string,不做窄化。

export interface Box {
  x: number
  y: number
  width: number
  height: number
}

export type Severity = 'none' | 'minor' | 'medium' | 'serious' | 'critical'

export interface PropDiff {
  prop: string
  expected: string
  actual: string
  unit: string | null
  delta: number | null
  tolerance: number
  severity: Severity
  status: 'mismatch' | 'missing'
  soft: boolean
}

export interface NodeResult {
  designLayer: string
  designId: string
  selector: string
  matchedBy: string
  severity: Severity
  diffs: PropDiff[]
  designBox: Box
  renderedBox: Box
}

export interface UnmatchedNode {
  designLayer: string
  designId: string
  reason: string
  designBox: Box
}

export interface ReportSummary {
  designNodes: number
  matched: number
  unmatched: number
  nodesWithDiffs: number
  critical: number
  serious: number
  medium: number
  minor: number
  maxSeverity: Severity
}

export interface FidelityReport {
  route: string
  url: string
  designReference: string
  nodes: NodeResult[]
  unmatched: UnmatchedNode[]
  summary: ReportSummary
}

export interface ReportDocument {
  schemaVersion: number
  reports: FidelityReport[]
}

// /api/runs 的列表項與 /api/runs/{id} 的中繼資料
export interface RunMeta {
  id: string
  createdAt: string
  score: number
  gateFailed: boolean
  commitSha: string | null
  branch: string | null
  triggeredBy: string | null
  project: string
}

export interface RunListItem extends RunMeta {
  pages: number
}

// M4:總覽卡與趨勢點
export interface OverviewCard {
  projectId: string
  project: string
  route: string
  url: string
  lastScore: number
  lastGateFailed: boolean
  lastAt: string
  prevScore: number | null
  runCount: number
}

export interface TrendPoint {
  runId: string
  at: string
  score: number
  gateFailed: boolean
  commitSha: string | null
}
