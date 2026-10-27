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
  /** 2.0.0 起一律寫出;舊報告沒有這個 key(= forward)。伺服器不收 reverse 報告 */
  direction?: 'forward' | 'reverse'
}

// /api/runs 的列表項與 /api/runs/{id} 的中繼資料
/** 合併伺服器:哪個工具的 run。字串比較,不窄化——伺服器多收一種工具時舊前端不該壞 */
export type Tool = 'parity' | 'xamlcontrast' | (string & {})

export interface RunMeta {
  id: string
  tool: Tool
  createdAt: string
  /** 只有 parity 的 run 有分數;xamlcontrast 是 null */
  score: number | null
  gateFailed: boolean
  commitSha: string | null
  branch: string | null
  triggeredBy: string | null
  repoUrl: string | null
  projectId: string
  project: string
}

export interface AuditEntry {
  at: string
  actorEmail: string
  action: string // 開放字彙:invite-created / invite-accepted / member-removed / token-rotated / run-deleted…
  detail: string | null
}

// M4.5:分支與「變了什麼」
export interface BranchInfo {
  branch: string // 空字串 = 無分支的 push
  lastAt: string
}

export interface ChangedDiff {
  route: string
  designLayer: string
  selector: string
  prop: string
  severity: Severity
  expected: string
  actual: string
}

export interface RunChanges {
  prevRunId: string | null
  prevCreatedAt: string | null
  regressions: ChangedDiff[]
  worsened: ChangedDiff[]
  fixed: ChangedDiff[]
  unchanged: number
}

export interface RunListItem extends RunMeta {
  pages: number
  /** xamlcontrast 的 run 才有;parity 是 null */
  contrast: { fail: number; warn: number; unresolved: number; coverage: number } | null
}

// ── XamlContrast(合併伺服器)──────────────────────────────────────
// /api/runs/{id} 在 xamlcontrast 的 run 上帶的摘要(伺服器 ContrastResult 的欄位)
export interface ContrastSummary {
  toolVersion: string | null
  exitCode: number | null
  files: number
  pairs: number
  coverage: number
  unresolved: number
  fail: number
  warn: number
  ok: number
  decorative: number
  suppressed: number
  parseErrors: number
  paletteMode: string // 開放集合
  paletteDegraded: boolean
  failOn: string      // 開放集合
}

export interface ContrastRunMeta extends RunMeta {
  contrast: ContrastSummary
}

// `xamlcontrast --json` 原文(schemaVersion 6,XamlContrast 1.0 凍結)的子集——只列畫面用到的欄位。
// category / symmetry 是封閉集;reason / theme 是開放集合 → string
export type ContrastCategory = 'ok' | 'warn' | 'fail' | 'decorative'

export interface ContrastFinding {
  file: string
  line: number
  element: string
  fg: string
  bg: string
  ratioDark: number | null
  ratioLight: number | null
  threshold: number
  category: ContrastCategory
  symmetry: 'both-low' | 'dark-fails' | 'light-fails' | 'single-theme' | null
  isLargeText: boolean
  theme: string | null
  baseline: 'known' | 'new' | 'worsened' | null
}

export interface ContrastUnresolvedSite {
  file: string
  line: number
  reason: string
  value: string
}

export interface ContrastReport {
  schemaVersion: number
  findings: ContrastFinding[]
  unresolvedSites: ContrastUnresolvedSite[]
  summary: { paletteDetail: string; unresolvedBy: Record<string, number> }
}

export interface ContrastCard {
  projectId: string
  project: string
  fail: number
  warn: number
  unresolved: number
  coverage: number
  gateFailed: boolean
  lastAt: string
  prevFail: number | null
  prevWarn: number | null
  runCount: number
}

export interface ContrastTrendPoint {
  runId: string
  at: string
  fail: number
  warn: number
  unresolved: number
  coverage: number
  gateFailed: boolean
  commitSha: string | null
}

/** XamlContrast 的「跟上次比」(伺服器 ContrastCompare)。一組配對 = 檔案|元素|前景|底色,不含行號。
    不帶 prev 的是這一次;不存在的那一側是 null / 0。lines:這一次的行號(修好的是上一次的),最多 20 個 */
export interface ContrastChange {
  file: string
  element: string
  fg: string
  bg: string
  category: 'fail' | 'warn' | null
  prevCategory: 'fail' | 'warn' | null
  count: number
  prevCount: number
  worst: number | null
  prevWorst: number | null
  lines: number[]
}

export interface ContrastChanges {
  prevRunId: string | null
  prevCreatedAt: string | null
  prevToolVersion: string | null
  prevExitCode: number | null
  regressions: ContrastChange[]
  worsened: ContrastChange[]
  improved: ContrastChange[]
  fixed: ContrastChange[]
  unchanged: number
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

// M3:登入身分與成員管理
export type Role = 'owner' | 'member' | 'viewer'

export interface MembershipInfo {
  projectId: string
  project: string
  role: Role
}

export interface Me {
  email: string
  memberships: MembershipInfo[]
}

export interface InvitePreview {
  project: string
  email: string
  role: Role
}

export interface MemberRow {
  userId: string
  email: string
  role: Role
  joined: string
}

export interface MembersResponse {
  members: MemberRow[]
  pendingInvites: { email: string; role: Role; expires: string }[]
}
