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
}

export interface CreateJobPayload {
  templateId: string
  name: string
  scheduleType: ScheduleType
  runAtIst?: string
  config: Record<string, string>
  retryPolicy?: RetryPolicy
}

/** Only these can be created so far; the rest arrive in later phases. */
export const CREATABLE_SCHEDULE_TYPES: ScheduleType[] = ['Fixed', 'Manual']

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

export async function apiFetch<T>(path: string, init: RequestInit = {}, token?: string | null): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body) headers.set('Content-Type', 'application/json')
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
