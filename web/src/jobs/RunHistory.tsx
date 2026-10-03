import { useEffect, useState } from 'react'
import { apiFetch, formatInstantIst, type JobRun } from '../api'
import { useAuth } from '../auth/AuthContext'

const STEP_LABELS = {
  DownloadReport: 'Download report',
  Calculate: 'Calculate',
  SendEmail: 'Send email',
} as const

/** Run history and step log for one job. Polls while the job is still moving. */
export function RunHistory({ jobId, status, refreshKey }: { jobId: string; status: string; refreshKey: number }) {
  const { token } = useAuth()
  const [runs, setRuns] = useState<JobRun[]>([])
  const [error, setError] = useState<string | null>(null)
  const active = status === 'InProgress' || runs.some((r) => r.status === 'Pending' || r.status === 'Running')

  useEffect(() => {
    const controller = new AbortController()
    const load = () =>
      apiFetch<JobRun[]>(`/jobs/${jobId}/runs`, { signal: controller.signal }, token)
        .then((list) => {
          setError(null)
          setRuns(list)
        })
        .catch((err: unknown) => {
          if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Failed to load runs')
        })

    void load()
    const timer = active ? setInterval(() => void load(), 2000) : undefined
    return () => {
      controller.abort()
      if (timer) clearInterval(timer)
    }
  }, [jobId, token, status, refreshKey, active])

  if (error) return <p role="alert" className="rounded bg-red-50 px-3 py-2 text-red-700">{error}</p>
  if (runs.length === 0) return <p className="text-slate-500">No runs yet.</p>

  return (
    <div className="space-y-3">
      {runs.map((run) => (
        <div key={run.id} className="rounded border border-slate-200 p-3">
          <div className="flex flex-wrap items-center gap-2">
            <span
              className={
                'rounded px-2 py-0.5 text-xs font-medium ' +
                (run.status === 'Succeeded'
                  ? 'bg-emerald-50 text-emerald-700'
                  : run.status === 'Failed'
                    ? 'bg-red-50 text-red-700'
                    : 'bg-amber-50 text-amber-700')
              }
            >
              {run.status}
            </span>
            <span className="text-xs text-slate-500">
              attempt {run.attempt} · auto-retries used {run.autoRetriesUsed} · started {formatInstantIst(run.startedAtUtc)}
            </span>
          </div>
          {run.error && (
            <p className="mt-1 text-xs text-red-700">
              {run.failedStep ? `${STEP_LABELS[run.failedStep]}: ` : ''}
              {run.error}
            </p>
          )}
          <ol className="mt-2 space-y-1 text-xs">
            {run.steps.map((s, i) => (
              <li key={i} className="flex gap-2">
                <span className={s.status === 'Succeeded' ? 'text-emerald-600' : 'text-red-600'}>
                  {s.status === 'Succeeded' ? '✓' : '✗'}
                </span>
                <span className="font-medium text-slate-700">{STEP_LABELS[s.step]}</span>
                <span className="text-slate-400">a{s.attempt}</span>
                <span className="truncate text-slate-500" title={s.output}>
                  {s.output}
                </span>
              </li>
            ))}
          </ol>
        </div>
      ))}
    </div>
  )
}
