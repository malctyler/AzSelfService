import Link from 'next/link'
import { useEffect, useMemo, useState } from 'react'
import { useRouter } from 'next/router'
import {
  createDeployment,
  deprecateModule,
  getManagedResources,
  getModules,
  publishModule,
  registerModule,
  checkStorageAccountNameAvailability,
  validateKeyVault,
  type ManagedResourceSummary,
  type ModuleSummary
} from '../lib/api'
import { useAuthStore } from '../store/auth'

type FormValues = Record<string, string>

type ModuleProperty = NonNullable<ModuleSummary['schema']['properties']>[string]

function getErrorMessage(error: unknown): string {
  if (typeof error === 'object' && error && 'response' in error) {
    const response = error.response
    if (typeof response === 'object' && response && 'data' in response) {
      const data = response.data

      if (typeof data === 'string' && data.trim().length > 0) {
        try {
          const parsed = JSON.parse(data) as { message?: string; title?: string }
          if (typeof parsed.message === 'string' && parsed.message.trim().length > 0) {
            return parsed.message
          }

          if (typeof parsed.title === 'string' && parsed.title.trim().length > 0) {
            return parsed.title
          }
        } catch {
          return data
        }

        return data
      }

      if (typeof data === 'object' && data) {
        if ('message' in data && typeof data.message === 'string' && data.message.trim().length > 0) {
          return data.message
        }

        if ('title' in data && typeof data.title === 'string' && data.title.trim().length > 0) {
          return data.title
        }

        if ('errors' in data && typeof data.errors === 'object' && data.errors) {
          const errors = data.errors as Record<string, unknown>
          for (const value of Object.values(errors)) {
            if (Array.isArray(value) && value.length > 0 && typeof value[0] === 'string') {
              return value[0]
            }

            if (typeof value === 'string' && value.trim().length > 0) {
              return value
            }
          }
        }

        try {
          const serialized = JSON.stringify(data)
          if (serialized && serialized !== '{}') {
            return serialized
          }
        } catch {
          // ignore serialization issues
        }
      }

      if ('statusText' in response && typeof response.statusText === 'string' && response.statusText.trim().length > 0) {
        return response.statusText
      }
    }
  }

  return 'Request failed.'
}

function parseJsonObjectInput(value: string): Record<string, string> {
  const trimmed = value.trim()

  if (trimmed.length === 0) {
    return {}
  }

  const parsed = JSON.parse(trimmed)
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error('Value must be a JSON object.')
  }

  const result: Record<string, string> = {}
  for (const [key, entry] of Object.entries(parsed as Record<string, unknown>)) {
    if (typeof entry !== 'string') {
      throw new Error('All tag values must be strings.')
    }

    result[key] = entry
  }

  return result
}

function validateScalarInput(fieldName: string, fieldSchema: ModuleProperty, value: string, isRequired: boolean): string | null {
  const trimmed = value.trim()

  if (isRequired && trimmed.length === 0) {
    return `${fieldName} is required.`
  }

  if (trimmed.length === 0) {
    return null
  }

  if (fieldSchema.minLength && trimmed.length < fieldSchema.minLength) {
    return fieldSchema.validationMessage || fieldSchema.description || `${fieldName} must be at least ${fieldSchema.minLength} characters.`
  }

  if (fieldSchema.pattern && !(new RegExp(fieldSchema.pattern).test(trimmed))) {
    return fieldSchema.validationMessage || fieldSchema.description || `${fieldName} is invalid.`
  }

  return null
}

export default function ModulesPage() {
  const router = useRouter()
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)
  const user = useAuthStore((state) => state.user)

  const [modules, setModules] = useState<ModuleSummary[]>([])
  const [managedResources, setManagedResources] = useState<ManagedResourceSummary[]>([])
  const [selectedModuleId, setSelectedModuleId] = useState<string>('')
  const [formValues, setFormValues] = useState<FormValues>({})
  const [modulePath, setModulePath] = useState<string>('terraform-modules/resource-group')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [submitStatusMessage, setSubmitStatusMessage] = useState<string | null>(null)
  const [isAdminSubmitting, setIsAdminSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [adminMessage, setAdminMessage] = useState<string | null>(null)
  const [isCheckingStorageName, setIsCheckingStorageName] = useState(false)
  const [storageNameCheckResult, setStorageNameCheckResult] = useState<{ isAvailable: boolean; message?: string } | null>(null)
  const [storageNameCheckError, setStorageNameCheckError] = useState<string | null>(null)
  const [isCheckingKeyVault, setIsCheckingKeyVault] = useState(false)
  const [keyVaultCheckResult, setKeyVaultCheckResult] = useState<{ isValid: boolean; message?: string } | null>(null)
  const [keyVaultCheckError, setKeyVaultCheckError] = useState<string | null>(null)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  useEffect(() => {
    if (!token) {
      router.replace('/login')
      return
    }

    Promise.all([getModules(), getManagedResources()])
      .then(([moduleData, managedResourceData]) => {
        setModules(moduleData)
        setManagedResources(managedResourceData)

        // If ?resourceGroup=1 is present, pre-select the resource group module
        const rgParam = router.query.resourceGroup
        if (rgParam && moduleData.length > 0) {
          const rgModule = moduleData.find((m) => m.name.toLowerCase().includes('resource group'))
          if (rgModule) {
            setSelectedModuleId(rgModule.id)
            return
          }
        }

        if (moduleData.length > 0) {
          setSelectedModuleId(moduleData[0].id)
        }
      })
      .catch(() => {
        setModules([])
        setManagedResources([])
      })
  }, [token, router])

  const selectedModule = useMemo(
    () => modules.find((module) => module.id === selectedModuleId),
    [modules, selectedModuleId]
  )
  const isAdminUser = user?.role?.toLowerCase() === 'admin'
  const normalizedModuleName = (selectedModule?.name ?? '').toLowerCase().replace(/[^a-z0-9]/g, '')
  const normalizedTerraformPath = (selectedModule?.terraformPath ?? '').toLowerCase().replace(/[^a-z0-9]/g, '')
  const normalizedDescription = (selectedModule?.description ?? '').toLowerCase().replace(/[^a-z0-9]/g, '')
  const isStorageAccountModule =
    normalizedModuleName.includes('storageaccount') ||
    normalizedTerraformPath.includes('storageaccount') ||
    normalizedDescription.includes('storageaccount')
  const isKeyVaultModule =
    normalizedModuleName.includes('keyvault') ||
    normalizedTerraformPath.includes('keyvault') ||
    normalizedDescription.includes('keyvault')

  const properties = selectedModule?.schema?.properties || {}
  const requiredFields = new Set(selectedModule?.schema?.required || [])
  const hasResourceGroupField = Object.prototype.hasOwnProperty.call(properties, 'resource_group_name')
  const hasTenantIdField = Object.prototype.hasOwnProperty.call(properties, 'tenant_id')
  const availableResourceGroupNames = useMemo(() => {
    const uniqueNames = new Set(
      managedResources
        .filter(
          (resource) =>
            resource.moduleName?.toLowerCase() === 'resource-group' &&
            resource.status?.toLowerCase() === 'succeeded' &&
            resource.resourceName?.trim().length > 0
        )
        .map((resource) => resource.resourceName.trim())
    )

    return Array.from(uniqueNames).sort((a, b) => a.localeCompare(b))
  }, [managedResources])

  const checkStorageNameAvailability = async () => {
    const storageName = formValues.name?.trim()
    if (!storageName) {
      setStorageNameCheckError('Please enter a storage account name first.')
      setStorageNameCheckResult(null)
      return
    }

    const validationError = validateScalarInput('name', properties.name, storageName, true)
    if (validationError) {
      setStorageNameCheckError(validationError)
      setStorageNameCheckResult(null)
      return
    }

    setIsCheckingStorageName(true)
    setStorageNameCheckError(null)
    setStorageNameCheckResult(null)

    try {
      const result = await checkStorageAccountNameAvailability(storageName)
      setStorageNameCheckResult(result)
    } catch (err) {
      const message = getErrorMessage(err)
      setStorageNameCheckError(message !== 'Request failed.' ? message : 'Failed to check storage account name availability.')
    } finally {
      setIsCheckingStorageName(false)
    }
  }

  const checkKeyVaultAvailability = async () => {
    const name = formValues.name?.trim()
    const resourceGroup = formValues.resource_group_name?.trim() || ''
    const location = formValues.location?.trim() || ''

    if (!name) {
      setKeyVaultCheckError('Please enter a Key Vault name first.')
      setKeyVaultCheckResult(null)
      return
    }

    const validationError = validateScalarInput('name', properties.name, name, true)
    if (validationError) {
      setKeyVaultCheckError(validationError)
      setKeyVaultCheckResult(null)
      return
    }

    setIsCheckingKeyVault(true)
    setKeyVaultCheckError(null)
    setKeyVaultCheckResult(null)

    try {
      const result = await validateKeyVault({ name, resourceGroup, location })
      setKeyVaultCheckResult({ isValid: result.isValid, message: result.errorMessage })
    } catch (err) {
      const message = getErrorMessage(err)
      setKeyVaultCheckError(message !== 'Request failed.' ? message : 'Failed to check Key Vault name availability.')
    } finally {
      setIsCheckingKeyVault(false)
    }
  }

  const submitDeployment = async () => {
    if (!selectedModule) {
      return
    }

    setIsSubmitting(true)
    setError(null)
    setSubmitStatusMessage(
      isStorageAccountModule
        ? 'Validating storage account inputs and global name availability...'
        : isKeyVaultModule
        ? 'Validating Key Vault inputs and name availability...'
        : 'Validating inputs and queuing deployment...'
    )

    try {
      const payload: Record<string, unknown> = {}

      for (const [fieldName, fieldSchema] of Object.entries(properties)) {
        const rawValue = formValues[fieldName] ?? ''

        const validationError = validateScalarInput(fieldName, fieldSchema, rawValue, requiredFields.has(fieldName))
        if (validationError) {
          throw new Error(validationError)
        }

        if (fieldSchema.type === 'object') {
          payload[fieldName] = parseJsonObjectInput(rawValue)
          continue
        }

        payload[fieldName] = rawValue
      }

      if (isKeyVaultModule) {
        // Call backend validation before deployment
        const name = formValues.name?.trim()
        const resourceGroup = formValues.resource_group_name?.trim() || ''
        const location = formValues.location?.trim() || ''
        const validation = await validateKeyVault({ name, resourceGroup, location })
        if (!validation.isValid) {
          throw new Error(validation.errorMessage || 'Key Vault validation failed.')
        }
        // Optionally, call deployKeyVault here instead of generic deployment
        // const deployResult = await deployKeyVault({ name, resourceGroup, location })
        // if (!deployResult.success) throw new Error(deployResult.errorMessage || 'Key Vault deployment failed.')
      }

      const response = await createDeployment(selectedModule.id, payload)
      router.push(`/deployment/${response.id}`)
    } catch (err) {
      const apiMessage = getErrorMessage(err)
      if (apiMessage !== 'Request failed.') {
        setError(apiMessage)
      } else if (err instanceof Error) {
        setError(err.message)
      } else {
        setError('Failed to create deployment.')
      }
    } finally {
      setIsSubmitting(false)
      setSubmitStatusMessage(null)
    }
  }

  const refreshModules = async () => {
    const data = await getModules()
    setModules(data)

    if (data.length === 0) {
      setSelectedModuleId('')
      return
    }

    if (!data.some((module) => module.id === selectedModuleId)) {
      setSelectedModuleId(data[0].id)
    }
  }

  const submitRegisterModule = async () => {
    setIsAdminSubmitting(true)
    setAdminMessage(null)

    try {
      const registeredModule = await registerModule(modulePath)
      await refreshModules()
      setSelectedModuleId(registeredModule.id)
      setAdminMessage(`Registered ${registeredModule.name} v${registeredModule.version}.`)
    } catch (err) {
      if (typeof err === 'object' && err && 'response' in err && err.response && typeof err.response === 'object' && 'data' in err.response && err.response.data && typeof err.response.data === 'object' && 'message' in err.response.data) {
        setAdminMessage((err.response.data as { message?: string }).message || 'Failed to register module.')
      } else {
        setAdminMessage('Failed to register module.')
      }
    } finally {
      setIsAdminSubmitting(false)
    }
  }

  const submitDeprecateModule = async () => {
    if (!selectedModule) {
      return
    }

    setIsAdminSubmitting(true)
    setAdminMessage(null)

    try {
      await deprecateModule(selectedModule.id)
      await refreshModules()
      setAdminMessage(`Deprecated ${selectedModule.name} v${selectedModule.version}.`)
    } catch (err) {
      if (typeof err === 'object' && err && 'response' in err && err.response && typeof err.response === 'object' && 'data' in err.response && err.response.data && typeof err.response.data === 'object' && 'message' in err.response.data) {
        setAdminMessage((err.response.data as { message?: string }).message || 'Failed to deprecate module.')
      } else {
        setAdminMessage('Failed to deprecate module.')
      }
    } finally {
      setIsAdminSubmitting(false)
    }
  }

  const submitPublishModule = async () => {
    if (!selectedModule) {
      return
    }

    setIsAdminSubmitting(true)
    setAdminMessage(null)

    try {
      await publishModule(selectedModule.id)
      await refreshModules()
      setAdminMessage(`Published ${selectedModule.name} v${selectedModule.version}.`)
    } catch (err) {
      if (typeof err === 'object' && err && 'response' in err && err.response && typeof err.response === 'object' && 'data' in err.response && err.response.data && typeof err.response.data === 'object' && 'message' in err.response.data) {
        setAdminMessage((err.response.data as { message?: string }).message || 'Failed to publish module.')
      } else {
        setAdminMessage('Failed to publish module.')
      }
    } finally {
      setIsAdminSubmitting(false)
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
            const isObjectField = fieldSchema.type === 'object'
            const helpText = fieldSchema.validationMessage || fieldSchema.description

            return (
              <div key={fieldName} style={{ marginBottom: 12 }}>
                <label style={{ display: 'block', marginBottom: 4 }}>
                  {fieldName}
                  {isRequired ? ' *' : ''}
                </label>
                {hasResourceGroupField && fieldName === 'resource_group_name' ? (
                  <select
                    value={formValues[fieldName] || ''}
                    onChange={(e) => setFormValues((current) => ({ ...current, [fieldName]: e.target.value }))}
                    style={{ width: '100%', padding: 8 }}
                    required={isRequired}
                  >
                    <option value="">
                      {availableResourceGroupNames.length > 0
                        ? 'Select existing resource group'
                        : 'No managed resource groups found'}
                    </option>
                    {availableResourceGroupNames.map((resourceGroupName) => (
                      <option key={resourceGroupName} value={resourceGroupName}>
                        {resourceGroupName}
                      </option>
                    ))}
                  </select>
                ) : isObjectField ? (
                  <textarea
                    value={formValues[fieldName] || ''}
                    onChange={(e) => setFormValues((current) => ({ ...current, [fieldName]: e.target.value }))}
                    style={{ width: '100%', padding: 8, minHeight: 120, fontFamily: 'monospace' }}
                    placeholder='{"owner":"platform","costCenter":"1234"}'
                    required={isRequired}
                  />
                ) : options.length > 0 ? (
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
                  <>
                    <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
                      <input
                        value={formValues[fieldName] || ''}
                        onChange={(e) => setFormValues((current) => ({ ...current, [fieldName]: e.target.value }))}
                        style={{ flex: 1, padding: 8 }}
                        required={isRequired}
                        minLength={fieldSchema.minLength}
                        pattern={fieldSchema.pattern}
                        title={helpText}
                        spellCheck={false}
                        autoCapitalize="none"
                      />
                      {hasResourceGroupField && fieldName === 'name' && (
                        <button
                          type="button"
                          onClick={hasTenantIdField ? checkKeyVaultAvailability : checkStorageNameAvailability}
                          disabled={(hasTenantIdField ? isCheckingKeyVault : isCheckingStorageName) || !formValues.name?.trim()}
                          style={{
                            padding: '8px 12px',
                            whiteSpace: 'nowrap',
                            backgroundColor: '#f0f0f0',
                            border: '1px solid #999',
                            borderRadius: 4,
                            cursor: ((hasTenantIdField ? isCheckingKeyVault : isCheckingStorageName) || !formValues.name?.trim()) ? 'not-allowed' : 'pointer'
                          }}
                        >
                          {(hasTenantIdField ? isCheckingKeyVault : isCheckingStorageName) ? 'Checking...' : 'Check Availability'}
                        </button>
                      )}
                    </div>
                    {isStorageAccountModule && fieldName === 'name' && (
                      <>
                        {storageNameCheckResult && (
                          <div style={{
                            marginTop: 8,
                            padding: 8,
                            borderRadius: 4,
                            backgroundColor: storageNameCheckResult.isAvailable ? '#dcfce7' : '#fee2e2',
                            color: storageNameCheckResult.isAvailable ? '#166534' : '#991b1b',
                            fontSize: 13
                          }}>
                            {storageNameCheckResult.isAvailable
                              ? `✓ "${formValues.name}" is available globally`
                              : `✗ "${formValues.name}" is not available: ${storageNameCheckResult.message || 'Name already taken'}`}
                          </div>
                        )}
                        {storageNameCheckError && (
                          <div style={{
                            marginTop: 8,
                            padding: 8,
                            borderRadius: 4,
                            backgroundColor: '#fef2f2',
                            color: '#991b1b',
                            fontSize: 13
                          }}>
                            Check failed: {storageNameCheckError}
                          </div>
                        )}
                      </>
                    )}
                  </>
                )}
                {helpText && (
                  <div style={{ color: '#666', fontSize: 12, marginTop: 4 }}>
                    {helpText}
                  </div>
                )}
                {(isStorageAccountModule && fieldName === 'name') && (
                  <div style={{ color: '#666', fontSize: 12, marginTop: 6, padding: 8, backgroundColor: '#f9fafb', borderRadius: 4 }}>
                    <strong>💡 Tip:</strong> Use the &quot;Check Availability&quot; button to verify if the name is free globally.
                    Available names check instantly, but taken names may take ~30 seconds to verify (Azure API behavior).
                    You can also submit directly—if the name is taken, the deployment will fail.
                  </div>
                )}
                {(isKeyVaultModule && fieldName === 'name') && (
                  <>
                    {keyVaultCheckResult && (
                      <div style={{
                        marginTop: 8,
                        padding: 8,
                        borderRadius: 4,
                        backgroundColor: keyVaultCheckResult.isValid ? '#dcfce7' : '#fee2e2',
                        color: keyVaultCheckResult.isValid ? '#166534' : '#991b1b',
                        fontSize: 13
                      }}>
                        {keyVaultCheckResult.isValid
                          ? `✓ "${formValues.name}" is available for Key Vault`
                          : `✗ "${formValues.name}" is not available: ${keyVaultCheckResult.message || 'Name already taken'}`}
                      </div>
                    )}
                    {keyVaultCheckError && (
                      <div style={{
                        marginTop: 8,
                        padding: 8,
                        borderRadius: 4,
                        backgroundColor: '#fef2f2',
                        color: '#991b1b',
                        fontSize: 13
                      }}>
                        Check failed: {keyVaultCheckError}
                      </div>
                    )}
                    <div style={{ color: '#666', fontSize: 12, marginTop: 6, padding: 8, backgroundColor: '#f9fafb', borderRadius: 4 }}>
                      <strong>💡 Tip:</strong> Use the &quot;Check Availability&quot; button to verify if the Key Vault name is available and valid. You can also submit directly—if the name is taken or invalid, the deployment will fail.
                    </div>
                  </>
                )}
              </div>
            )
          })}

          {error && <div style={{ color: '#b91c1c', marginBottom: 10 }}>{error}</div>}
          {isSubmitting && submitStatusMessage && (
            <div style={{ color: '#1f2937', marginBottom: 10 }}>{submitStatusMessage}</div>
          )}

          <button onClick={submitDeployment} disabled={isSubmitting || !selectedModule}>
            {isSubmitting ? 'Submitting...' : 'Create Deployment'}
          </button>
        </div>
      </section>

      {isAdminUser && (
        <section style={{ marginTop: 16, border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
          <h2 style={{ marginTop: 0 }}>Admin Module Controls</h2>

          <label style={{ display: 'block', marginBottom: 4 }}>Module Path</label>
          <input
            value={modulePath}
            onChange={(e) => setModulePath(e.target.value)}
            style={{ width: '100%', padding: 8, marginBottom: 10 }}
            placeholder="terraform-modules/resource-group"
          />

          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            <button onClick={submitRegisterModule} disabled={isAdminSubmitting || modulePath.trim().length === 0}>
              Register or Update Module
            </button>
            <button onClick={submitDeprecateModule} disabled={isAdminSubmitting || !selectedModule}>
              Deprecate Selected Module
            </button>
            <button onClick={submitPublishModule} disabled={isAdminSubmitting || !selectedModule}>
              Publish Selected Module
            </button>
          </div>

          {adminMessage && <p style={{ marginBottom: 0 }}>{adminMessage}</p>}
        </section>
      )}
    </main>
  )
}