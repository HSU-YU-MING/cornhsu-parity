import { useEffect, useState } from 'react'
import RunsList from './RunsList'
import RunDetail from './RunDetail'

/* 兩條路由(/ 與 /runs/:id),手刻 history 路由——不為兩頁引一顆 router。 */

function parse(pathname: string): { page: 'list' } | { page: 'run'; id: string } {
  const m = pathname.match(/^\/runs\/([0-9a-f-]{36})$/i)
  return m ? { page: 'run', id: m[1] } : { page: 'list' }
}

export function navigate(to: string) {
  history.pushState(null, '', to)
  dispatchEvent(new PopStateEvent('popstate'))
}

export default function App() {
  const [route, setRoute] = useState(() => parse(location.pathname))

  useEffect(() => {
    const onPop = () => setRoute(parse(location.pathname))
    addEventListener('popstate', onPop)
    return () => removeEventListener('popstate', onPop)
  }, [])

  return (
    <>
      <header className="masthead">
        <h1>
          <a href="/" onClick={e => { e.preventDefault(); navigate('/') }}>PARITY</a>
        </h1>
        <span className="sub">design fidelity — measured, not eyeballed</span>
      </header>
      {route.page === 'list' ? <RunsList /> : <RunDetail id={route.id} />}
    </>
  )
}
