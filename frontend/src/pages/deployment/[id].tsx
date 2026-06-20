import Link from 'next/link'
import { useRouter } from 'next/router'
import { useEffect, useRef, useState } from 'react'
import { destroyDeployment, getDeployment, getDeploymentLogs, retryDeployment, type DeploymentDetails, type DeploymentLog } from '../../lib/api'
import { useAuthStore } from '../../store/auth'

const TERMINAL_STATUSES = new Set(['SUCCEEDED', 'FAILED', 'DESTROYED', 'ROLLED_BACK'])

function extractRebuildLinks(logs: DeploymentLog[]): { nextDeploymentId?: string; dependsOnDeploymentId?: string } {
  for (let index = logs.length - 1; index >= 0; index -= 1) {
    const context = logs[index]?.context
    if (typeof context !== 'object' || context === null) {
      continue
    }

    const record = context as Record<string, unknown>
    const nextDeploymentId = typeof record.nextDeploymentId === 'string' ? record.nextDeploymentId : undefined
    const dependsOnDeploymentId = typeof record.dependsOnDeploymentId === 'string' ? record.dependsOnDeploymentId : undefined
    if (nextDeploymentId || dependsOnDeploymentId) {
      return { nextDeploymentId, dependsOnDeploymentId }
    }
  }

  return {}
}

function StatusBadge({ status }: { status: string | undefined }) {
  if (!status) return <span style={{ color: '#6b7280' }}>Loading...</span>

  const isActive = !TERMINAL_STATUSES.has(status)

  const colors: Record<string, { bg: string; color: string }> = {
    QUEUED: { bg: '#fef9c3', color: '#854d0e' },
    RUNNING: { bg: '#dbeafe', color: '#1e40af' },
    SUCCEEDED: { bg: '#dcfce7', color: '#166534' },
    FAILED: { bg: '#fee2e2', color: '#991b1b' },
    DESTROYED: { bg: '#f3f4f6', color: '#374151' },
    ROLLED_BACK: { bg: '#e0e7ff', color: '#3730a3' },
  }
  const style = colors[status] ?? { bg: '#f3f4f6', color: '#374151' }

  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 6,
      padding: '2px 10px', borderRadius: 12, fontSize: 13, fontWeight: 600,
      backgroundColor: style.bg, color: style.color
    }}>
      {isActive && (
        <span style={{
          width: 8, height: 8, borderRadius: '50%',
          backgroundColor: status === 'RUNNING' ? '#3b82f6' : '#f59e0b',
          display: 'inline-block',
          animation: 'pulse 1.5s ease-in-out infinite'
        }} />
      )}
      {status}
    </span>
  )
}

function formatUtc(value: string | undefined | null): string {
  if (!value) return '-'
  try {
    return new Date(value).toLocaleString(undefined, {
      dateStyle: 'medium', timeStyle: 'medium'
    })
  } catch {
    return value
  }
}

function formatLogTimestamp(value: string): string {
  try {
    return new Date(value).toLocaleTimeString(undefined, { hour12: false, timeStyle: 'medium' })
  } catch {
    return value
  }
}

function LogLevelColor(level: string): string {
  if (level === 'ERROR') return '#f87171'
  if (level === 'WARN') return '#fbbf24'
  if (level === 'INFO') return '#86efac'
  return '#e5e7eb'
}

function getErrorMessage(err: unknown, fallback: string): string {
  if (typeof err === 'object' && err !== null && 'response' in err) {
    const response = (err as { response?: { data?: { message?: string } } }).response
    if (typeof response?.data?.message === 'string' && response.data.message.length > 0) {
      return response.data.message
    }
  }
  return fallback
}

type RetryFieldType = 'scalar' | 'string-array' | 'json'
const FORCE_JSON_RETRY_FIELDS = new Set(['subnets', 'nsgs', 'tags'])

export default function DeploymentPage() {
  const router = useRouter()
  const deploymentId = router.query.id as string | undefined

  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)

  const [deployment, setDeployment] = useState<DeploymentDetails | null>(null)
  const [logs, setLogs] = useState<DeploymentLog[]>([])
  const [error, setError] = useState<string | null>(null)
  const [actionMessage, setActionMessage] = useState<string | null>(null)
  const [isDestroying, setIsDestroying] = useState(false)
  const [showRetryPanel, setShowRetryPanel] = useState(false)
  const [retryInputs, setRetryInputs] = useState<Record<string, string>>({})
  const [retryFieldTypes, setRetryFieldTypes] = useState<Record<string, RetryFieldType>>({})
  const [isRetrying, setIsRetrying] = useState(false)

  const latestLogIdRef = useRef<number | undefined>(undefined)
  const autoRedirectRef = useRef(false)
  const logsEndRef = useRef<HTMLDivElement>(null)
  const [lastPollAt, setLastPollAt] = useState<Date | null>(null)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (!deploymentId) {
      return
    }

    // Reset stream state when navigating between deployments so logs/status don't leak.
    setDeployment(null)
    setLogs([])
    setError(null)
    setActionMessage(null)
    setShowRetryPanel(false)
    setRetryInputs({})
    setRetryFieldTypes({})
    setLastPollAt(null)
    latestLogIdRef.current = undefined
    autoRedirectRef.current = false
  }, [deploymentId])

  useEffect(() => {
    if (!token) {
      router.replace('/login')
      return
    }

    if (!deploymentId) {
      return
    }

    let cancelled = false
    // Use a ref-style flag so the interval callback always reads the latest value
    // without depending on React state. Avoids the "return prev" bailout pattern
    // that could race with state updates in the same batch.
    let stopped = false

    const fetchLogs = async () => {
      try {
        const newLogs = await getDeploymentLogs(deploymentId, latestLogIdRef.current)
        if (!cancelled && newLogs.length > 0) {
          latestLogIdRef.current = newLogs[newLogs.length - 1].id
          setLogs((current) => [...current, ...newLogs])
          // auto-scroll to bottom
          setTimeout(() => logsEndRef.current?.scrollIntoView({ behavior: 'smooth' }), 50)
        }
      } catch {
        if (!cancelled) {
          setError('Could not load deployment logs.')
        }
      }
    }

    const fetchDeployment = async () => {
      try {
        const details = await getDeployment(deploymentId)
        if (!cancelled) {
          setDeployment(details)
          setLastPollAt(new Date())
          // Terminal status can race with final log writes. Pull one final log batch
          // before stopping to avoid stale/truncated stream output.
          if (TERMINAL_STATUSES.has(details.status)) {
            await fetchLogs()
            stopped = true
          }
        }
      } catch {
        if (!cancelled) {
          setError('Could not load deployment details.')
        }
      }
    }

    fetchDeployment()
    fetchLogs()

    const deploymentTimer = setInterval(async () => {
      if (stopped) return
      await fetchDeployment()
    }, 3000)
    const logsTimer = setInterval(async () => {
      if (stopped) return
      await fetchLogs()
    }, 2000)

    // When the user returns to the tab (e.g. after it was backgrounded during a long
    // deployment), immediately re-fetch so the button appears without waiting for the
    // next poll interval.
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible' && !stopped && !cancelled) {
        fetchDeployment()
        fetchLogs()
      }
    }
    document.addEventListener('visibilitychange', onVisibilityChange)

    return () => {
      cancelled = true
      stopped = true
      clearInterval(deploymentTimer)
      clearInterval(logsTimer)
      document.removeEventListener('visibilitychange', onVisibilityChange)
    }
  }, [token, deploymentId, router])

  useEffect(() => {
    if (!deploymentId || !deployment || autoRedirectRef.current) {
      return
    }

    const { nextDeploymentId, dependsOnDeploymentId } = extractRebuildLinks(logs)

    // If user lands on queued redeploy step, jump to destroy step for live progress.
    if (deployment.status === 'QUEUED'
      && typeof dependsOnDeploymentId === 'string'
      && dependsOnDeploymentId !== deploymentId) {
      autoRedirectRef.current = true
      setActionMessage(`Rebuild is waiting on destroy step ${dependsOnDeploymentId}. Redirecting...`)
      router.push(`/deployment/${dependsOnDeploymentId}`)
      return
    }

    // When destroy step finishes, automatically continue to queued/running redeploy.
    if (deployment.status === 'ROLLED_BACK'
      && typeof nextDeploymentId === 'string'
      && nextDeploymentId !== deploymentId) {
      autoRedirectRef.current = true
      setActionMessage(`Destroy step completed. Redirecting to redeploy step ${nextDeploymentId}...`)
      router.push(`/deployment/${nextDeploymentId}`)
    }
  }, [deploymentId, deployment, logs, router])

  const triggerDestroy = async () => {
    if (!deploymentId || !deployment || deployment.status !== 'SUCCEEDED' || isDestroying) {
      return
    }

    const confirmed = window.confirm('Queue destroy for this deployment? This will remove created resources.')
    if (!confirmed) {
      return
    }

    setIsDestroying(true)
    setActionMessage(null)
    setError(null)

    try {
      const response = await destroyDeployment(deploymentId)
      setActionMessage(`Destroy queued as deployment ${response.id}. Redirecting...`)
      router.push(`/deployment/${response.id}`)
    } catch (err: unknown) {
      setError(getErrorMessage(err, 'Failed to queue destroy deployment.'))
    } finally {
      setIsDestroying(false)
    }
  }

  const openRetryPanel = () => {
    if (!deployment) return
    // Pre-populate editable inputs from the failed deployment, excluding internal meta-keys.
    const editable: Record<string, string> = {}
    const types: Record<string, RetryFieldType> = {}
    for (const [k, v] of Object.entries(deployment.inputs)) {
      if (k.startsWith('__')) continue
      if (FORCE_JSON_RETRY_FIELDS.has(k)) {
        if (Array.isArray(v)) {
          const normalized = v.map((item) => {
            if (typeof item === 'string') {
              const trimmed = item.trim()
              if (trimmed === '[object Object]') return {}
              try {
                return JSON.parse(trimmed)
              } catch {
                return item
              }
            }
            return item
          })
          editable[k] = JSON.stringify(normalized, null, 2)
        } else if (v !== null && typeof v === 'object') {
          editable[k] = JSON.stringify(v, null, 2)
        } else if (typeof v === 'string') {
          const trimmed = v.trim()
          if (!trimmed) {
            editable[k] = k === 'tags' ? '{}' : '[]'
          } else {
            try {
              editable[k] = JSON.stringify(JSON.parse(trimmed), null, 2)
            } catch {
              editable[k] = trimmed
            }
          }
        } else {
          editable[k] = k === 'tags' ? '{}' : '[]'
        }
        types[k] = 'json'
        continue
      }
      if (Array.isArray(v)) {
        const hasOnlyStrings = v.every((item) => typeof item === 'string')
        if (hasOnlyStrings) {
          editable[k] = (v as string[]).join(', ')
          types[k] = 'string-array'
        } else {
          editable[k] = JSON.stringify(v, null, 2)
          types[k] = 'json'
        }
      } else if (v !== null && typeof v === 'object') {
        editable[k] = JSON.stringify(v, null, 2)
        types[k] = 'json'
      } else {
        editable[k] = String(v ?? '')
        types[k] = 'scalar'
      }
    }
    setRetryInputs(editable)
    setRetryFieldTypes(types)
    setShowRetryPanel(true)
    setActionMessage(null)
    setError(null)
  }

  const submitRetry = async () => {
    if (!deploymentId || isRetrying) return
    setIsRetrying(true)
    setError(null)
    setActionMessage(null)
    try {
      // Reconstruct values: comma-separated strings become arrays for known array fields.
      const inputs: Record<string, unknown> = {}
      for (const [k, v] of Object.entries(retryInputs)) {
        const fieldType = retryFieldTypes[k] ?? 'scalar'
        if (fieldType === 'string-array' && !FORCE_JSON_RETRY_FIELDS.has(k)) {
          inputs[k] = v.split(',').map((s) => s.trim()).filter(Boolean)
        } else if (fieldType === 'json' || FORCE_JSON_RETRY_FIELDS.has(k)) {
          try {
            inputs[k] = JSON.parse(v)
          } catch {
            throw new Error(`${k} must be valid JSON.`)
          }
        } else {
          inputs[k] = v
        }
      }
      const response = await retryDeployment(deploymentId, inputs)
      setActionMessage(`Retry queued as deployment ${response.id}. Redirecting...`)
      router.push(`/deployment/${response.id}`)
    } catch (err: unknown) {
      setError(getErrorMessage(err, 'Failed to queue retry deployment.'))
    } finally {
      setIsRetrying(false)
    }
  }

  const isActive = deployment ? !TERMINAL_STATUSES.has(deployment.status) : false

  return (
    <main style={{ maxWidth: 1100, margin: '2rem auto', padding: '0 1rem' }}>
      <style>{`
        @keyframes pulse {
          0%, 100% { opacity: 1; }
          50% { opacity: 0.3; }
        }
      `}</style>

      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Deployment Status</h1>
        <Link href="/modules">Back to Modules</Link>
      </header>

      {error && <p style={{ color: '#b91c1c' }}>{error}</p>}

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16, marginBottom: 16 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
          <h2 style={{ margin: 0 }}>Details</h2>
          {isActive && (
            <span style={{ fontSize: 12, color: '#6b7280', display: 'flex', alignItems: 'center', gap: 6 }}>
              <span style={{
                width: 7, height: 7, borderRadius: '50%', backgroundColor: '#22c55e',
                display: 'inline-block', animation: 'pulse 1.5s ease-in-out infinite'
              }} />
              Live · last updated {lastPollAt ? lastPollAt.toLocaleTimeString() : '...'}
            </span>
          )}
        </div>

        <table style={{ borderCollapse: 'collapse', width: '100%' }}>
          <tbody>
            <tr>
              <td style={{ padding: '4px 0', color: '#374151', width: 120 }}><strong>ID</strong></td>
              <td style={{ padding: '4px 0', fontFamily: 'monospace', fontSize: 13 }}>{deployment?.id || deploymentId}</td>
            </tr>
            <tr>
              <td style={{ padding: '4px 0', color: '#374151' }}><strong>Module</strong></td>
              <td style={{ padding: '4px 0' }}>{deployment ? `${deployment.moduleName} v${deployment.moduleVersion}` : '—'}</td>
            </tr>
            <tr>
              <td style={{ padding: '4px 0', color: '#374151' }}><strong>Status</strong></td>
              <td style={{ padding: '6px 0' }}><StatusBadge status={deployment?.status} /></td>
            </tr>
            <tr>
              <td style={{ padding: '4px 0', color: '#374151' }}><strong>Created</strong></td>
              <td style={{ padding: '4px 0' }}>{formatUtc(deployment?.createdAtUtc)}</td>
            </tr>
            {deployment?.completedAtUtc && (
              <tr>
                <td style={{ padding: '4px 0', color: '#374151' }}><strong>Completed</strong></td>
                <td style={{ padding: '4px 0' }}>{formatUtc(deployment.completedAtUtc)}</td>
              </tr>
            )}
          </tbody>
        </table>

        {deployment?.errorMessage && (
          <div style={{ marginTop: 12, padding: 10, backgroundColor: '#fef2f2', borderRadius: 4, color: '#991b1b', fontSize: 13 }}>
            <strong>Error:</strong> {deployment.errorMessage}
          </div>
        )}

        {/* Show as soon as the deployment has failed at least once, even if the worker
            has re-queued it for an automatic retry with the same wrong inputs. */}
        {(deployment?.status === 'FAILED' || (deployment?.retryCount != null && deployment.retryCount > 0 && !!deployment.errorMessage)) && (
          <div style={{ marginTop: 14 }}>
            {!showRetryPanel ? (
              <button onClick={openRetryPanel}>Edit &amp; Retry</button>
            ) : (
              <div style={{ marginTop: 8, border: '1px solid #fca5a5', borderRadius: 6, padding: 16, backgroundColor: '#fff7f7' }}>
                <h3 style={{ margin: '0 0 12px', fontSize: 15 }}>Edit inputs and retry</h3>
                <p style={{ margin: '0 0 12px', fontSize: 13, color: '#6b7280' }}>
                  Correct the values below then click <strong>Queue Retry</strong>. The deployment will reuse the existing Terraform state so only the changed resources are updated.
                </p>
                <table style={{ width: '100%', borderCollapse: 'collapse' }}>
                  <tbody>
                    {Object.entries(retryInputs).map(([key, value]) => (
                      <tr key={key}>
                        <td style={{ padding: '4px 8px 4px 0', verticalAlign: 'middle', width: 200 }}>
                          <label style={{ fontFamily: 'monospace', fontSize: 13, color: '#374151' }}>{key}</label>
                        </td>
                        <td style={{ padding: '4px 0' }}>
                          {(retryFieldTypes[key] === 'json' || FORCE_JSON_RETRY_FIELDS.has(key)) ? (
                            <textarea
                              value={value}
                              onChange={(e) => setRetryInputs((prev) => ({ ...prev, [key]: e.target.value }))}
                              style={{ width: '100%', minHeight: 120, fontFamily: 'monospace', fontSize: 12, padding: '6px 8px', boxSizing: 'border-box' }}
                            />
                          ) : (
                            <input
                              type="text"
                              value={value}
                              onChange={(e) => setRetryInputs((prev) => ({ ...prev, [key]: e.target.value }))}
                              style={{ width: '100%', fontFamily: 'monospace', fontSize: 13, padding: '3px 6px', boxSizing: 'border-box' }}
                            />
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <div style={{ marginTop: 14, display: 'flex', gap: 8 }}>
                  <button onClick={submitRetry} disabled={isRetrying}>
                    {isRetrying ? 'Queueing...' : 'Queue Retry'}
                  </button>
                  <button onClick={() => setShowRetryPanel(false)} disabled={isRetrying} style={{ background: 'none', border: '1px solid #d1d5db' }}>
                    Cancel
                  </button>
                </div>
              </div>
            )}
          </div>
        )}

        {isActive && (
          <div style={{ marginTop: 14, padding: 10, backgroundColor: '#f0f9ff', borderRadius: 4, border: '1px solid #bae6fd', fontSize: 13, color: '#0369a1' }}>
            ⏳ Deployment is in progress — this page will update automatically. Do not close this tab.
          </div>
        )}

        {deployment?.status === 'SUCCEEDED' && (
          <div style={{ marginTop: 14 }}>
            <button onClick={triggerDestroy} disabled={isDestroying}>
              {isDestroying ? 'Queueing Destroy...' : 'Destroy Resources'}
            </button>
          </div>
        )}

        {actionMessage && <p style={{ marginTop: 8 }}>{actionMessage}</p>}
      </section>

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 10 }}>
          <h2 style={{ margin: 0 }}>Logs</h2>
          {isActive && (
            <span style={{ fontSize: 12, color: '#22c55e', display: 'flex', alignItems: 'center', gap: 5 }}>
              <span style={{
                width: 7, height: 7, borderRadius: '50%', backgroundColor: '#22c55e',
                display: 'inline-block', animation: 'pulse 1.5s ease-in-out infinite'
              }} />
              Streaming
            </span>
          )}
        </div>
        <div style={{ maxHeight: 420, overflowY: 'auto', background: '#0f172a', color: '#e2e8f0', padding: 12, borderRadius: 6 }}>
          {logs.length === 0 && (
            <div style={{ color: '#64748b', fontFamily: 'monospace', fontSize: 13 }}>
              {isActive ? 'Waiting for logs...' : 'No logs recorded.'}
            </div>
          )}
          {logs.map((log) => (
            <div key={log.id} style={{ marginBottom: 4, fontFamily: 'monospace', fontSize: 13, lineHeight: 1.5 }}>
              <span style={{ color: '#64748b', marginRight: 8 }}>{formatLogTimestamp(log.timestampUtc)}</span>
              <span style={{ color: LogLevelColor(log.level), marginRight: 8, fontWeight: 600 }}>{log.level}</span>
              <span>{log.message}</span>
            </div>
          ))}
          <div ref={logsEndRef} />
        </div>
      </section>
    </main>
  )
}