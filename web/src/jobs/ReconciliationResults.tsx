import { useEffect, useState } from 'react'
import {
  apiFetch,
  downloadFile,
  type ReconciliationPage,
  type ReconciliationSummary,
  type RecordStatus,
} from '../api'
import { useAuth } from '../auth/AuthContext'
import { formatAmount } from './reconciliation'

const PAGE_SIZE = 50

/** Record-by-record results of one run: totals per rule, a filterable table, and CSV/PDF export of the same view. */
export function ReconciliationResults({ runId, jobName }: { runId: string; jobName: string }) {
  const { token } = useAuth()
  const [summary, setSummary] = useState<ReconciliationSummary | null>(null)
  // Rows are stored with the filter they were fetched for, so "loading" is simply: what we show is not what is selected.
  const [loaded, setLoaded] = useState<{ key: string; data: ReconciliationPage } | null>(null)
  const [status, setStatus] = useState<'' | RecordStatus>('')
  const [ruleId, setRuleId] = useState('')
  const [page, setPage] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    apiFetch<ReconciliationSummary>(`/runs/${runId}/reconciliation/summary`, { signal: controller.signal }, token)
      .then((s) => {
        setError(null)
        setSummary(s)
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Failed to load the summary')
      })
    return () => controller.abort()
  }, [runId, token])

  const viewKey = `${runId}|${status}|${ruleId}|${page}`
  const pageData = loaded?.data ?? null
  const loading = loaded?.key !== viewKey

  useEffect(() => {
    const controller = new AbortController()
    const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) })
    if (status) params.set('status', status)
    if (ruleId) params.set('ruleId', ruleId)
    apiFetch<ReconciliationPage>(`/runs/${runId}/reconciliation/results?${params}`, { signal: controller.signal }, token)
      .then((p) => {
        setError(null)
        setLoaded({ key: viewKey, data: p })
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        setError(err instanceof Error ? err.message : 'Failed to load the results')
      })
    return () => controller.abort()
  }, [runId, token, status, ruleId, page, viewKey])

  function filterQuery(format: 'csv' | 'pdf') {
    const params = new URLSearchParams({ format })
    if (status) params.set('status', status)
    if (ruleId) params.set('ruleId', ruleId)
    return params
  }

  async function exportAs(format: 'csv' | 'pdf') {
    setExporting(true)
    setError(null)
    try {
      const rule = summary?.rules.find((r) => r.ruleId === ruleId)
      const suffix = [rule?.ruleName, status.toLowerCase()].filter(Boolean).join('-')
      await downloadFile(
        `/runs/${runId}/reconciliation/export?${filterQuery(format)}`,
        `${jobName}-results${suffix ? `-${suffix}` : ''}.${format}`,
        token,
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Export failed')
    } finally {
      setExporting(false)
    }
  }

  function choose(nextStatus: '' | RecordStatus, nextRule: string) {
    setStatus(nextStatus)
    setRuleId(nextRule)
    setPage(1)
  }

  if (error && !summary) return <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  if (!summary) return <p className="text-sm text-slate-500">Loading results…</p>
  if (summary.total === 0)
    return <p className="text-sm text-slate-500">No record results yet{summary.runStatus === 'Succeeded' ? ' for this run.' : ': the run has not finished.'}</p>

  const totalPages = pageData ? Math.max(1, Math.ceil(pageData.total / PAGE_SIZE)) : 1
  const chip = (active: boolean) =>
    'rounded border px-2 py-1 text-xs ' + (active ? 'border-indigo-400 bg-indigo-50 text-indigo-800' : 'border-slate-200 bg-white text-slate-600 hover:bg-slate-50')

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-2" aria-label="Reconciliation totals">
        <Stat label="Record checks" value={summary.total} tone="text-slate-800" />
        <Stat label="Passed" value={summary.passed} tone="text-emerald-700" />
        <Stat label="Failed" value={summary.failed} tone={summary.failed > 0 ? 'text-red-700' : 'text-slate-500'} />
      </div>

      <div className="space-y-1">
        {summary.rules.map((r) => (
          <button
            key={r.ruleId}
            type="button"
            onClick={() => choose(status, ruleId === r.ruleId ? '' : r.ruleId)}
            className={'block w-full rounded border px-3 py-2 text-left text-xs ' + (ruleId === r.ruleId ? 'border-indigo-400 bg-indigo-50' : 'border-slate-200 bg-white hover:bg-slate-50')}
            title="Show only this rule's records"
          >
            <span className="font-medium text-slate-800">{r.ruleName}</span>
            <span className="ml-2 text-emerald-700">{r.passed} passed</span>
            <span className={'ml-2 ' + (r.failed > 0 ? 'text-red-700' : 'text-slate-400')}>{r.failed} failed</span>
            <span className="ml-2 text-slate-400">of {r.total}</span>
            <span className="mt-0.5 block truncate font-mono text-slate-500" title={r.description}>{r.description}</span>
          </button>
        ))}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <button type="button" onClick={() => choose('', ruleId)} className={chip(status === '')}>All</button>
        <button type="button" onClick={() => choose('Fail', ruleId)} className={chip(status === 'Fail')}>Failed only</button>
        <button type="button" onClick={() => choose('Pass', ruleId)} className={chip(status === 'Pass')}>Passed only</button>
        <span className="ml-auto flex gap-2">
          <button type="button" disabled={exporting} onClick={() => void exportAs('csv')} className="rounded border border-slate-300 bg-white px-2 py-1 text-xs hover:bg-slate-50 disabled:opacity-50">
            Export CSV
          </button>
          <button type="button" disabled={exporting} onClick={() => void exportAs('pdf')} className="rounded border border-slate-300 bg-white px-2 py-1 text-xs hover:bg-slate-50 disabled:opacity-50">
            Export PDF
          </button>
        </span>
      </div>
      <p className="text-xs text-slate-500">Exports contain every record matching the filters above, not just this page.</p>

      {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}

      <div className={'overflow-x-auto rounded border border-slate-200 transition-opacity ' + (loading ? 'opacity-50' : '')} aria-busy={loading}>
        <table aria-label="Reconciliation records" className="w-full text-left text-xs">
          <thead className="bg-slate-50 text-slate-500">
            <tr>
              <th className="px-2 py-1.5">Rule</th>
              <th className="px-2 py-1.5">Record</th>
              <th className="px-2 py-1.5 text-right">Left</th>
              <th className="px-2 py-1.5 text-right">Right</th>
              <th className="px-2 py-1.5 text-right">Difference</th>
              <th className="px-2 py-1.5">Status</th>
              <th className="px-2 py-1.5">Reason</th>
            </tr>
          </thead>
          <tbody>
            {pageData?.items.length === 0 && (
              <tr>
                <td colSpan={7} className="px-2 py-4 text-center text-slate-500">No records match.</td>
              </tr>
            )}
            {pageData?.items.map((r) => (
              <tr key={r.id} className={'border-t border-slate-100 ' + (r.status === 'Fail' ? 'bg-red-50/40' : '')}>
                <td className="px-2 py-1.5 text-slate-600">{r.ruleName}</td>
                <td className="px-2 py-1.5">
                  <span className="font-medium text-slate-800">{r.recordKey}</span>
                  <span className="ml-1 text-slate-400">
                    {[r.leftRow !== null ? `L${r.leftRow}` : null, r.rightRow !== null ? `R${r.rightRow}` : null].filter(Boolean).join(' ')}
                  </span>
                </td>
                <td className="px-2 py-1.5 text-right font-mono">{formatAmount(r.leftValue)}</td>
                <td className="px-2 py-1.5 text-right font-mono">{formatAmount(r.rightValue)}</td>
                <td className="px-2 py-1.5 text-right font-mono">{formatAmount(r.difference)}</td>
                <td className="px-2 py-1.5">
                  <span className={'rounded px-1.5 py-0.5 font-medium ' + (r.status === 'Pass' ? 'bg-emerald-50 text-emerald-700' : 'bg-red-50 text-red-700')}>
                    {r.status}
                  </span>
                </td>
                <td className="px-2 py-1.5 text-slate-600">{r.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {pageData && pageData.total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-xs text-slate-600">
          <span>
            {(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, pageData.total)} of {pageData.total.toLocaleString()}
          </span>
          <span className="flex gap-2">
            <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)} className="rounded border border-slate-300 px-2 py-1 hover:bg-slate-50 disabled:opacity-50">
              Previous
            </button>
            <span className="py-1">Page {page} of {totalPages}</span>
            <button type="button" disabled={page >= totalPages} onClick={() => setPage(page + 1)} className="rounded border border-slate-300 px-2 py-1 hover:bg-slate-50 disabled:opacity-50">
              Next
            </button>
          </span>
        </div>
      )}
    </div>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone: string }) {
  return (
    <div className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs">
      <div className={'text-lg font-semibold ' + tone}>{value.toLocaleString()}</div>
      <div className="text-slate-500">{label}</div>
    </div>
  )
}
