import Link from 'next/link'
import { useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import {
    getSoftwarePackageCatalog,
    uploadSoftwarePackage,
    validateSoftwarePackage,
    type SoftwarePackageCatalogItem,
    type SoftwarePackageValidationResponse
} from '../../lib/api'
import { useAuthStore } from '../../store/auth'

function getErrorMessage(err: unknown, fallback: string): string {
    if (typeof err === 'object' && err !== null) {
        const maybeAxiosError = err as {
            message?: string
            response?: {
                status?: number
                statusText?: string
                data?: {
                    message?: string
                    errors?: string[] | Record<string, string[]>
                    title?: string
                    detail?: string
                } | string
            }
        }

        const response = maybeAxiosError.response
        const data = response?.data

        if (data && typeof data === 'object') {
            if (Array.isArray(data.errors) && data.errors.length > 0) {
                return data.errors.join(' | ')
            }

            if (data.errors && typeof data.errors === 'object') {
                const flattened = Object.values(data.errors)
                    .flatMap((messages) => messages)
                    .filter((message) => typeof message === 'string' && message.length > 0)
                if (flattened.length > 0) {
                    return flattened.join(' | ')
                }
            }

            if (typeof data.detail === 'string' && data.detail.length > 0) {
                return data.detail
            }

            if (typeof data.message === 'string' && data.message.length > 0) {
                return data.message
            }

            if (typeof data.title === 'string' && data.title.length > 0) {
                return data.title
            }
        }

        if (typeof data === 'string' && data.length > 0) {
            return data
        }

        if (typeof response?.status === 'number') {
            const suffix = response.statusText ? ` ${response.statusText}` : ''
            return `${fallback} (HTTP ${response.status}${suffix})`
        }

        if (typeof maybeAxiosError.message === 'string' && maybeAxiosError.message.length > 0) {
            return maybeAxiosError.message
        }
    }
    return fallback
}

export default function AdminSoftwarePackagesPage() {
    const router = useRouter()
    const hydrate = useAuthStore((state) => state.hydrate)
    const token = useAuthStore((state) => state.token)
    const user = useAuthStore((state) => state.user)

    const [scope, setScope] = useState<'platform' | 'customer'>('platform')
    const [customerId, setCustomerId] = useState('')
    const [storageAccountName, setStorageAccountName] = useState('azselfservicesoftware01')
    const [containerName, setContainerName] = useState('packages')
    const [isPublished, setIsPublished] = useState(true)
    const [packageFile, setPackageFile] = useState<File | null>(null)

    const [isBusy, setIsBusy] = useState(false)
    const [message, setMessage] = useState<string | null>(null)
    const [validation, setValidation] = useState<SoftwarePackageValidationResponse | null>(null)
    const [catalog, setCatalog] = useState<SoftwarePackageCatalogItem[]>([])
    const [listScope, setListScope] = useState<'platform' | 'customer' | 'all'>('platform')
    const [listCustomerId, setListCustomerId] = useState('')

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

        void refreshCatalog()
    }, [token, isAdminUser, router])

    const refreshCatalog = async () => {
        try {
            const resolvedScope = listScope === 'all' ? undefined : listScope
            const resolvedCustomerId = listCustomerId.trim() || undefined
            const data = await getSoftwarePackageCatalog(resolvedScope, resolvedCustomerId)
            setCatalog(data)
        } catch (err: unknown) {
            setMessage(getErrorMessage(err, 'Failed to load package catalog.'))
        }
    }

    const handleValidate = async () => {
        if (!packageFile) {
            setMessage('Select a package zip file first.')
            return
        }

        setIsBusy(true)
        setMessage(null)
        setValidation(null)

        try {
            const result = await validateSoftwarePackage(packageFile)
            setValidation(result)
            setMessage(result.isValid ? 'Package is valid.' : 'Package validation failed.')
        } catch (err: unknown) {
            setMessage(getErrorMessage(err, 'Failed to validate package.'))
        } finally {
            setIsBusy(false)
        }
    }

    const handleUpload = async () => {
        if (!packageFile) {
            setMessage('Select a package zip file first.')
            return
        }

        if (scope === 'customer' && customerId.trim().length === 0) {
            setMessage('Customer ID is required for customer scope uploads.')
            return
        }

        setIsBusy(true)
        setMessage(null)
        setValidation(null)

        try {
            const uploaded = await uploadSoftwarePackage({
                scope,
                customerId: scope === 'customer' ? customerId.trim() : undefined,
                storageAccountName: storageAccountName.trim(),
                containerName: containerName.trim(),
                isPublished,
                packageFile
            })

            setMessage(`Uploaded and cataloged ${uploaded.packageId} v${uploaded.version}.`)
            await refreshCatalog()
        } catch (err: unknown) {
            setMessage(getErrorMessage(err, 'Failed to upload package.'))
        } finally {
            setIsBusy(false)
        }
    }

    return (
        <main style={{ maxWidth: 1200, margin: '2rem auto', padding: '0 1rem' }}>
            <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <h1>Admin Software Packages</h1>
                <div style={{ display: 'flex', gap: 12 }}>
                    <Link href="/admin/modules">Admin Modules</Link>
                    <Link href="/dashboard">Back to Dashboard</Link>
                </div>
            </header>

            <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16, marginBottom: 16 }}>
                <h2 style={{ marginTop: 0 }}>Upload Package</h2>

                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Scope</label>
                        <select value={scope} onChange={(e) => setScope(e.target.value as 'platform' | 'customer')} style={{ width: '100%', padding: 8 }}>
                            <option value="platform">platform</option>
                            <option value="customer">customer</option>
                        </select>
                    </div>

                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Customer ID (when scope=customer)</label>
                        <input
                            value={customerId}
                            onChange={(e) => setCustomerId(e.target.value)}
                            style={{ width: '100%', padding: 8 }}
                            placeholder="00000000-0000-0000-0000-000000000000"
                            disabled={scope !== 'customer'}
                        />
                    </div>

                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Storage Account Name</label>
                        <input
                            value={storageAccountName}
                            onChange={(e) => setStorageAccountName(e.target.value)}
                            style={{ width: '100%', padding: 8 }}
                        />
                    </div>

                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Container Name</label>
                        <input
                            value={containerName}
                            onChange={(e) => setContainerName(e.target.value)}
                            style={{ width: '100%', padding: 8 }}
                        />
                    </div>

                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Published</label>
                        <select value={isPublished ? 'true' : 'false'} onChange={(e) => setIsPublished(e.target.value === 'true')} style={{ width: '100%', padding: 8 }}>
                            <option value="true">true</option>
                            <option value="false">false</option>
                        </select>
                    </div>

                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Package File (.zip)</label>
                        <input
                            type="file"
                            accept=".zip"
                            onChange={(e) => setPackageFile(e.target.files?.[0] ?? null)}
                            style={{ width: '100%', padding: 8 }}
                        />
                    </div>
                </div>

                <div style={{ display: 'flex', gap: 10, marginTop: 12 }}>
                    <button onClick={handleValidate} disabled={isBusy || !packageFile}>Validate</button>
                    <button onClick={handleUpload} disabled={isBusy || !packageFile}>Upload + Publish</button>
                </div>

                {packageFile && (
                    <p style={{ marginTop: 8, marginBottom: 0, fontSize: 12, color: '#555' }}>
                        Selected file: {packageFile.name} ({Math.ceil(packageFile.size / 1024)} KB)
                    </p>
                )}

                {validation && (
                    <div style={{ marginTop: 12, padding: 10, borderRadius: 6, backgroundColor: validation.isValid ? '#dcfce7' : '#fee2e2', color: validation.isValid ? '#166534' : '#991b1b' }}>
                        <div><strong>Valid:</strong> {String(validation.isValid)}</div>
                        <div><strong>Package ID:</strong> {validation.packageId || '-'}</div>
                        <div><strong>Version:</strong> {validation.version || '-'}</div>
                        {validation.errors.length > 0 && (
                            <div style={{ marginTop: 8 }}>
                                <strong>Errors:</strong>
                                <ul>
                                    {validation.errors.map((error) => <li key={error}>{error}</li>)}
                                </ul>
                            </div>
                        )}
                    </div>
                )}
            </section>

            <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
                <h2 style={{ marginTop: 0 }}>Catalog</h2>

                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr auto', gap: 12, marginBottom: 12 }}>
                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Filter Scope</label>
                        <select value={listScope} onChange={(e) => setListScope(e.target.value as 'platform' | 'customer' | 'all')} style={{ width: '100%', padding: 8 }}>
                            <option value="platform">platform</option>
                            <option value="customer">customer</option>
                            <option value="all">all</option>
                        </select>
                    </div>
                    <div>
                        <label style={{ display: 'block', marginBottom: 4 }}>Filter Customer ID</label>
                        <input value={listCustomerId} onChange={(e) => setListCustomerId(e.target.value)} style={{ width: '100%', padding: 8 }} />
                    </div>
                    <div style={{ display: 'flex', alignItems: 'end' }}>
                        <button onClick={() => void refreshCatalog()} disabled={isBusy}>Refresh</button>
                    </div>
                </div>

                <div style={{ overflowX: 'auto' }}>
                    <table style={{ width: '100%', borderCollapse: 'collapse' }}>
                        <thead>
                            <tr>
                                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Scope</th>
                                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Package</th>
                                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Version</th>
                                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Installer</th>
                                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Published</th>
                                <th style={{ textAlign: 'left', borderBottom: '1px solid #ddd', padding: '8px 4px' }}>Blob Path</th>
                            </tr>
                        </thead>
                        <tbody>
                            {catalog.map((item) => (
                                <tr key={item.id}>
                                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{item.scope}</td>
                                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{item.packageId}</td>
                                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{item.version}</td>
                                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{item.installerType}</td>
                                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px' }}>{String(item.isPublished)}</td>
                                    <td style={{ borderBottom: '1px solid #f0f0f0', padding: '8px 4px', fontFamily: 'monospace', fontSize: 12 }}>{item.blobPath}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            </section>

            {message && <p style={{ marginTop: 12 }}>{message}</p>}
        </main>
    )
}
