import { useEffect, useState, type ChangeEvent } from 'react'
import { apiFetch, formatInstantIst, uploadJobFiles, type ReconciliationSetupView as SetupView } from '../api'
import { useAuth } from '../auth/AuthContext'
import { formatBytes } from './reconciliation'

const OPERATOR_SYMBOLS = { Equal: '==', NotEqual: '!=', LessThan: '<', LessThanOrEqual: '<=', GreaterThan: '>', GreaterThanOrEqual: '>=' } as const

/** A file reconciliation job's files and rules, with "replace file" for the owner (for example today's extract). */
export function ReconciliationSetupView({
  jobId,
  canReplace,
  running,
  onChanged,
}: {
  jobId: string
  canReplace: boolean
  running: boolean
  onChanged: () => void
}) {
  const { token } = useAuth()
  const [view, setView] = useState<SetupView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busyAlias, setBusyAlias] = useState<string | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    apiFetch<SetupView>(`/jobs/${jobId}/reconciliation`, { signal: controller.signal }, token)
      .then((v) => {
        setError(null)
        setView(v)
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Failed to load the files and rules')
      })
    return () => controller.abort()
  }, [jobId, token, reload])

  async function replace(alias: string, e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setError(null)
    setBusyAlias(alias)
    try {
      const [uploaded] = await uploadJobFiles([file], token)
      const next = await apiFetch<SetupView>(
        `/jobs/${jobId}/reconciliation/files`,
        { method: 'PUT', body: JSON.stringify({ alias, fileId: uploaded.id }) },
        token,
      )
      setView(next)
      setReload((n) => n + 1)
      onChanged()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not replace the file')
    } finally {
      setBusyAlias(null)
    }
  }

  if (error && !view) return <p role="alert" className="rounded bg-red-50 px-3 py-2 text-red-700">{error}</p>
  if (!view) return <p className="text-slate-500">Loading files and rules…</p>

  return (
    <div className="space-y-3">
      {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-red-700">{error}</p>}

      <ul className="space-y-1">
        {view.files.map((f) => (
          <li key={f.id} className="flex flex-wrap items-center gap-2 rounded border border-slate-200 px-2 py-1.5">
            <code className="rounded bg-indigo-50 px-1.5 py-0.5 text-xs text-indigo-900">{f.alias}</code>
            <span className="text-slate-700">{f.fileName}</span>
            <span className="text-xs text-slate-500">
              {f.rowCount.toLocaleString()} rows · {f.headers.length} columns · {formatBytes(f.sizeBytes)} · uploaded {formatInstantIst(f.uploadedAtUtc)}
            </span>
            {canReplace && (
              <label
                className={
                  'ml-auto cursor-pointer rounded border border-slate-300 px-2 py-0.5 text-xs hover:bg-slate-100 ' +
                  (running || busyAlias !== null ? 'pointer-events-none opacity-50' : '')
                }
                title={running ? 'Wait for the current run to finish' : 'Upload a newer file under the same name'}
              >
                {busyAlias === f.alias ? 'Replacing…' : 'Replace file'}
                <input type="file" accept=".csv,text/csv" className="sr-only" disabled={running || busyAlias !== null} onChange={(e) => void replace(f.alias, e)} />
              </label>
            )}
          </li>
        ))}
      </ul>

      <ul className="space-y-1">
        {view.rules.map((r) => (
          <li key={r.id ?? r.name} className="rounded border border-slate-200 px-2 py-1.5">
            <span className="font-medium text-slate-800">{r.name}</span>
            <span className="mt-0.5 block font-mono text-xs text-slate-600">
              {r.leftExpression} {OPERATOR_SYMBOLS[r.operator]} {r.rightExpression}
            </span>
            <span className="block text-xs text-slate-500">
              {r.leftAlias === r.rightAlias
                ? `each row of ${r.leftAlias}`
                : `matched on ${r.leftAlias}.${r.leftKeyColumn} = ${r.rightAlias}.${r.rightKeyColumn}`}
              {r.tolerance > 0 && (r.operator === 'Equal' || r.operator === 'NotEqual') &&
                ` · tolerance ${r.tolerance}${r.toleranceMode === 'Percent' ? '%' : ''}`}
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}
