import { useCallback, useEffect, useState } from 'react'
import { apiFetch, type Template } from '../api'
import { useAuth } from '../auth/AuthContext'

export function CatalogPage() {
  const { token, user } = useAuth()
  const isAdmin = user?.role === 'Admin'
  const [templates, setTemplates] = useState<Template[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)

  const load = useCallback(
    (signal?: AbortSignal) =>
      apiFetch<Template[]>(`/templates?includeUnapproved=${isAdmin}`, { signal }, token)
        .then(setTemplates)
        .catch((err: unknown) => {
          if (signal?.aborted) return
          setError(err instanceof Error ? err.message : 'Failed to load templates')
        }),
    [token, isAdmin],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])

  async function approve(id: string) {
    setBusyId(id)
    setError(null)
    try {
      await apiFetch<Template>(`/admin/templates/${id}/approve`, { method: 'POST' }, token)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Approval failed')
    } finally {
      setBusyId(null)
    }
  }

  return (
    <div className="space-y-3">
      {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}
      {templates.map((t) => (
        <article key={t.id} className="rounded-lg border border-slate-200 bg-white p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 className="font-medium text-slate-800">{t.name}</h3>
            <div className="flex items-center gap-2">
              <span
                className={
                  'rounded px-2 py-0.5 text-xs ' +
                  (t.isApproved ? 'bg-emerald-50 text-emerald-700' : 'bg-amber-50 text-amber-700')
                }
              >
                {t.isApproved ? 'Approved' : 'Awaiting approval'}
              </span>
              {isAdmin && !t.isApproved && (
                <button
                  onClick={() => approve(t.id)}
                  disabled={busyId === t.id}
                  className="rounded bg-indigo-600 px-2 py-1 text-xs font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
                >
                  Approve
                </button>
              )}
            </div>
          </div>
          <p className="mt-1 text-sm text-slate-600">{t.description}</p>
          <p className="mt-2 text-xs text-slate-500">
            Schedules: {t.supportedScheduleTypes.join(', ')} · Fields:{' '}
            {t.fields.map((f) => `${f.label}${f.required ? '*' : ''}`).join(', ') || 'none'} · Retries:{' '}
            {t.defaultRetryPolicy.maxAutoRetries} × {t.defaultRetryPolicy.backoffSeconds}s
          </p>
        </article>
      ))}
    </div>
  )
}
