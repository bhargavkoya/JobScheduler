import { useEffect, useState } from 'react'
import { apiFetch, type NotificationEvent, type NotificationPreference } from '../api'
import { useAuth } from '../auth/AuthContext'

const EVENT_LABELS: Record<NotificationEvent, { title: string; help: string }> = {
  JobCompleted: { title: 'Job completed', help: 'A job you own finished successfully, or a result email addressed to you.' },
  JobFailed: { title: 'Job failed', help: 'A job you own failed after all automatic retries.' },
  ApprovalRequested: { title: 'Approval requested', help: 'A job is waiting for your approval.' },
  FollowUpReminder: { title: 'Follow-up reminders', help: 'A chaser because a job is still waiting for your decision.' },
}

/** The signed-in user's email preferences. Everything is on until switched off. */
export function NotificationsPage() {
  const { token } = useAuth()
  const [prefs, setPrefs] = useState<NotificationPreference[]>([])
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    apiFetch<NotificationPreference[]>('/me/notification-preferences', { signal: controller.signal }, token)
      .then(setPrefs)
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Failed to load preferences')
      })
    return () => controller.abort()
  }, [token])

  function toggle(event: NotificationEvent) {
    setSaved(false)
    setPrefs((list) => list.map((p) => (p.event === event ? { ...p, enabled: !p.enabled } : p)))
  }

  async function save() {
    setBusy(true)
    setError(null)
    try {
      const result = await apiFetch<NotificationPreference[]>(
        '/me/notification-preferences',
        { method: 'PUT', body: JSON.stringify({ preferences: prefs }) },
        token,
      )
      setPrefs(result)
      setSaved(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save preferences')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="space-y-3 rounded-lg border border-slate-200 bg-white p-4">
      <h2 className="font-medium text-slate-800">Email notifications</h2>
      <p className="text-sm text-slate-500">Choose which events the scheduler may email you about.</p>

      {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}

      <ul className="divide-y divide-slate-100">
        {prefs.map((p) => (
          <li key={p.event} className="flex items-start gap-3 py-2">
            <input
              id={`pref-${p.event}`}
              type="checkbox"
              checked={p.enabled}
              onChange={() => toggle(p.event)}
              className="mt-1"
            />
            <label htmlFor={`pref-${p.event}`} className="text-sm">
              <span className="font-medium text-slate-800">{EVENT_LABELS[p.event].title}</span>
              <span className="block text-slate-500">{EVENT_LABELS[p.event].help}</span>
            </label>
          </li>
        ))}
      </ul>

      <div className="flex items-center gap-3">
        <button
          onClick={() => void save()}
          disabled={busy || prefs.length === 0}
          className="rounded bg-indigo-600 px-3 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
        >
          {busy ? 'Saving...' : 'Save'}
        </button>
        {saved && <span className="text-sm text-emerald-700">Saved.</span>}
      </div>
    </section>
  )
}
