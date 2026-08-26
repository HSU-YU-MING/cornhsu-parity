import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { createInvite, fetchAudit, fetchMembers, removeMember, rotateToken } from './api'
import type { AuditEntry, Me, MembersResponse, Role } from './types'

/* 專案設定(M3,Owner 限定):成員清單、發邀請連結、CI token 換發。
   邀請連結與新 token 都只顯示一次——這頁的工作是把它交到你手上,不是替你保管。 */

export default function Settings({ me }: { me: Me }) {
  const owned = me.memberships.filter(m => m.role === 'owner')
  const [projectId, setProjectId] = useState(owned[0]?.projectId ?? null)

  if (owned.length === 0)
    return <div className="empty">Settings are for project owners — your role is read-only.</div>

  return (
    <>
      {owned.length > 1 && (
        <div className="pagetabs">
          {owned.map(m => (
            <button key={m.projectId} className={m.projectId === projectId ? 'on' : ''}
              onClick={() => setProjectId(m.projectId)}>{m.project}</button>
          ))}
        </div>
      )}
      {projectId && <ProjectSettings key={projectId} projectId={projectId}
        projectName={owned.find(m => m.projectId === projectId)!.project} myEmail={me.email} />}
    </>
  )
}

function ProjectSettings({ projectId, projectName, myEmail }: {
  projectId: string; projectName: string; myEmail: string
}) {
  const [data, setData] = useState<MembersResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const reload = useCallback(
    () => fetchMembers(projectId).then(setData, e => setError(String(e))), [projectId])
  useEffect(() => { void reload() }, [reload])

  // 發邀請
  const [inviteEmail, setInviteEmail] = useState('')
  const [inviteRole, setInviteRole] = useState<Role>('viewer')
  const [inviteLink, setInviteLink] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submitInvite = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true); setError(null); setInviteLink(null)
    try {
      const r = await createInvite(projectId, inviteEmail, inviteRole)
      setInviteLink(r.link); setInviteEmail('')
      void reload()
    } catch (err) { setError(err instanceof Error ? err.message : String(err)) }
    finally { setBusy(false) }
  }

  // CI token 換發
  const [newToken, setNewToken] = useState<string | null>(null)
  const rotate = async () => {
    if (!confirm('Rotate the CI token? The current token stops working immediately — update your CI secret right after.')) return
    try { setNewToken((await rotateToken(projectId)).token) }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
  }

  const remove = async (userId: string, email: string | null) => {
    if (!confirm(`Remove ${email ?? 'this member'} from ${projectName}?`)) return
    try { await removeMember(projectId, userId); void reload() }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
  }

  if (error && !data) return <div className="error">{error}</div>
  if (!data) return <div className="loading">Loading…</div>

  return (
    <div className="settings">
      <section className="pane">
        <div className="pane-h"><span className="tag">members — {projectName}</span></div>
        <table className="runs"><tbody>
          {data.members.map(m => (
            <tr key={m.userId}>
              <td>{m.email}{m.email === myEmail && <span className="m dim"> (you)</span>}</td>
              <td><span className="m">{m.role}</span></td>
              <td className="m dim">{m.joined.slice(0, 10)}</td>
              <td>
                <button className="chip" onClick={() => remove(m.userId, m.email)}>remove</button>
              </td>
            </tr>
          ))}
          {data.pendingInvites.map((i, n) => (
            <tr key={`p${n}`} style={{ opacity: 0.6 }}>
              <td>{i.email}</td>
              <td><span className="m">{i.role}</span></td>
              <td className="m dim" colSpan={2}>invited — expires {i.expires.slice(0, 10)}</td>
            </tr>
          ))}
        </tbody></table>
      </section>

      <section className="pane">
        <div className="pane-h"><span className="tag">invite someone</span></div>
        <form className="invite-form" onSubmit={submitInvite}>
          <input type="email" required placeholder="their email"
            value={inviteEmail} onChange={e => setInviteEmail(e.target.value)} />
          <select value={inviteRole} onChange={e => setInviteRole(e.target.value as Role)}>
            <option value="viewer">viewer — read only</option>
            <option value="member">member — read + push</option>
            <option value="owner">owner — manage project</option>
          </select>
          <button className="btn" disabled={busy}>Create invite link</button>
        </form>
        {inviteLink && (
          <div className="reveal-once">
            <p className="tag">hand them this link — it is shown once and expires in 7 days</p>
            <code className="m">{inviteLink}</code>
            <button className="chip" onClick={() => navigator.clipboard.writeText(inviteLink)}>copy</button>
          </div>
        )}
      </section>

      <section className="pane">
        <div className="pane-h"><span className="tag">ci token</span></div>
        <div className="invite-form">
          <p style={{ margin: 0 }}>The token your CI uses with <code className="m">parity push</code>.
            Rotating it revokes the old one immediately.</p>
          <button className="btn" onClick={rotate}>Rotate token</button>
        </div>
        {newToken && (
          <div className="reveal-once">
            <p className="tag">new token — shown once, update your ci secret now</p>
            <code className="m">{newToken}</code>
            <button className="chip" onClick={() => navigator.clipboard.writeText(newToken)}>copy</button>
          </div>
        )}
        {error && data && <p className="form-error">{error}</p>}
      </section>

      <AuditSection projectId={projectId} />
    </div>
  )
}

/* 稽核紀錄(M4.6):誰在什麼時候動了什麼——邀請、成員異動、token 換發、刪 run。 */
function AuditSection({ projectId }: { projectId: string }) {
  const [entries, setEntries] = useState<AuditEntry[] | null>(null)
  useEffect(() => { fetchAudit(projectId).then(setEntries, () => setEntries([])) }, [projectId])

  return (
    <section className="pane">
      <div className="pane-h"><span className="tag">activity — who did what</span></div>
      {entries === null && <div className="loading">Loading…</div>}
      {entries && entries.length === 0 && (
        <div className="unmatched-note">No activity recorded yet.</div>
      )}
      {entries && entries.length > 0 && (
        <table className="runs"><tbody>
          {entries.map((a, i) => (
            <tr key={i}>
              <td className="m dim">{a.at.replace('T', ' ').slice(0, 16)}</td>
              <td>{a.actorEmail}</td>
              <td className="m">{a.action}</td>
              <td className="m dim">{a.detail ?? ''}</td>
            </tr>
          ))}
        </tbody></table>
      )}
    </section>
  )
}
