import type {
  AuditEntry, BranchInfo, ContrastCard, ContrastReport, ContrastTrendPoint, InvitePreview, Me,
  MembersResponse, OverviewCard, ReportDocument, RunChanges, RunListItem, RunMeta, TrendPoint,
} from './types'
import { readDirection } from './direction'

/* 同源 SPA + cookie 認證:fetch 預設帶 cookie。401 丟 Unauthorized,App 層導去登入。 */

export class Unauthorized extends Error {}

async function handle<T>(res: Response): Promise<T> {
  if (res.status === 401) throw new Unauthorized()
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`
    try {
      const body = await res.json()
      if (body?.error) msg = body.error
    } catch { /* 非 JSON 的錯誤體就用狀態列 */ }
    throw new Error(msg)
  }
  return res.status === 204 ? (undefined as T) : (res.json() as Promise<T>)
}

const get = <T>(url: string) => fetch(url).then(res => handle<T>(res))
const post = <T>(url: string, body?: unknown) =>
  fetch(url, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  }).then(res => handle<T>(res))
const del = (url: string) => fetch(url, { method: 'DELETE' }).then(res => handle<void>(res))

// 資料
export const fetchRuns = () => get<RunListItem[]>('/api/runs')
export const fetchRunMeta = (id: string) => get<RunMeta>(`/api/runs/${id}`)
// direction 照凍結面 4 的規則讀(與伺服器同一套):不是 forward/reverse 就當壞報告,不畫
export const fetchRunReport = (id: string) =>
  get<ReportDocument>(`/api/runs/${id}/report`).then(doc => { readDirection(doc); return doc })
export const fetchBranches = () => get<BranchInfo[]>('/api/branches')
export const fetchOverview = (branch: string | null) =>
  get<OverviewCard[]>(`/api/overview${branch === null ? '' : `?branch=${encodeURIComponent(branch)}`}`)
export const fetchTrend = (projectId: string, route: string, branch: string | null) =>
  get<TrendPoint[]>(`/api/trend?project=${projectId}&route=${encodeURIComponent(route)}`
    + (branch === null ? '' : `&branch=${encodeURIComponent(branch)}`))
export const fetchChanges = (id: string) => get<RunChanges>(`/api/runs/${id}/changes`)

// XamlContrast(合併伺服器):原文是 `xamlcontrast --json` 的輸出,沒有 direction 這回事
export const fetchContrastReport = (id: string) => get<ContrastReport>(`/api/runs/${id}/report`)
export const fetchContrastOverview = (branch: string | null) =>
  get<ContrastCard[]>(`/api/contrast/overview${branch === null ? '' : `?branch=${encodeURIComponent(branch)}`}`)
export const fetchContrastTrend = (projectId: string, branch: string | null) =>
  get<ContrastTrendPoint[]>(`/api/contrast/trend?project=${projectId}`
    + (branch === null ? '' : `&branch=${encodeURIComponent(branch)}`))
export const deleteRun = (id: string) => del(`/api/runs/${id}`)
export const fetchAudit = (projectId: string) => get<AuditEntry[]>(`/api/projects/${projectId}/audit`)

// 認證
export const fetchMe = () => get<Me>('/api/auth/me')
export const login = (email: string, password: string) =>
  post<Me>('/api/auth/login', { email, password })
export const logout = () => post<void>('/api/auth/logout')

// 邀請
export const fetchInvite = (token: string) => get<InvitePreview>(`/api/invites/${token}`)
export const acceptInvite = (token: string, password: string) =>
  post<Me>(`/api/invites/${token}/accept`, { password })

// 專案管理(Owner)
export const fetchMembers = (projectId: string) =>
  get<MembersResponse>(`/api/projects/${projectId}/members`)
export const createInvite = (projectId: string, email: string, role: string) =>
  post<{ link: string; expires: string }>(`/api/projects/${projectId}/invites`, { email, role })
export const removeMember = (projectId: string, userId: string) =>
  del(`/api/projects/${projectId}/members/${userId}`)
export const rotateToken = (projectId: string) =>
  post<{ token: string }>(`/api/projects/${projectId}/token/rotate`)
