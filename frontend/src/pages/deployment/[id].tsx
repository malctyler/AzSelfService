import Link from 'next/link'
import { useRouter } from 'next/router'
import { useEffect, useRef, useState } from 'react'
import { getDeployment, getDeploymentLogs, type DeploymentDetails, type DeploymentLog } from '../../lib/api'
import { useAuthStore } from '../../store/auth'

export default function DeploymentPage() {
  const router = useRouter()
  const deploymentId = router.query.id as string | undefined

  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)

  const [deployment, setDeployment] = useState<DeploymentDetails | null>(null)
  const [logs, setLogs] = useState<DeploymentLog[]>([])
  const [error, setError] = useState<string | null>(null)

  const latestLogIdRef = useRef<number | undefined>(undefined)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (!token) {
      router.replace('/login')
      return
    }

    if (!deploymentId) {
      return
    }

    let cancelled = false

    const fetchDeployment = async () => {
      try {
        const details = await getDeployment(deploymentId)
        if (!cancelled) {
          setDeployment(details)
        }
      } catch {
        if (!cancelled) {
          setError('Could not load deployment details.')
        }
      }
    }

    const fetchLogs = async () => {
      try {
        const newLogs = await getDeploymentLogs(deploymentId, latestLogIdRef.current)
        if (!cancelled && newLogs.length > 0) {
          latestLogIdRef.current = newLogs[newLogs.length - 1].id
          setLogs((current) => [...current, ...newLogs])
        }
      } catch {
        if (!cancelled) {
          setError('Could not load deployment logs.')
        }
      }
    }

    fetchDeployment()
    fetchLogs()

    const deploymentTimer = setInterval(fetchDeployment, 3000)
    const logsTimer = setInterval(fetchLogs, 2000)

    return () => {
      cancelled = true
      clearInterval(deploymentTimer)
      clearInterval(logsTimer)
    }
  }, [token, deploymentId, router])

  return (
    <main style={{ maxWidth: 1100, margin: '2rem auto', padding: '0 1rem' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Deployment Status</h1>
        <Link href="/modules">Back to Modules</Link>
      </header>

      {error && <p style={{ color: '#b91c1c' }}>{error}</p>}

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16, marginBottom: 16 }}>
        <h2 style={{ marginTop: 0 }}>Details</h2>
        <p>
          <strong>ID:</strong> {deployment?.id || deploymentId}
        </p>
        <p>
          <strong>Module:</strong> {deployment?.moduleName} v{deployment?.moduleVersion}
        </p>
        <p>
          <strong>Status:</strong> {deployment?.status || 'Loading...'}
        </p>
        <p>
          <strong>Created:</strong> {deployment?.createdAtUtc || '-'}
        </p>
        {deployment?.errorMessage && (
          <p style={{ color: '#b91c1c' }}>
            <strong>Error:</strong> {deployment.errorMessage}
          </p>
        )}
      </section>

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
        <h2 style={{ marginTop: 0 }}>Live Logs</h2>
        <div style={{ maxHeight: 360, overflowY: 'auto', background: '#111', color: '#eee', padding: 12, borderRadius: 6 }}>
          {logs.length === 0 && <div>No logs yet...</div>}
          {logs.map((log) => (
            <div key={log.id} style={{ marginBottom: 6, fontFamily: 'monospace', fontSize: 13 }}>
              [{log.timestampUtc}] {log.level} - {log.message}
            </div>
          ))}
        </div>
      </section>
    </main>
  )
}