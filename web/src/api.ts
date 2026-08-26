import type { ReportDocument, RunListItem, RunMeta } from './types'

async function get<T>(url: string): Promise<T> {
  const res = await fetch(url)
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`)
  return res.json() as Promise<T>
}

export const fetchRuns = () => get<RunListItem[]>('/api/runs')
export const fetchRunMeta = (id: string) => get<RunMeta>(`/api/runs/${id}`)
export const fetchRunReport = (id: string) => get<ReportDocument>(`/api/runs/${id}/report`)
