import { create } from 'zustand'
import type { AuthUser } from '../lib/api'

type AuthState = {
  token: string | null
  user: AuthUser | null
  setSession: (token: string, user: AuthUser) => void
  clearSession: () => void
  hydrate: () => void
}

export const useAuthStore = create<AuthState>((set) => ({
  token: null,
  user: null,
  setSession: (token, user) => {
    if (typeof window !== 'undefined') {
      localStorage.setItem('azselfservice_token', token)
      localStorage.setItem('azselfservice_user', JSON.stringify(user))
    }
    set({ token, user })
  },
  clearSession: () => {
    if (typeof window !== 'undefined') {
      localStorage.removeItem('azselfservice_token')
      localStorage.removeItem('azselfservice_user')
    }
    set({ token: null, user: null })
  },
  hydrate: () => {
    if (typeof window === 'undefined') {
      return
    }

    const token = localStorage.getItem('azselfservice_token')
    const userRaw = localStorage.getItem('azselfservice_user')
    const user = userRaw ? (JSON.parse(userRaw) as AuthUser) : null

    set({ token, user })
  }
}))