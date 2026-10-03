import type { JobStatus } from '../api'

export const STATUS_LABELS: Record<JobStatus, string> = {
  Scheduled: 'Scheduled',
  InProgress: 'In progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  Failed: 'Failed',
  NeedsManualAction: 'Needs manual action',
}
