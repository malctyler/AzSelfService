import Link from 'next/link'
import { useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import axios from 'axios'
import { deleteFailedDeployment, getManagedResources, getModules, rebuildAllDeployments, rebuildDeployment, type ManagedResourceSummary, type ModuleSummary } from '../lib/api'
import { useAuthStore } from '../store/auth'

export default function DashboardPage() {
  const router = useRouter()
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)
  const user = useAuthStore((state) => state.user)
  const clearSession = useAuthStore((state) => state.clearSession)

  const [modules, setModules] = useState<ModuleSummary[]>([])
  const [managedResources, setManagedResources] = useState<ManagedResourceSummary[]>([])
  const [selectedStatus, setSelectedStatus] = useState('ALL')
  const [deletingDeploymentId, setDeletingDeploymentId] = useState<string | null>(null)
  const [rebuildingDeploymentId, setRebuildingDeploymentId] = useState<string | null>(null)
  const [rebuildingAll, setRebuildingAll] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const [actionMessage, setActionMessage] = useState<string | null>(null)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (!token) {
      router.replace('/login')
      return
    }

    Promise.all([getModules(), getManagedResources()])
      .then(([moduleData, resourceData]) => {
        setModules(moduleData)
        setManagedResources(resourceData)
      })
      .catch((error) => {
        if (axios.isAxiosError(error) && (error.response?.status === 401 || error.response?.status === 403)) {
          clearSession()
          router.replace('/login')
          return
        }

        setModules([])
        setManagedResources([])
      })
  }, [token, router, clearSession])

  const statusOptions = ['ALL', ...Array.from(new Set(managedResources.map((resource) => resource.status))).sort()]
  const filteredResources = selectedStatus === 'ALL'
    ? managedResources
    : managedResources.filter((resource) => resource.status === selectedStatus)
  const rebuildableCount = managedResources.filter((resource) => resource.status === 'SUCCEEDED').length

  const handleDeleteFailedDeployment = async (deploymentId: string) => {
    if (deletingDeploymentId || rebuildingDeploymentId) return

    const confirmed = window.confirm('Delete this failed deployment from the dashboard history? This cannot be undone.')
    if (!confirmed) return

    setDeletingDeploymentId(deploymentId)
    setActionError(null)
    setActionMessage(null)

    try {
      await deleteFailedDeployment(deploymentId)

      // Remove the deleted record immediately so the UI reflects the action
      // even before any background refresh.
      setManagedResources((prev) => prev.filter((resource) => resource.deploymentId !== deploymentId))
      setActionMessage(`Deleted failed deployment ${deploymentId}.`)

      // Best-effort refresh in case the backend grouping surfaces another
      // historical record for the same state path.
      getManagedResources()
        .then((refreshedResources) => setManagedResources(refreshedResources))
        .catch(() => {
          // Keep optimistic state if refresh fails; user still sees deletion.
        })
    } catch (error: unknown) {
      if (axios.isAxiosError(error)) {
        setActionError(error.response?.data?.message ?? 'Failed to delete deployment.')
      } else {
        setActionError('Failed to delete deployment.')
      }
    } finally {
      setDeletingDeploymentId(null)
    }
  }

  const handleRebuildDeployment = async (deploymentId: string) => {
    if (deletingDeploymentId || rebuildingDeploymentId || rebuildingAll) return

    const confirmed = window.confirm('Rebuild this resource? This will queue destroy followed by redeploy using the original inputs.')
    if (!confirmed) return

    setRebuildingDeploymentId(deploymentId)
    setActionError(null)
    setActionMessage(null)

    try {
      const response = await rebuildDeployment(deploymentId)
      setActionMessage(`Rebuild queued. Destroy: ${response.destroyDeploymentId}, Redeploy: ${response.redeployDeploymentId}. Redirecting to destroy step...`)
      router.push(`/deployment/${response.destroyDeploymentId}`)
    } catch (error: unknown) {
      if (axios.isAxiosError(error)) {
        setActionError(error.response?.data?.message ?? 'Failed to queue rebuild deployment.')
      } else {
        setActionError('Failed to queue rebuild deployment.')
      }
    } finally {
      setRebuildingDeploymentId(null)
    }
  }

  const handleRebuildAll = async () => {
    if (deletingDeploymentId || rebuildingDeploymentId || rebuildingAll || rebuildableCount === 0) return

    const confirmed = window.confirm(`Rebuild all ${rebuildableCount} succeeded deployments? This will queue destroy jobs in reverse build order and redeploy in original build order.`)
    if (!confirmed) return

    setRebuildingAll(true)
    setActionError(null)
    setActionMessage(null)

    try {
      const response = await rebuildAllDeployments()
      setActionMessage(`Rebuild-all queued (${response.deploymentCount} deployments, batch ${response.batchId}).`)
    } catch (error: unknown) {
      if (axios.isAxiosError(error)) {
        setActionError(error.response?.data?.message ?? 'Failed to queue rebuild-all.')
      } else {
        setActionError('Failed to queue rebuild-all.')
      }
    } finally {
      setRebuildingAll(false)
    }
  }

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
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginBottom: 8 }}>
          <Link href="/modules">Go to Module Catalog</Link>
          <Link href="/modules?resourceGroup=1" style={{ fontWeight: 600, color: '#2563eb', border: '1px solid #2563eb', borderRadius: 4, padding: '6px 14px', textDecoration: 'none' }}>Create Resource Group</Link>
        </div>
        {user?.role?.toLowerCase() === 'admin' && (
          <div style={{ marginTop: 8 }}>
            <Link href="/admin/modules">Go to Admin Modules</Link>
            <span> | </span>
            <Link href="/admin/customers">Manage Customers</Link>
          </div>
        )}
      </section>

      <section style={{ marginTop: 24, padding: 16, border: '1px solid #ddd', borderRadius: 8 }}>
        <h2 style={{ marginTop: 0 }}>Managed Resources</h2>
        <p style={{ color: '#555' }}>Resources deployed through AzSelfService for this customer.</p>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
          <label htmlFor="status-filter" style={{ color: '#374151', fontWeight: 600 }}>Status</label>
          <select
            id="status-filter"
            value={selectedStatus}
            onChange={(event) => setSelectedStatus(event.target.value)}
            style={{ border: '1px solid #d1d5db', borderRadius: 6, padding: '6px 10px', backgroundColor: '#fff' }}
          >
            {statusOptions.map((status) => (
              <option key={status} value={status}>{status}</option>
            ))}
          </select>
          <span style={{ color: '#64748b', fontSize: 13 }}>{filteredResources.length} shown</span>
          <button
            onClick={handleRebuildAll}
            disabled={rebuildableCount === 0 || !!deletingDeploymentId || !!rebuildingDeploymentId || rebuildingAll}
            style={{ marginLeft: 'auto', color: '#0f172a', border: '1px solid #93c5fd', background: '#eff6ff' }}
          >
            {rebuildingAll ? 'Queueing All...' : `Rebuild All (${rebuildableCount})`}
          </button>
        </div>

        {actionMessage && <p style={{ marginTop: 0, color: '#166534' }}>{actionMessage}</p>}
        {actionError && <p style={{ marginTop: 0, color: '#b91c1c' }}>{actionError}</p>}

        {filteredResources.length === 0 ? (
          <p style={{ marginBottom: 0 }}>No managed resources yet. Create a resource group to get started.</p>
        ) : (
          <div style={{ overflowX: 'auto' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse' }}>
              <thead>
                <tr>
                  <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 6px' }}>Resource</th>
                  <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 6px' }}>Location</th>
                  <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 6px' }}>Status</th>
                  <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 6px' }}>Module</th>
                  <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 6px' }}>Created</th>
                  <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 6px' }}>Deployment</th>
                  <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 6px' }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredResources.map((resource) => (
                  <tr key={resource.deploymentId}>
                    <td style={{ borderBottom: '1px solid #f1f5f9', padding: '8px 6px' }}>
                      <div style={{ fontWeight: 600 }}>{resource.resourceName}</div>
                      {resource.resourceId && <div style={{ color: '#64748b', fontSize: 12 }}>{resource.resourceId}</div>}
                    </td>
                    <td style={{ borderBottom: '1px solid #f1f5f9', padding: '8px 6px' }}>{resource.resourceLocation || 'n/a'}</td>
                    <td style={{ borderBottom: '1px solid #f1f5f9', padding: '8px 6px' }}>{resource.status}</td>
                    <td style={{ borderBottom: '1px solid #f1f5f9', padding: '8px 6px' }}>
                      {resource.moduleName} v{resource.moduleVersion}
                    </td>
                    <td style={{ borderBottom: '1px solid #f1f5f9', padding: '8px 6px' }}>
                      {new Date(resource.createdAtUtc).toLocaleString()}
                    </td>
                    <td style={{ borderBottom: '1px solid #f1f5f9', padding: '8px 6px' }}>
                      <Link href={`/deployment/${resource.deploymentId}`}>View</Link>
                    </td>
                    <td style={{ borderBottom: '1px solid #f1f5f9', padding: '8px 6px' }}>
                      {resource.status === 'FAILED' ? (
                        <button
                          onClick={() => handleDeleteFailedDeployment(resource.deploymentId)}
                          disabled={deletingDeploymentId === resource.deploymentId || !!rebuildingDeploymentId}
                          style={{ color: '#991b1b', border: '1px solid #fca5a5', background: '#fff1f2' }}
                        >
                          {deletingDeploymentId === resource.deploymentId ? 'Deleting...' : 'Delete'}
                        </button>
                      ) : resource.status === 'SUCCEEDED' ? (
                        <button
                          onClick={() => handleRebuildDeployment(resource.deploymentId)}
                          disabled={rebuildingDeploymentId === resource.deploymentId || !!deletingDeploymentId}
                          style={{ color: '#0f172a', border: '1px solid #93c5fd', background: '#eff6ff' }}
                        >
                          {rebuildingDeploymentId === resource.deploymentId ? 'Queueing...' : 'Rebuild'}
                        </button>
                      ) : (
                        <span style={{ color: '#9ca3af' }}>-</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </main>
  )
}