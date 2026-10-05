import { useCallback, useEffect, useState } from 'react'
import { apiFetch, downloadFile, webhookUrl, type Job, type JobStatus, type ScheduleType, type Team } from '../api'
import { useAuth } from '../auth/AuthContext'
import { CreateJobForm } from './CreateJobForm'
import { ApprovalHistory } from './ApprovalHistory'
import { RunHistory } from './RunHistory'
import { AtRiskBadge, StatusBadge } from './StatusBadge'
import { STATUS_LABELS } from './statusLabels'

const STATUSES: JobStatus[] = ['Scheduled', 'InProgress', 'Completed', 'Cancelled', 'Failed', 'NeedsManualAction']
const TEAMS: Team[] = ['Business', 'Technical']
const TYPES: ScheduleType[] = ['Fixed', 'Recurrent', 'EventBased', 'TriggerBased', 'Manual']

function formatIst(value: string | null) {
  return value ? value.replace('T', ' ').slice(0, 16) + ' IST' : '—'
}

export function JobsPage() {
  const { token, user } = useAuth()
  const [jobs, setJobs] = useState<Job[]>([])
  const [allJobs, setAllJobs] = useState<Job[]>([]) // unfiltered, for the summary strip
  const [mineOnly, setMineOnly] = useState(false)
  const [atRiskOnly, setAtRiskOnly] = useState(false)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [status, setStatus] = useState('')
  const [team, setTeam] = useState('')
  const [type, setType] = useState('')
  const [creating, setCreating] = useState(false)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [runsRefresh, setRunsRefresh] = useState(0)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    (signal?: AbortSignal) => {
      const params = new URLSearchParams()
      if (status) params.set('status', status)
      if (team) params.set('team', team)
      if (type) params.set('type', type)
      if (mineOnly && user) params.set('ownerId', user.id)
      // Dates are picked as IST calendar days; send them as UTC instants.
      if (from) params.set('createdFromUtc', new Date(`${from}T00:00:00+05:30`).toISOString())
      if (to) params.set('createdToUtc', new Date(`${to}T23:59:59+05:30`).toISOString())
      return Promise.all([
        apiFetch<Job[]>(`/jobs?${params}`, { signal }, token),
        apiFetch<Job[]>('/jobs', { signal }, token),
      ])
        .then(([list, everything]) => {
          setError(null)
          setJobs(list)
          setAllJobs(everything)
        })
        .catch((err: unknown) => {
          if (signal?.aborted) return
          setError(err instanceof Error ? err.message : 'Failed to load jobs')
        })
    },
    [token, user, status, team, type, mineOnly, from, to],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])

  const shown = atRiskOnly ? jobs.filter((j) => j.isAtRisk) : jobs
  const selected = jobs.find((j) => j.id === selectedId) ?? null
  const anyInProgress = jobs.some((j) => j.status === 'InProgress')
  const atRiskCount = allJobs.filter((j) => j.isAtRisk).length

  // Keep statuses live while anything is running.
  useEffect(() => {
    if (!anyInProgress) return
    const timer = setInterval(() => void load(), 2000)
    return () => clearInterval(timer)
  }, [anyInProgress, load])

  async function act(path: string, headers?: Record<string, string>) {
    setError(null)
    setBusy(true)
    try {
      await apiFetch(path, { method: 'POST', headers }, token)
      await load()
      setRunsRefresh((n) => n + 1)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed')
    } finally {
      setBusy(false)
    }
  }

  const canRetry = user?.role === 'Admin' || user?.permissions.includes('jobs.retry')

  const select = 'rounded border border-slate-300 bg-white px-2 py-1 text-sm'

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2" aria-label="Job summary">
        {STATUSES.map((s) => {
          const count = allJobs.filter((j) => j.status === s).length
          return (
            <button
              key={s}
              onClick={() => setStatus(status === s ? '' : s)}
              className={
                'rounded-lg border px-3 py-1.5 text-left text-xs ' +
                (status === s ? 'border-indigo-400 bg-indigo-50' : 'border-slate-200 bg-white hover:bg-slate-50')
              }
            >
              <div className="text-lg font-semibold text-slate-800">{count}</div>
              <div className="text-slate-500">{STATUS_LABELS[s]}</div>
            </button>
          )
        })}
        <button
          onClick={() => setAtRiskOnly((v) => !v)}
          className={
            'rounded-lg border px-3 py-1.5 text-left text-xs ' +
            (atRiskOnly ? 'border-orange-400 bg-orange-50' : 'border-slate-200 bg-white hover:bg-slate-50')
          }
        >
          <div className="text-lg font-semibold text-orange-700">{atRiskCount}</div>
          <div className="text-slate-500">At risk</div>
        </button>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <select aria-label="Status" value={status} onChange={(e) => setStatus(e.target.value)} className={select}>
          <option value="">All statuses</option>
          {STATUSES.map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
        <select aria-label="Team" value={team} onChange={(e) => setTeam(e.target.value)} className={select}>
          <option value="">All teams</option>
          {TEAMS.map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
        <select aria-label="Job type" value={type} onChange={(e) => setType(e.target.value)} className={select}>
          <option value="">All types</option>
          {TYPES.map((s) => (
            <option key={s}>{s}</option>
          ))}
        </select>
        <label className="flex items-center gap-1 text-sm text-slate-600">
          From
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} className={select} />
        </label>
        <label className="flex items-center gap-1 text-sm text-slate-600">
          To
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} className={select} />
        </label>
        <label className="flex items-center gap-1 text-sm text-slate-600">
          <input type="checkbox" checked={mineOnly} onChange={(e) => setMineOnly(e.target.checked)} />
          My jobs
        </label>
        <button
          onClick={() => setCreating(true)}
          className="ml-auto rounded bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-indigo-700"
        >
          New job
        </button>
      </div>

      {creating && (
        <CreateJobForm
          onCancel={() => setCreating(false)}
          onCreated={() => {
            setCreating(false)
            void load()
          }}
        />
      )}

      {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}

      <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-50 text-slate-500">
            <tr>
              <th className="px-3 py-2">Name</th>
              <th className="px-3 py-2">Template</th>
              <th className="px-3 py-2">Type</th>
              <th className="px-3 py-2">Run at</th>
              <th className="px-3 py-2">Team</th>
              <th className="px-3 py-2">Status</th>
            </tr>
          </thead>
          <tbody>
            {shown.length === 0 && (
              <tr>
                <td colSpan={6} className="px-3 py-6 text-center text-slate-500">
                  No jobs match.
                </td>
              </tr>
            )}
            {shown.map((j) => (
              <tr
                key={j.id}
                onClick={() => setSelectedId(j.id)}
                className="cursor-pointer border-t border-slate-100 hover:bg-slate-50"
              >
                <td className="px-3 py-2 font-medium text-slate-800">{j.name}</td>
                <td className="px-3 py-2">{j.templateName}</td>
                <td className="px-3 py-2">{j.scheduleType === 'Manual' ? 'Manual kickoff' : j.scheduleType}</td>
                <td className="px-3 py-2">{j.recurrenceText ?? (j.scheduleType === 'EventBased' ? 'After another job' : j.scheduleType === 'TriggerBased' ? 'On webhook' : formatIst(j.runAtIst))}</td>
                <td className="px-3 py-2">{j.team}</td>
                <td className="px-3 py-2">
                  <StatusBadge status={j.status} />
                  <AtRiskBadge job={j} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {selected && (
        <section className="space-y-3 rounded-lg border border-slate-200 bg-white p-4 text-sm text-slate-600">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 className="font-medium text-slate-800">{selected.name}</h3>
            <div className="flex flex-wrap gap-2">
              {selected.scheduleType === 'Manual' && selected.status === 'Scheduled' && (
                <button
                  disabled={busy}
                  onClick={() => act(`/jobs/${selected.id}/run`, { 'Idempotency-Key': crypto.randomUUID() })}
                  className="rounded bg-indigo-600 px-2 py-1 font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
                >
                  Run now
                </button>
              )}
              {selected.status === 'Failed' && canRetry && (
                <button
                  disabled={busy}
                  onClick={() => act(`/jobs/${selected.id}/retry`)}
                  className="rounded bg-amber-600 px-2 py-1 font-medium text-white hover:bg-amber-700 disabled:opacity-50"
                >
                  Retry
                </button>
              )}
              <button
                disabled={busy}
                onClick={() =>
                  void downloadFile(`/jobs/${selected.id}/runs/export`, `${selected.name}-runs.csv`, token).catch((e: unknown) =>
                    setError(e instanceof Error ? e.message : 'Export failed'),
                  )
                }
                className="rounded border border-slate-300 px-2 py-1 hover:bg-slate-100 disabled:opacity-50"
              >
                Export CSV
              </button>
              <button
                disabled={busy}
                onClick={() =>
                  void downloadFile(`/jobs/${selected.id}/runs/export?format=pdf`, `${selected.name}-runs.pdf`, token).catch((e: unknown) =>
                    setError(e instanceof Error ? e.message : 'Export failed'),
                  )
                }
                className="rounded border border-slate-300 px-2 py-1 hover:bg-slate-100 disabled:opacity-50"
              >
                Export PDF
              </button>
              {selected.canCancel && (
                <button
                  disabled={busy}
                  onClick={() => act(`/jobs/${selected.id}/cancel`)}
                  className="rounded border border-red-300 px-2 py-1 text-red-700 hover:bg-red-50 disabled:opacity-50"
                >
                  Cancel job
                </button>
              )}
              <button onClick={() => setSelectedId(null)} className="rounded border border-slate-300 px-2 py-1 hover:bg-slate-100">
                Close
              </button>
            </div>
          </div>
          {selected.isAtRisk && (
            <p className="rounded bg-orange-50 px-3 py-2 text-orange-800">At risk: {selected.atRiskReason}</p>
          )}
          <p>
            {selected.templateName} · {selected.status} · retries {selected.retryPolicy.maxAutoRetries} ×{' '}
            {selected.retryPolicy.backoffSeconds}s (exponential)
          </p>
          <dl className="grid grid-cols-[10rem_1fr] gap-y-1">
            {Object.entries(selected.config).map(([k, v]) => (
              <div key={k} className="contents">
                <dt className="text-slate-500">{k}</dt>
                <dd>{v}</dd>
              </div>
            ))}
          </dl>
          {selected.scheduleType === 'TriggerBased' && (
            <p className="break-all rounded bg-slate-900 px-3 py-2 font-mono text-xs text-slate-100">
              curl -X POST {webhookUrl(selected.id)} -H "X-Webhook-Token: &lt;token shown at creation&gt;" -H "Idempotency-Key: &lt;unique-id&gt;"
            </p>
          )}
          {selected.requiresApproval && (
            <div>
              <h4 className="mb-2 font-medium text-slate-800">Approvals</h4>
              <ApprovalHistory jobId={selected.id} status={selected.status} refreshKey={runsRefresh} />
            </div>
          )}
          <div>
            <h4 className="mb-2 font-medium text-slate-800">Run history</h4>
            <RunHistory jobId={selected.id} status={selected.status} refreshKey={runsRefresh} />
          </div>
        </section>
      )}
    </div>
  )
}
