import { useEffect, useState, type FormEvent } from 'react'
import {
  apiFetch,
  CREATABLE_SCHEDULE_TYPES,
  type CreateJobPayload,
  type Job,
  type ScheduleType,
  type Template,
} from '../api'
import { useAuth } from '../auth/AuthContext'

export function CreateJobForm({ onCreated, onCancel }: { onCreated: () => void; onCancel: () => void }) {
  const { token } = useAuth()
  const [templates, setTemplates] = useState<Template[]>([])
  const [templateId, setTemplateId] = useState('')
  const [scheduleType, setScheduleType] = useState<ScheduleType>('Manual')
  const [name, setName] = useState('')
  const [runAtIst, setRunAtIst] = useState('')
  const [config, setConfig] = useState<Record<string, string>>({})
  const [retries, setRetries] = useState('')
  const [backoff, setBackoff] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    apiFetch<Template[]>('/templates', { signal: controller.signal }, token)
      .then((list) => {
        setTemplates(list)
        if (list.length > 0) selectTemplate(list[0])
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Failed to load templates')
      })
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token])

  const template = templates.find((t) => t.id === templateId)
  const allowedTypes = template?.supportedScheduleTypes.filter((s) => CREATABLE_SCHEDULE_TYPES.includes(s)) ?? []

  function selectTemplate(t: Template) {
    setTemplateId(t.id)
    setConfig({})
    const first = t.supportedScheduleTypes.find((s) => CREATABLE_SCHEDULE_TYPES.includes(s))
    if (first) setScheduleType(first)
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      const payload: CreateJobPayload = {
        templateId,
        name,
        scheduleType,
        config,
        ...(retries !== '' || backoff !== ''
          ? {
              retryPolicy: {
                maxAutoRetries: Number(retries === '' ? template?.defaultRetryPolicy.maxAutoRetries : retries),
                backoffSeconds: Number(backoff === '' ? template?.defaultRetryPolicy.backoffSeconds : backoff),
              },
            }
          : {}),
        ...(scheduleType === 'Fixed' ? { runAtIst: runAtIst.length === 16 ? `${runAtIst}:00` : runAtIst } : {}),
      }
      await apiFetch<Job>('/jobs', { method: 'POST', body: JSON.stringify(payload) }, token)
      onCreated()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create job')
    } finally {
      setBusy(false)
    }
  }

  const input = 'mt-1 w-full rounded border border-slate-300 px-3 py-2 text-slate-900'

  return (
    <form onSubmit={onSubmit} className="space-y-3 rounded-lg border border-slate-200 bg-white p-4">
      <h3 className="font-medium text-slate-800">New job</h3>

      <label className="block text-sm text-slate-600">
        Template
        <select
          value={templateId}
          onChange={(e) => {
            const t = templates.find((x) => x.id === e.target.value)
            if (t) selectTemplate(t)
          }}
          className={input}
        >
          {templates.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </select>
      </label>

      <label className="block text-sm text-slate-600">
        Job name
        <input required value={name} onChange={(e) => setName(e.target.value)} className={input} />
      </label>

      <label className="block text-sm text-slate-600">
        Schedule type
        <select value={scheduleType} onChange={(e) => setScheduleType(e.target.value as ScheduleType)} className={input}>
          {allowedTypes.map((s) => (
            <option key={s} value={s}>
              {s === 'Manual' ? 'Manual kickoff' : s}
            </option>
          ))}
        </select>
      </label>

      {scheduleType === 'Fixed' && (
        <label className="block text-sm text-slate-600">
          Run at (IST)
          <input type="datetime-local" required value={runAtIst} onChange={(e) => setRunAtIst(e.target.value)} className={input} />
        </label>
      )}

      {template?.fields.map((f) => (
        <label key={f.name} className="block text-sm text-slate-600">
          {f.label}
          {f.required && ' *'}
          {f.type === 'Text' ? (
            <textarea
              required={f.required}
              value={config[f.name] ?? ''}
              onChange={(e) => setConfig({ ...config, [f.name]: e.target.value })}
              className={input}
            />
          ) : (
            <input
              required={f.required}
              type={f.type === 'Number' ? 'number' : f.type === 'Email' ? 'email' : 'text'}
              step={f.type === 'Number' ? 'any' : undefined}
              value={config[f.name] ?? ''}
              onChange={(e) => setConfig({ ...config, [f.name]: e.target.value })}
              className={input}
            />
          )}
        </label>
      ))}

      <div className="grid grid-cols-2 gap-3">
        <label className="block text-sm text-slate-600">
          Auto retries (blank = template default{template ? `: ${template.defaultRetryPolicy.maxAutoRetries}` : ''})
          <input type="number" min={0} max={10} value={retries} onChange={(e) => setRetries(e.target.value)} className={input} />
        </label>
        <label className="block text-sm text-slate-600">
          Backoff seconds (blank = default{template ? `: ${template.defaultRetryPolicy.backoffSeconds}` : ''})
          <input type="number" min={0} max={3600} value={backoff} onChange={(e) => setBackoff(e.target.value)} className={input} />
        </label>
      </div>

      {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}

      <div className="flex gap-2">
        <button
          type="submit"
          disabled={busy || !template}
          className="rounded bg-indigo-600 px-3 py-2 font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
        >
          {busy ? 'Creating...' : 'Create job'}
        </button>
        <button type="button" onClick={onCancel} className="rounded border border-slate-300 px-3 py-2 hover:bg-slate-100">
          Close
        </button>
      </div>
    </form>
  )
}
