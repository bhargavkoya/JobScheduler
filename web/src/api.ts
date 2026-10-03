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

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
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
