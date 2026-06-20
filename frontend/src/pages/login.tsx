import { FormEvent, useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import { login } from '../lib/api'
import { useAuthStore } from '../store/auth'

function getErrorMessage(err: unknown, fallback: string): string {
  if (typeof err === 'object' && err !== null && 'response' in err) {
    const response = (err as { response?: { data?: { message?: string } } }).response
    if (typeof response?.data?.message === 'string' && response.data.message.length > 0) {
      return response.data.message
    }
  }
  return fallback
}

export default function LoginPage() {
  const router = useRouter()
  const setSession = useAuthStore((state) => state.setSession)
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)

  const [username, setUsername] = useState('admin')
  const [password, setPassword] = useState('Test@1234')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (token) {
      router.replace('/dashboard')
    }
  }, [token, router])

  const onSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setIsSubmitting(true)
    setError(null)

    try {
      const response = await login(username, password)
      setSession(response.token, response.user)
      router.push('/dashboard')
    } catch (err: unknown) {
      setError(getErrorMessage(err, 'Login failed.'))
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <main style={{ maxWidth: 420, margin: '4rem auto', padding: '1.5rem', border: '1px solid #ddd', borderRadius: 8 }}>
      <h1 style={{ marginTop: 0 }}>AzSelfService Login</h1>
      <p style={{ color: '#555' }}>Use your tenant-scoped credentials to access modules and deployments.</p>

      <form onSubmit={onSubmit} style={{ display: 'grid', gap: 12 }}>
        <label>
          Username
          <input
            style={{ width: '100%', padding: 8, marginTop: 4 }}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            required
          />
        </label>

        <label>
          Password
          <input
            style={{ width: '100%', padding: 8, marginTop: 4 }}
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
        </label>

        {error && <div style={{ color: '#b91c1c' }}>{error}</div>}

        <button type="submit" disabled={isSubmitting} style={{ padding: 10 }}>
          {isSubmitting ? 'Signing in...' : 'Sign in'}
        </button>
      </form>
    </main>
  )
}