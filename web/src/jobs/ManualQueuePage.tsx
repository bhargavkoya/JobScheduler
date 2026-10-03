import { useCallback, useEffect, useState } from 'react'
import { apiFetch, formatInstantIst, type ManualActionItem } from '../api'
import { useAuth } from '../auth/AuthContext'
import { AtRiskBadge } from './StatusBadge'

/** Every job currently waiting on a human. Approve / Reject are only enabled for the named approver (or an admin). */
export function ManualQueuePage({ onChanged }: { onChanged: () => void }) {
  const { token } = useAuth()
  const [items, setItems] = useState<ManualActionItem[]>([])
  const [comments, setComments] = useState<Record<string, string>>({})
  const [busyId, setBusyId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)

  const load = useCallback(
    (signal?: AbortSignal) =>
      apiFetch<ManualActionItem[]>('/jobs/manual-queue', { signal }, token)
        .then((list) => {
          setError(null)
          setItems(list)
          setLoaded(true)
        })
        .catch((err: unknown) => {
          if (!signal?.aborted) setError(err instanceof Error ? err.message : 'Failed to load the queue')
        }),
    [token],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    const timer = setInterval(() => void load(), 5000)
    return () => {
      controller.abort()
      clearInterval(timer)
    }
  }, [load])

  async function decide(id: string, decision: 'approve' | 'reject') {
    const comment = (comments[id] ?? '').trim()
    if (decision === 'reject' && !comment) {
      setError('A comment is required when rejecting.')
      return
    }
    setError(null)
    setBusyId(id)
    try {
      await apiFetch(`/jobs/${id}/${decision}`, { method: 'POST', body: JSON.stringify({ comment }) }, token)
      setComments((c) => ({ ...c, [id]: '' }))
      await load()
      onChanged()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Action failed')
      await load() // e.g. someone else already decided: show the current queue
    } finally {
      setBusyId(null)
    }
  }

  return (
    <div className="space-y-4">
      <div>
        <h2 className="font-medium text-slate-800">Manual action queue</h2>
        <p className="text-sm text-slate-500">Jobs waiting on a human decision, oldest first.</p>
      </div>

      {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}

      {loaded && items.length === 0 && (
        <p className="rounded-lg border border-slate-200 bg-white px-4 py-6 text-center text-sm text-slate-500">
          Nothing is waiting on anyone.
        </p>
      )}

      {items.map(({ job, approverEmail, canDecide }) => (
        <section key={job.id} className="space-y-3 rounded-lg border border-slate-200 bg-white p-4 text-sm text-slate-600">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 className="font-medium text-slate-800">
              {job.name}
              <AtRiskBadge job={job} />
            </h3>
            <span className="text-xs text-slate-500">
              {job.templateName} · {job.team} · waiting since {formatInstantIst(job.statusChangedAtUtc)}
            </span>
          </div>
          <p>
            Approver: <span className="font-medium text-slate-800">{approverEmail ?? 'unassigned'}</span>
            {job.atRiskReason && <span className="ml-2 text-orange-700">{job.atRiskReason}</span>}
          </p>
          <dl className="grid grid-cols-[10rem_1fr] gap-y-1">
            {Object.entries(job.config).map(([k, v]) => (
              <div key={k} className="contents">
                <dt className="text-slate-500">{k}</dt>
                <dd>{v}</dd>
              </div>
            ))}
          </dl>
          <div className="flex flex-wrap items-center gap-2">
            <input
              aria-label={`Comment for ${job.name}`}
              placeholder="Comment (required to reject)"
              value={comments[job.id] ?? ''}
              disabled={!canDecide}
              onChange={(e) => setComments((c) => ({ ...c, [job.id]: e.target.value }))}
              className="min-w-0 flex-1 rounded border border-slate-300 px-2 py-1 disabled:bg-slate-50"
            />
            <button
              disabled={!canDecide || busyId === job.id}
              onClick={() => void decide(job.id, 'approve')}
              className="rounded bg-emerald-600 px-3 py-1 font-medium text-white hover:bg-emerald-700 disabled:opacity-40"
            >
              Approve
            </button>
            <button
              disabled={!canDecide || busyId === job.id}
              onClick={() => void decide(job.id, 'reject')}
              className="rounded border border-red-300 px-3 py-1 font-medium text-red-700 hover:bg-red-50 disabled:opacity-40"
            >
              Reject
            </button>
          </div>
          {!canDecide && <p className="text-xs text-slate-400">Only the named approver (or an admin) can decide.</p>}
        </section>
      ))}
    </div>
  )
}
