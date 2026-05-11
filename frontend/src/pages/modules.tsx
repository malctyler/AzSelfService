import Link from 'next/link'
import { useEffect, useMemo, useState } from 'react'
import { useRouter } from 'next/router'
import { createDeployment, getModules, type ModuleSummary } from '../lib/api'
import { useAuthStore } from '../store/auth'

type FormValues = Record<string, string>

export default function ModulesPage() {
  const router = useRouter()
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)

  const [modules, setModules] = useState<ModuleSummary[]>([])
  const [selectedModuleId, setSelectedModuleId] = useState<string>('')
  const [formValues, setFormValues] = useState<FormValues>({})
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (!token) {
      router.replace('/login')
      return
    }

    getModules()
      .then((data) => {
        setModules(data)
        if (data.length > 0) {
          setSelectedModuleId(data[0].id)
        }
      })
      .catch(() => setModules([]))
  }, [token, router])

  const selectedModule = useMemo(
    () => modules.find((module) => module.id === selectedModuleId),
    [modules, selectedModuleId]
  )

  const properties = selectedModule?.schema?.properties || {}
  const requiredFields = new Set(selectedModule?.schema?.required || [])

  const submitDeployment = async () => {
    if (!selectedModule) {
      return
    }

    setIsSubmitting(true)
    setError(null)

    try {
      const response = await createDeployment(selectedModule.id, formValues)
      router.push(`/deployment/${response.id}`)
    } catch (err: any) {
      setError(err?.response?.data?.message || 'Failed to create deployment.')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <main style={{ maxWidth: 1100, margin: '2rem auto', padding: '0 1rem' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Module Catalog</h1>
        <Link href="/dashboard">Back to Dashboard</Link>
      </header>

      <section style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
        <div style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
          <h2 style={{ marginTop: 0 }}>Modules</h2>
          <select
            value={selectedModuleId}
            onChange={(e) => {
              setSelectedModuleId(e.target.value)
              setFormValues({})
            }}
            style={{ width: '100%', padding: 8, marginBottom: 12 }}
          >
            {modules.map((module) => (
              <option key={module.id} value={module.id}>
                {module.name} v{module.version}
              </option>
            ))}
          </select>

          <p style={{ color: '#555' }}>{selectedModule?.description || 'Select a module to configure inputs.'}</p>
        </div>

        <div style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
          <h2 style={{ marginTop: 0 }}>Deployment Inputs</h2>

          {Object.entries(properties).map(([fieldName, fieldSchema]) => {
            const isRequired = requiredFields.has(fieldName)
            const options = fieldSchema.enum || []

            return (
              <div key={fieldName} style={{ marginBottom: 12 }}>
                <label style={{ display: 'block', marginBottom: 4 }}>
                  {fieldName}
                  {isRequired ? ' *' : ''}
                </label>
                {options.length > 0 ? (
                  <select
                    value={formValues[fieldName] || ''}
                    onChange={(e) => setFormValues((current) => ({ ...current, [fieldName]: e.target.value }))}
                    style={{ width: '100%', padding: 8 }}
                    required={isRequired}
                  >
                    <option value="">Select</option>
                    {options.map((option) => (
                      <option key={option} value={option}>
                        {option}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    value={formValues[fieldName] || ''}
                    onChange={(e) => setFormValues((current) => ({ ...current, [fieldName]: e.target.value }))}
                    style={{ width: '100%', padding: 8 }}
                    required={isRequired}
                  />
                )}
              </div>
            )
          })}

          {error && <div style={{ color: '#b91c1c', marginBottom: 10 }}>{error}</div>}

          <button onClick={submitDeployment} disabled={isSubmitting || !selectedModule}>
            {isSubmitting ? 'Submitting...' : 'Create Deployment'}
          </button>
        </div>
      </section>
    </main>
  )
}