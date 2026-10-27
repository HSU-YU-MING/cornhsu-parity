import { deleteRun } from './api'
import type { Me, RunMeta } from './types'
import { navigate } from './App'

/* run 詳情頁頁首的共用格:專案、收到時間、分支、commit,以及 Owner 的刪除鈕。
   parity 與 XamlContrast 的詳情頁各自放自己的「結果」格在前面。 */

export function MetaCells({ meta }: { meta: RunMeta }) {
  return (
    <>
      <div className="cell"><span className="tag">project</span><span className="v">{meta.project}</span></div>
      <div className="cell"><span className="tag">received</span>
        <span className="v">{meta.createdAt.replace('T', ' ').slice(0, 19)} UTC</span></div>
      {meta.branch && <div className="cell"><span className="tag">branch</span><span className="v">{meta.branch}</span></div>}
      {meta.commitSha && (
        <div className="cell"><span className="tag">commit</span>
          <span className="v">
            {meta.repoUrl
              ? <a href={`${meta.repoUrl}/commit/${meta.commitSha}`} target="_blank" rel="noreferrer">
                  {meta.commitSha.slice(0, 7)}</a>
              : meta.commitSha.slice(0, 7)}
          </span>
        </div>
      )}
    </>
  )
}

export function DeleteRunButton({ meta, me }: { meta: RunMeta; me: Me }) {
  if (!me.memberships.some(m => m.projectId === meta.projectId && m.role === 'owner')) return null
  return (
    <button className="btn-danger" onClick={async () => {
      if (!confirm('Delete this run? Its data leaves the overview and trends permanently.')) return
      await deleteRun(meta.id)
      navigate('/')
    }}>Delete run</button>
  )
}
