import { useEffect, useState } from 'react'
import { apiFetch, formatInstantIst, type JobRun, type RunStep } from '../api'
import { useAuth } from '../auth/AuthContext'
import { ReconciliationResults } from './ReconciliationResults'

const DEFAULT_STEP_LABELS = {
  DownloadReport: 'Download report',
  Calculate: 'Calculate',
  SendEmail: 'Send email',
} as const

/** File reconciliation jobs have no finance report: the first step pins the uploaded files, the second checks every record. */
const FILE_STEP_LABELS = {
  DownloadReport: 'Load files',
  Calculate: 'Reconcile records',
  SendEmail: 'Send email',
} as const

/** File jobs store JSON in the first two steps; show it as a sentence. Anything unexpected falls back to the raw text. */
function friendlyOutput(step: RunStep): string {
  try {
    if (step.step === 'DownloadReport') {
      const files = (JSON.parse(step.output) as { Files: { Alias: string; Rows: number }[] }).Files
      return `Pinned ${files.length} file(s): ${files.map((f) => `${f.Alias} (${f.Rows.toLocaleString()} rows)`).join(', ')}`
    }
    if (step.step === 'Calculate') return (JSON.parse(step.output) as { Summary: string }).Summary.split('\n')[0]
  } catch {
    // not the JSON we expected; show it as it is
  }
  return step.output
}

/** Run history and step log for one job. Polls while the job is still moving. */
export function RunHistory({
  jobId,
  jobName,
  status,
  refreshKey,
  fileRecon = false,
}: {
  jobId: string
  jobName: string
  status: string
  refreshKey: number
  fileRecon?: boolean
}) {
  const { token } = useAuth()
  const STEP_LABELS = fileRecon ? FILE_STEP_LABELS : DEFAULT_STEP_LABELS
  const [openResults, setOpenResults] = useState<Record<string, boolean>>({})
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
      {runs.map((run, index) => {
        // The latest finished run opens its record results by default; older runs open on demand.
        const resultsOpen = openResults[run.id] ?? index === 0
        return (
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
            {run.triggerPayload && (
              <p className="mt-1 truncate font-mono text-xs text-slate-500" title={run.triggerPayload}>
                webhook payload: {run.triggerPayload}
              </p>
            )}
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
                    {fileRecon ? friendlyOutput(s) : s.output}
                  </span>
                </li>
              ))}
            </ol>
            {fileRecon && run.status === 'Succeeded' && (
              <div className="mt-3">
                <button
                  type="button"
                  onClick={() => setOpenResults({ ...openResults, [run.id]: !resultsOpen })}
                  className="text-xs font-medium text-indigo-700 hover:underline"
                >
                  {resultsOpen ? 'Hide record results' : 'Show record results'}
                </button>
                {resultsOpen && (
                  <div className="mt-2">
                    <ReconciliationResults runId={run.id} jobName={jobName} />
                  </div>
                )}
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}
