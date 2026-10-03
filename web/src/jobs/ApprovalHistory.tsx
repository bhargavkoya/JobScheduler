import { useEffect, useState } from 'react'
import { apiFetch, formatInstantIst, type Approval } from '../api'
import { useAuth } from '../auth/AuthContext'

/** Single-approver decisions recorded for a job (audit trail). */
export function ApprovalHistory({ jobId, status, refreshKey }: { jobId: string; status: string; refreshKey: number }) {
  const { token } = useAuth()
  const [items, setItems] = useState<Approval[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    apiFetch<Approval[]>(`/jobs/${jobId}/approvals`, { signal: controller.signal }, token)
      .then((list) => {
        setError(null)
        setItems(list)
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Failed to load approvals')
      })
    return () => controller.abort()
  }, [jobId, token, status, refreshKey])

  if (error) return <p role="alert" className="rounded bg-red-50 px-3 py-2 text-red-700">{error}</p>
  if (items.length === 0) return <p className="text-slate-500">No decisions yet.</p>

  return (
    <ul className="space-y-1 text-xs">
      {items.map((a) => (
        <li key={a.id} className="flex flex-wrap gap-2">
          <span className={a.decision === 'Approved' ? 'font-medium text-emerald-700' : 'font-medium text-red-700'}>
            {a.decision}
          </span>
          <span className="text-slate-700">{a.approverEmail ?? a.approverUserId}</span>
          <span className="text-slate-400">{formatInstantIst(a.decidedAtUtc)}</span>
          {a.comment && <span className="text-slate-500">“{a.comment}”</span>}
        </li>
      ))}
    </ul>
  )
}
