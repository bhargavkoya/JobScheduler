import { useEffect, useState } from 'react'
import { API_BASE_URL } from './api'
import { useAuth } from './auth/AuthContext'
import { ProtectedRoute } from './auth/ProtectedRoute'

type HealthState =
  | { status: 'loading' }
  | { status: 'healthy'; raw: string }
  | { status: 'error'; message: string }

function Health() {
  const [health, setHealth] = useState<HealthState>({ status: 'loading' })

  useEffect(() => {
    const controller = new AbortController()

    fetch(`${API_BASE_URL}/health`, { signal: controller.signal })
      .then(async (res) => {
        const text = await res.text()
        if (!res.ok) throw new Error(`HTTP ${res.status}: ${text}`)
        setHealth({ status: 'healthy', raw: text })
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        setHealth({ status: 'error', message: err instanceof Error ? err.message : String(err) })
      })

    return () => controller.abort()
  }, [])

  return (
    <div
      className={
        'rounded-md border px-4 py-2 font-mono text-sm ' +
        (health.status === 'healthy'
          ? 'border-emerald-300 bg-emerald-50 text-emerald-700'
          : health.status === 'error'
            ? 'border-red-300 bg-red-50 text-red-700'
            : 'border-slate-300 bg-slate-100 text-slate-600')
      }
    >
      {health.status === 'loading' && 'Checking API health...'}
      {health.status === 'healthy' && `API /health -> ${health.raw}`}
      {health.status === 'error' && `API /health unreachable: ${health.message}`}
    </div>
  )
}

function Home() {
  const { user, logout } = useAuth()
  if (!user) return null

  return (
    <div className="min-h-screen bg-slate-50">
      <header className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-200 bg-white px-6 py-3">
        <h1 className="text-lg font-semibold text-slate-800">Job Scheduler</h1>
        <div className="flex items-center gap-3 text-sm text-slate-600">
          <span>{user.email}</span>
          <span className="rounded bg-indigo-50 px-2 py-0.5 text-indigo-700">{user.role}</span>
          <span className="rounded bg-slate-100 px-2 py-0.5">{user.primaryTeam}</span>
          <button onClick={logout} className="rounded border border-slate-300 px-2 py-1 hover:bg-slate-100">
            Sign out
          </button>
        </div>
      </header>

      <main className="mx-auto max-w-2xl space-y-4 p-6">
        <section className="rounded-lg border border-slate-200 bg-white p-4">
          <h2 className="mb-2 font-medium text-slate-800">Your access</h2>
          <dl className="grid grid-cols-[8rem_1fr] gap-y-1 text-sm text-slate-600">
            <dt>Role</dt>
            <dd>{user.role}</dd>
            <dt>Primary team</dt>
            <dd>{user.primaryTeam}</dd>
            <dt>Observer teams</dt>
            <dd>{user.observerTeams.length ? user.observerTeams.join(', ') : 'none'}</dd>
            <dt>Claims</dt>
            <dd>{user.permissions.length ? user.permissions.join(', ') : 'none'}</dd>
          </dl>
        </section>
        <Health />
      </main>
    </div>
  )
}

function App() {
  return (
    <ProtectedRoute>
      <Home />
    </ProtectedRoute>
  )
}

export default App
