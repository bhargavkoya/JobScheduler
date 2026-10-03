import type { ReactNode } from 'react'
import { useAuth } from './AuthContext'
import { LoginPage } from './LoginPage'

/** Renders children only for a signed-in user; otherwise shows the login page. */
export function ProtectedRoute({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth()

  if (loading) {
    return <main className="flex min-h-screen items-center justify-center text-slate-500">Loading...</main>
  }
  if (!user) return <LoginPage />
  return <>{children}</>
}
