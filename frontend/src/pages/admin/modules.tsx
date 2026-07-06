import Link from 'next/link'
import { useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import {
  deprecateModule,
  getAdminModules,
  getAllowedRegions,
  publishModule,
  registerModule,
  updateAllowedRegions,
  type ModuleSummary
} from '../../lib/api'
import { useAuthStore } from '../../store/auth'

function getErrorMessage(err: unknown, fallback: string): string {
  if (typeof err === 'object' && err !== null && 'response' in err) {
    const response = (err as { response?: { data?: { message?: string } } }).response
    if (typeof response?.data?.message === 'string' && response.data.message.length > 0) {
      return response.data.message
    }
  }
  return fallback
}

export default function AdminModulesPage() {
  const router = useRouter()
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)
  const user = useAuthStore((state) => state.user)

  const [modules, setModules] = useState<ModuleSummary[]>([])
  const [modulePath, setModulePath] = useState('terraform-modules/resource-group')
  const [regionCodesText, setRegionCodesText] = useState('')
  const [isBusy, setIsBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)

  const isAdminUser = user?.role?.toLowerCase() === 'admin'

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (!token) {
      router.replace('/login')
      return
    }

    if (!isAdminUser) {
      router.replace('/modules')
      return
    }

    void refreshModules()
  }, [token, isAdminUser, router])

  const refreshModules = async () => {
    try {
      const [data, regions] = await Promise.all([getAdminModules(), getAllowedRegions()])
      setModules(data)
      setRegionCodesText(regions.map((region) => region.code).join('\n'))
      setMessage(null)
    } catch {
      setModules([])
      setMessage('Failed to load admin module data.')
    }
  }

  const handleRegister = async () => {
    setIsBusy(true)
    setMessage(null)

    try {
      const registeredModule = await registerModule(modulePath)
      await refreshModules()
      setMessage(`Registered ${registeredModule.name} v${registeredModule.version}.`)
    } catch (err: unknown) {
      setMessage(getErrorMessage(err, 'Failed to register module.'))
    } finally {
      setIsBusy(false)
    }
  }

  const handlePublish = async (id: string, name: string, version: string) => {
    setIsBusy(true)
    setMessage(null)

    try {
      await publishModule(id)
      await refreshModules()
      setMessage(`Published ${name} v${version}.`)
    } catch (err: unknown) {
      setMessage(getErrorMessage(err, 'Failed to publish module.'))
    } finally {
      setIsBusy(false)
    }
  }

  const handleSaveRegions = async () => {
    setIsBusy(true)
    setMessage(null)

    try {
      const codes = regionCodesText
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter((line) => line.length > 0)

      const updated = await updateAllowedRegions(codes)
      setRegionCodesText(updated.map((region) => region.code).join('\n'))
      await refreshModules()
      setMessage(`Saved ${updated.length} allowed regions.`)
    } catch (err: unknown) {
      setMessage(getErrorMessage(err, 'Failed to save allowed regions.'))
    } finally {
      setIsBusy(false)
    }
  }

  const handleDeprecate = async (id: string, name: string, version: string) => {
    setIsBusy(true)
    setMessage(null)

    try {
      await deprecateModule(id)
      await refreshModules()
      setMessage(`Deprecated ${name} v${version}.`)
    } catch (err: unknown) {
      setMessage(getErrorMessage(err, 'Failed to deprecate module.'))
    } finally {
      setIsBusy(false)
    }
  }

  return (
    <main style={{ maxWidth: 1100, margin: '2rem auto', padding: '0 1rem' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Admin Modules</h1>
        <div style={{ display: 'flex', gap: 12 }}>
          <Link href="/admin/software-packages">Software Packages</Link>
          <Link href="/dashboard">Back to Dashboard</Link>
        </div>
      </header>

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16, marginBottom: 16 }}>
        <h2 style={{ marginTop: 0 }}>Register or Update</h2>
        <label style={{ display: 'block', marginBottom: 4 }}>Module Path</label>
        <input
          value={modulePath}
          onChange={(event) => setModulePath(event.target.value)}
          style={{ width: '100%', padding: 8, marginBottom: 10 }}
          placeholder="terraform-modules/resource-group"
        />
        <button onClick={handleRegister} disabled={isBusy || modulePath.trim().length === 0}>
          Register Module
        </button>
      </section>

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16, marginBottom: 16 }}>
        <h2 style={{ marginTop: 0 }}>Allowed Azure Regions</h2>
        <p style={{ color: '#555' }}>
          One Azure region code per line. This list is applied to every module location dropdown and backend validation.
        </p>
        <textarea
          value={regionCodesText}
          onChange={(event) => setRegionCodesText(event.target.value)}
          style={{ width: '100%', minHeight: 180, padding: 8, marginBottom: 10, fontFamily: 'monospace' }}
          placeholder={'eastus\nwestus\neastus2\nwesteurope'}
        />
        <button onClick={handleSaveRegions} disabled={isBusy}>
          Save Regions
        </button>
      </section>

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
        <h2 style={{ marginTop: 0 }}>All Modules</h2>
        <p style={{ color: '#555' }}>
          Includes published and deprecated modules. Total: {modules.length}
        </p>

        <div style={{ overflowX: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse' }}>
            <thead>
              <tr>
                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Name</th>
                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Version</th>
                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Path</th>
                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Status</th>
                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {modules.map((module) => {
                const status = module.isDeprecated
                  ? 'deprecated'
                  : module.isPublished
                    ? 'published'
                    : 'unpublished'

                return (
                  <tr key={module.id}>
                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{module.name}</td>
                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{module.version}</td>
                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{module.terraformPath}</td>
                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>
                      {status}
                    </td>
                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>
                      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                        <button
                          onClick={() => handlePublish(module.id, module.name, module.version)}
                          disabled={isBusy}
                        >
                          Publish
                        </button>
                        <button
                          onClick={() => handleDeprecate(module.id, module.name, module.version)}
                          disabled={isBusy}
                        >
                          Deprecate
                        </button>
                      </div>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </section>

      {message && <p style={{ marginTop: 12 }}>{message}</p>}
    </main>
  )
}