import Link from 'next/link'
import { useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import { getModules, type ModuleSummary } from '../lib/api'
import { useAuthStore } from '../store/auth'

export default function DashboardPage() {
  const router = useRouter()
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)
  const user = useAuthStore((state) => state.user)
  const clearSession = useAuthStore((state) => state.clearSession)

  const [modules, setModules] = useState<ModuleSummary[]>([])

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (!token) {
      router.replace('/login')
      return
    }

    getModules().then(setModules).catch(() => setModules([]))
  }, [token, router])

  return (
    <main style={{ maxWidth: 960, margin: '2rem auto', padding: '0 1rem' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h1 style={{ marginBottom: 0 }}>Dashboard</h1>
          <p style={{ marginTop: 4, color: '#555' }}>
            Signed in as <strong>{user?.username || 'unknown'}</strong>
          </p>
        </div>
        <button
          onClick={() => {
            clearSession()
            router.push('/login')
          }}
        >
          Sign out
        </button>
      </header>

      <section style={{ marginTop: 24, padding: 16, border: '1px solid #ddd', borderRadius: 8 }}>
        <h2 style={{ marginTop: 0 }}>Available Modules</h2>
        <p>{modules.length} published modules are available for deployment.</p>
        <Link href="/modules">Go to Module Catalog</Link>
        {user?.role?.toLowerCase() === 'admin' && (
          <div style={{ marginTop: 8 }}>
            <Link href="/admin/modules">Go to Admin Modules</Link>
          </div>
        )}
      </section>
    </main>
  )
}