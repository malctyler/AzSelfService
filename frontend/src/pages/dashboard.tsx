import Link from 'next/link'
import { useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import axios from 'axios'
import { getManagedResources, getModules, type ManagedResourceSummary, type ModuleSummary } from '../lib/api'
import { useAuthStore } from '../store/auth'

export default function DashboardPage() {
  const router = useRouter()
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)
  const user = useAuthStore((state) => state.user)
  const clearSession = useAuthStore((state) => state.clearSession)

  const [modules, setModules] = useState<ModuleSummary[]>([])
  const [managedResources, setManagedResources] = useState<ManagedResourceSummary[]>([])

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

        {managedResources.length === 0 ? (
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
                </tr>
              </thead>
              <tbody>
                {managedResources.map((resource) => (
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