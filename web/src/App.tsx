import { useEffect, useState } from 'react'
import RunsList from './RunsList'
import RunDetail from './RunDetail'
import Settings from './Settings'
import { Login, AcceptInvite } from './Auth'
import { fetchMe, logout } from './api'
import type { Me } from './types'

/* 路由(手刻,M3 起五條):/ | /runs/:id | /login | /invite/:token | /settings
   session 閘門:未登入 → 一律看到登入頁;唯一例外是 /invite/:token(連結即憑證)。 */

type Route =
  | { page: 'list' } | { page: 'run'; id: string }
  | { page: 'invite'; token: string } | { page: 'settings' }

function parse(pathname: string): Route {
  const run = pathname.match(/^\/runs\/([0-9a-f-]{36})$/i)
  if (run) return { page: 'run', id: run[1] }
  const invite = pathname.match(/^\/invite\/([A-Za-z0-9_-]{20,})$/)
  if (invite) return { page: 'invite', token: invite[1] }
  if (pathname === '/settings') return { page: 'settings' }
  return { page: 'list' }
}

export function navigate(to: string) {
  history.pushState(null, '', to)
  dispatchEvent(new PopStateEvent('popstate'))
}

export default function App() {
  const [route, setRoute] = useState(() => parse(location.pathname))
  const [me, setMe] = useState<Me | null | 'loading'>('loading')

  useEffect(() => {
    const onPop = () => setRoute(parse(location.pathname))
    addEventListener('popstate', onPop)
    fetchMe().then(setMe, () => setMe(null))
    return () => removeEventListener('popstate', onPop)
  }, [])

  const signedIn = (m: Me) => { setMe(m); navigate('/') }
  const signOut = async () => { await logout().catch(() => {}); setMe(null); navigate('/') }

  const body = () => {
    if (route.page === 'invite')
      return <AcceptInvite token={route.token} onSignedIn={signedIn} />
    if (me === 'loading') return <div className="loading">Loading…</div>
    if (me === null) return <Login onSignedIn={signedIn} />
    switch (route.page) {
      case 'run': return <RunDetail id={route.id} me={me} />
      case 'settings': return <Settings me={me} />
      default: return <RunsList />
    }
  }

  const isOwner = me !== 'loading' && me !== null && me.memberships.some(m => m.role === 'owner')
  return (
    <>
      <header className="masthead">
        <h1>
          <a href="/" onClick={e => { e.preventDefault(); navigate('/') }}>PARITY</a>
        </h1>
        <span className="sub">design fidelity — measured, not eyeballed</span>
        {me !== 'loading' && me !== null && (
          <span className="session">
            <span className="m dim">{me.email}</span>
            {isOwner && (
              <a href="/settings" onClick={e => { e.preventDefault(); navigate('/settings') }}>settings</a>
            )}
            <button className="chip" onClick={signOut}>sign out</button>
          </span>
        )}
      </header>
      {body()}
    </>
  )
}
