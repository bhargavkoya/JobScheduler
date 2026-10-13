export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5031'

export type Role = 'Employee' | 'Admin'
export type Team = 'Business' | 'Technical'

export interface UserProfile {
  id: string
  email: string
  role: Role
  primaryTeam: Team
  observerTeams: Team[]
  permissions: string[]
}

export interface AuthResult {
  token: string
  expiresAtUtc: string
  user: UserProfile
}

export type ScheduleType = 'Fixed' | 'Recurrent' | 'EventBased' | 'TriggerBased' | 'Manual'
export type JobStatus = 'Scheduled' | 'InProgress' | 'Completed' | 'Cancelled' | 'Failed' | 'NeedsManualAction'
export type FieldType = 'String' | 'Text' | 'Number' | 'Email'

export interface TemplateField {
  name: string
  label: string
  type: FieldType
  required: boolean
}

export interface RetryPolicy {
  maxAutoRetries: number
  backoffSeconds: number
}

export interface Template {
  id: string
  name: string
  description: string
  supportedScheduleTypes: ScheduleType[]
  fields: TemplateField[]
  defaultRetryPolicy: RetryPolicy
  isApproved: boolean
  requiresApproval: boolean
  /** Jobs of this template compare uploaded CSV files record by record. */
  isFileReconciliation: boolean
}

export interface Job {
  id: string
  templateId: string
  templateName: string
  name: string
  scheduleType: ScheduleType
  runAtUtc: string | null
  runAtIst: string | null
  status: JobStatus
  team: Team
  ownerId: string
  retryPolicy: RetryPolicy
  config: Record<string, string>
  createdAtUtc: string
  canCancel: boolean
  requiresApproval: boolean
  approverUserId: string | null
  statusChangedAtUtc: string
  isAtRisk: boolean
  atRiskReason: string | null
  recurrenceText: string | null
  triggerJobId: string | null
  /** TriggerBased jobs: only present on the create response; shown once. */
  webhookToken?: string | null
  /** The job compares uploaded CSV files; its runs have record-level results. */
  isFileReconciliation: boolean
}

export interface ManualActionItem {
  job: Job
  approverEmail: string | null
  canDecide: boolean
}

export interface Approval {
  id: string
  jobId: string
  runId: string | null
  approverUserId: string
  approverEmail: string | null
  decision: 'Approved' | 'Rejected'
  comment: string
  decidedAtUtc: string
}

export type RunStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed'
export type PipelineStep = 'DownloadReport' | 'Calculate' | 'SendEmail'

export interface RunStep {
  step: PipelineStep
  status: 'Succeeded' | 'Failed'
  attempt: number
  output: string
  startedAtUtc: string
  finishedAtUtc: string
}

export interface JobRun {
  id: string
  jobId: string
  idempotencyKey: string
  status: RunStatus
  attempt: number
  autoRetriesUsed: number
  error: string | null
  failedStep: PipelineStep | null
  createdAtUtc: string
  startedAtUtc: string | null
  finishedAtUtc: string | null
  steps: RunStep[]
  triggerPayload: string | null
}

export interface CreateJobPayload {
  templateId: string
  name: string
  scheduleType: ScheduleType
  runAtIst?: string
  config: Record<string, string>
  retryPolicy?: RetryPolicy
  recurrence?: Recurrence
  triggerJobId?: string
  reconciliation?: ReconciliationSetup
}

export type RecurrenceFrequency = 'Daily' | 'Weekly' | 'Monthly'

export interface Recurrence {
  frequency: RecurrenceFrequency
  time: string
  dayOfWeek: number | null
  dayOfMonth: number | null
}

export const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

/** Every schedule type can be created now. */
export const CREATABLE_SCHEDULE_TYPES: ScheduleType[] = ['Fixed', 'Manual', 'Recurrent', 'EventBased', 'TriggerBased']

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

/** Server timestamps are UTC; the app shows everything in IST. */
export function formatInstantIst(utc: string | null): string {
  if (!utc) return '—'
  const value = /[zZ]|[+-]\d\d:\d\d$/.test(utc) ? utc : `${utc}Z`
  return new Date(value).toLocaleString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false }) + ' IST'
}

/** Downloads an authenticated file such as CSV or PDF (a plain link cannot send the bearer token). */
export async function downloadFile(path: string, filename: string, token?: string | null): Promise<void> {
  const res = await fetch(`${API_BASE_URL}${path}`, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
  if (!res.ok) throw new ApiError(res.status, `Export failed (HTTP ${res.status})`)
  const url = URL.createObjectURL(await res.blob())
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}

export function webhookUrl(jobId: string): string {
  return `${API_BASE_URL}/webhooks/jobs/${jobId}`
}

export async function apiFetch<T>(path: string, init: RequestInit = {}, token?: string | null): Promise<T> {
  const headers = new Headers(init.headers)
  // A FormData body (file upload) sets its own multipart boundary header.
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  if (token) headers.set('Authorization', `Bearer ${token}`)

  const res = await fetch(`${API_BASE_URL}${path}`, { ...init, headers })
  if (!res.ok) {
    let message = `HTTP ${res.status}`
    try {
      const problem = await res.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      // non-JSON error body; keep the status message
    }
    throw new ApiError(res.status, message)
  }
  return (await res.json()) as T
}

export type NotificationEvent = 'JobCompleted' | 'JobFailed' | 'ApprovalRequested' | 'FollowUpReminder'

export interface NotificationPreference {
  event: NotificationEvent
  enabled: boolean
}

// ---- file reconciliation ----

export type ComparisonOperator = 'Equal' | 'NotEqual' | 'LessThan' | 'LessThanOrEqual' | 'GreaterThan' | 'GreaterThanOrEqual'
export type ToleranceMode = 'Absolute' | 'Percent'
export type RecordStatus = 'Pass' | 'Fail'

/** A CSV the user uploaded: its columns come from the file's own header row. */
export interface InputFile {
  id: string
  alias: string
  fileName: string
  headers: string[]
  rowCount: number
  sizeBytes: number
  uploadedAtUtc: string
  /** First rows, only returned by the upload call. */
  preview: string[][]
}

export interface ReconciliationRule {
  id?: string | null
  name: string
  leftAlias: string
  rightAlias: string
  leftKeyColumn: string | null
  rightKeyColumn: string | null
  leftExpression: string
  operator: ComparisonOperator
  rightExpression: string
  tolerance: number
  toleranceMode: ToleranceMode
}

export interface ReconciliationSetup {
  files: { fileId: string; alias: string }[]
  rules: ReconciliationRule[]
}

export interface ReconciliationSetupView {
  files: InputFile[]
  rules: ReconciliationRule[]
}

export interface RuleSummary {
  ruleId: string
  ruleName: string
  description: string
  total: number
  passed: number
  failed: number
}

export interface ReconciliationSummary {
  runId: string
  jobId: string
  jobName: string
  runStatus: RunStatus
  finishedAtUtc: string | null
  total: number
  passed: number
  failed: number
  rules: RuleSummary[]
}

export interface ReconciliationRecord {
  id: number
  ruleId: string
  ruleName: string
  recordKey: string
  leftRow: number | null
  rightRow: number | null
  leftValue: number | null
  rightValue: number | null
  difference: number | null
  status: RecordStatus
  reason: string
}

export interface ReconciliationPage {
  items: ReconciliationRecord[]
  total: number
  page: number
  pageSize: number
}

/** Uploads CSV files. They are parsed on arrival, so the response already lists each file's real columns. */
export function uploadJobFiles(files: File[], token?: string | null): Promise<InputFile[]> {
  const body = new FormData()
  for (const f of files) body.append('files', f, f.name)
  return apiFetch<InputFile[]>('/job-files', { method: 'POST', body }, token)
}
