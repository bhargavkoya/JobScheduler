import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { ApiError, apiFetch, type AuthResult, type UserProfile } from '../api'

const TOKEN_KEY = 'jobscheduler.token'

interface AuthState {
  user: UserProfile | null
  token: string | null
  loading: boolean
  login: (email: string, password: string) => Promise<void>
  register: (email: string, password: string) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthState | null>(null)

function readStoredToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY)
  } catch {
    return null
  }
}

function storeToken(token: string | null) {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token)
    else localStorage.removeItem(TOKEN_KEY)
  } catch {
    // storage unavailable; session just won't survive a reload
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(readStoredToken)
  const [user, setUser] = useState<UserProfile | null>(null)
  const [loading, setLoading] = useState<boolean>(() => readStoredToken() !== null)

  const logout = useCallback(() => {
    storeToken(null)
    setToken(null)
    setUser(null)
  }, [])

  // Restore the session: validate a stored token against /auth/me.
  useEffect(() => {
    if (!token || user) return
    const controller = new AbortController()
    apiFetch<UserProfile>('/auth/me', { signal: controller.signal }, token)
      .then(setUser)
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        if (err instanceof ApiError && err.status === 401) logout()
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [token, user, logout])

  const applyResult = useCallback((result: AuthResult) => {
    storeToken(result.token)
    setToken(result.token)
    setUser(result.user)
    setLoading(false)
  }, [])

  const login = useCallback(
    async (email: string, password: string) => {
      applyResult(await apiFetch<AuthResult>('/auth/login', { method: 'POST', body: JSON.stringify({ email, password }) }))
    },
    [applyResult],
  )

  const register = useCallback(
    async (email: string, password: string) => {
      applyResult(
        await apiFetch<AuthResult>('/auth/register', { method: 'POST', body: JSON.stringify({ email, password }) }),
      )
    },
    [applyResult],
  )

  const value = useMemo(
    () => ({ user, token, loading, login, register, logout }),
    [user, token, loading, login, register, logout],
  )
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
