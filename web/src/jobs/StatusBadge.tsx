import type { Job, JobStatus } from '../api'
import { STATUS_LABELS } from './statusLabels'

const STATUS_STYLES: Record<JobStatus, string> = {
  Scheduled: 'bg-slate-100 text-slate-700',
  InProgress: 'bg-sky-50 text-sky-700',
  Completed: 'bg-emerald-50 text-emerald-700',
  Cancelled: 'bg-slate-100 text-slate-500',
  Failed: 'bg-red-50 text-red-700',
  NeedsManualAction: 'bg-amber-50 text-amber-800',
}

export function StatusBadge({ status }: { status: JobStatus }) {
  return <span className={'rounded px-2 py-0.5 text-xs font-medium ' + STATUS_STYLES[status]}>{STATUS_LABELS[status]}</span>
}

/** Shown next to the status when the job is late, stuck, or waiting too long. The reason is the tooltip. */
export function AtRiskBadge({ job }: { job: Pick<Job, 'isAtRisk' | 'atRiskReason'> }) {
  if (!job.isAtRisk) return null
  return (
    <span
      title={job.atRiskReason ?? 'At risk'}
      className="ml-1 rounded border border-orange-300 bg-orange-50 px-1.5 py-0.5 text-xs font-medium text-orange-700"
    >
      At risk
    </span>
  )
}
