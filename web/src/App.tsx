import { useEffect, useState } from 'react'

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5031'

type HealthState =
  | { status: 'loading' }
  | { status: 'healthy'; raw: string }
  | { status: 'error'; message: string }

function App() {
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
    <main className="flex min-h-screen flex-col items-center justify-center gap-4 bg-slate-50 p-6">
      <h1 className="text-2xl font-semibold text-slate-800">Job Scheduler</h1>
      <p className="text-slate-500">Phase 0 scaffold — backend health check</p>
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
    </main>
  )
}

export default App
