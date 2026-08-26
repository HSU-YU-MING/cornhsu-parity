import { useEffect, useState, type FormEvent } from 'react'
import { acceptInvite, fetchInvite, login } from './api'
import type { InvitePreview, Me } from './types'

/* 登入與接受邀請(M3)。邀請制:沒有註冊頁——帳號只從邀請連結誕生。 */

export function Login({ onSignedIn }: { onSignedIn: (me: Me) => void }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true); setError(null)
    try { onSignedIn(await login(email, password)) }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
    finally { setBusy(false) }
  }

  return (
    <form className="authbox" onSubmit={submit}>
      <h2 className="tag">sign in</h2>
      <label className="tag" htmlFor="email">email</label>
      <input id="email" type="email" required autoComplete="username"
        value={email} onChange={e => setEmail(e.target.value)} />
      <label className="tag" htmlFor="pw">password</label>
      <input id="pw" type="password" required autoComplete="current-password"
        value={password} onChange={e => setPassword(e.target.value)} />
      {error && <p className="form-error">{error}</p>}
      <button className="btn" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      <p className="form-hint">No account? Accounts are created from invite links —
        ask your project owner for one. Forgot your password? Same answer: a fresh
        invite lets you set a new one.</p>
    </form>
  )
}

export function AcceptInvite({ token, onSignedIn }: { token: string; onSignedIn: (me: Me) => void }) {
  const [invite, setInvite] = useState<InvitePreview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    fetchInvite(token).then(setInvite, e => setError(e instanceof Error ? e.message : String(e)))
  }, [token])

  if (error && !invite) return <div className="error">{error}</div>
  if (!invite) return <div className="loading">Loading…</div>

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true); setError(null)
    try { onSignedIn(await acceptInvite(token, password)) }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
    finally { setBusy(false) }
  }

  return (
    <form className="authbox" onSubmit={submit}>
      <h2 className="tag">join project</h2>
      <p>You were invited to <strong>{invite.project}</strong> as{' '}
        <span className="m">{invite.role}</span>.</p>
      <label className="tag">email</label>
      <input value={invite.email} disabled autoComplete="username" />
      <label className="tag" htmlFor="pw">choose a password (10+ characters)</label>
      <input id="pw" type="password" required minLength={10} autoComplete="new-password"
        value={password} onChange={e => setPassword(e.target.value)} />
      {error && <p className="form-error">{error}</p>}
      <button className="btn" disabled={busy}>{busy ? 'Joining…' : 'Join'}</button>
      <p className="form-hint">Already have an account with this email?
        Enter its existing password instead.</p>
    </form>
  )
}
