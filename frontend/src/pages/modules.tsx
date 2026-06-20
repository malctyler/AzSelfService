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
  importDeployment,
  listImportOptions,
  lookupResourceGroup,
  type ArmLookupResult,
  type ImportResourceOption,
  type ManagedResourceSummary,
  type ModuleSummary
} from '../lib/api'
import {
  getVNetDeployments,
  type VNetInfo
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

function parseMultiSelectInput(value: string): string[] {
  return Array.from(
    new Set(
      value
        .split(',')
        .map((entry) => entry.trim())
        .filter((entry) => entry.length > 0)
    )
  )
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
  const [vnetDeployments, setVnetDeployments] = useState<VNetInfo[]>([])
  const [isImportMode, setIsImportMode] = useState(false)
  const [isLookingUp, setIsLookingUp] = useState(false)
  const [lookupResult, setLookupResult] = useState<ArmLookupResult | null>(null)
  const [lookupError, setLookupError] = useState<string | null>(null)
  const [importOptions, setImportOptions] = useState<ImportResourceOption[]>([])
  const [isLoadingImportOptions, setIsLoadingImportOptions] = useState(false)
  const [importOptionsError, setImportOptionsError] = useState<string | null>(null)
  const [isDomainJoinExpanded, setIsDomainJoinExpanded] = useState(false)
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

        // Load VNet deployment details (for Windows Server subnet picker)
        getVNetDeployments().then(setVnetDeployments).catch(() => setVnetDeployments([]))

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
  const isResourceGroupModule =
    normalizedModuleName.includes('resourcegroup') ||
    normalizedTerraformPath.includes('resourcegroup')
  const isNetworkSecurityGroupModule =
    normalizedModuleName.includes('networksecuritygroup') ||
    normalizedTerraformPath.includes('networksecuritygroup')
  const isNetworkSecurityRuleModule =
    normalizedModuleName.includes('networksecurityrule') ||
    normalizedTerraformPath.includes('networksecurityrule')
  const isPublicIpModule =
    normalizedModuleName.includes('publicip') ||
    normalizedTerraformPath.includes('publicip')
  const isLocalNetworkGatewayModule =
    normalizedModuleName.includes('localnetworkgateway') ||
    normalizedTerraformPath.includes('localnetworkgateway')
  const isVirtualNetworkGatewayModule =
    normalizedModuleName.includes('virtualnetworkgateway') ||
    normalizedTerraformPath.includes('virtualnetworkgateway')
  const isVirtualNetworkPeeringModule =
    normalizedModuleName.includes('virtualnetworkpeering') ||
    normalizedTerraformPath.includes('virtualnetworkpeering')
  const isBastionHostModule =
    normalizedModuleName.includes('bastionhost') ||
    normalizedTerraformPath.includes('bastionhost')
  const isSubnetModule =
    normalizedModuleName.includes('subnet') ||
    normalizedTerraformPath.includes('subnet')

  const isImportSupportedModule = isResourceGroupModule || isStorageAccountModule || isKeyVaultModule

  const rawModuleName = (selectedModule?.name ?? '').toLowerCase()
  const rawTerraformPath = (selectedModule?.terraformPath ?? '').toLowerCase()
  const isWindowsServerModule =
    rawModuleName.includes('windows-server') ||
    rawTerraformPath.includes('windows-server')

  const isVirtualNetworkModule =
    rawModuleName.includes('virtual-network') ||
    rawTerraformPath.includes('virtual-network')

  const isImportSupportedModuleFull =
    isImportSupportedModule ||
    isVirtualNetworkModule ||
    isNetworkSecurityGroupModule ||
    isNetworkSecurityRuleModule ||
    isPublicIpModule ||
    isLocalNetworkGatewayModule ||
    isVirtualNetworkGatewayModule ||
    isVirtualNetworkPeeringModule ||
    isBastionHostModule ||
    isSubnetModule
  const isImportOptionModule = isImportSupportedModuleFull && !isResourceGroupModule

  const selectedLocation = formValues['location'] ?? ''
  const parsedSubnetCount = Number.parseInt(formValues['subnet_count'] ?? '1', 10)
  const subnetCount = Number.isFinite(parsedSubnetCount) ? parsedSubnetCount : 1

  // VNets in the currently selected location
  const availableVnetsForLocation = vnetDeployments.filter(
    (v) => v.location === selectedLocation
  )

  const isAutoPopulatedField = (fieldName: string): boolean =>
    (isKeyVaultModule && fieldName === 'tenant_id') ||
    (isWindowsServerModule && fieldName === 'subnet_id')

  const DOMAIN_JOIN_FIELDS = new Set(['domain_name', 'domain_join_username', 'domain_join_password', 'domain_join_ou_path', 'dns_servers'])

  const properties = selectedModule?.schema?.properties || {}
  const requiredFields = new Set(selectedModule?.schema?.required || [])
  const hasResourceGroupField = Object.prototype.hasOwnProperty.call(properties, 'resource_group_name')
  const hasTenantIdField = Object.prototype.hasOwnProperty.call(properties, 'tenant_id')
  const importResourceLabel =
    isStorageAccountModule ? 'Storage Account' :
    isKeyVaultModule ? 'Key Vault' :
    isVirtualNetworkModule ? 'Virtual Network' :
    isNetworkSecurityGroupModule ? 'Network Security Group' :
    isNetworkSecurityRuleModule ? 'Network Security Rule' :
    isPublicIpModule ? 'Public IP' :
    isLocalNetworkGatewayModule ? 'Local Network Gateway' :
    isVirtualNetworkGatewayModule ? 'Virtual Network Gateway' :
    isVirtualNetworkPeeringModule ? 'Virtual Network Peering' :
    isBastionHostModule ? 'Bastion Host' :
    isSubnetModule ? 'Subnet' :
    'Resource'
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

  const lookupCurrentResourceGroup = async () => {
    const name = formValues['name']?.trim()
    if (!name) {
      setLookupError('Enter a resource group name first.')
      setLookupResult(null)
      return
    }

    setIsLookingUp(true)
    setLookupError(null)
    setLookupResult(null)

    try {
      const result = await lookupResourceGroup(name)
      setLookupResult(result)
    } catch (err) {
      setLookupError(getErrorMessage(err))
    } finally {
      setIsLookingUp(false)
    }
  }

  const loadImportOptionsForResourceGroup = async (resourceGroup: string) => {
    if (!selectedModule || !resourceGroup.trim() || !isImportOptionModule) {
      setImportOptions([])
      setImportOptionsError(null)
      return
    }

    setIsLoadingImportOptions(true)
    setImportOptions([])
    setImportOptionsError(null)

    try {
      const options = await listImportOptions(selectedModule.id, resourceGroup)
      setImportOptions(options)
    } catch (err) {
      setImportOptionsError(getErrorMessage(err))
    } finally {
      setIsLoadingImportOptions(false)
    }
  }

  const submitImport = async () => {
    if (!selectedModule) return

    const name = formValues['name']?.trim()
    if (!isVirtualNetworkModule && !name) {
      setError(isResourceGroupModule ? 'Resource group name is required.' : 'A name is required.')
      return
    }

    setIsSubmitting(true)
    setError(null)
    setSubmitStatusMessage('Validating resource and queuing import...')

    try {
      let response
      if (isResourceGroupModule) {
        const environment = formValues['environment']?.trim() || 'dev'
        response = await importDeployment(selectedModule.id, { resourceGroupName: name, environment })
      } else if (isStorageAccountModule) {
        const rg = formValues['resource_group_name']?.trim()
        if (!rg) throw new Error('Please select a resource group.')
        response = await importDeployment(selectedModule.id, { storageAccountName: name, storageAccountResourceGroup: rg })
      } else if (isKeyVaultModule) {
        const rg = formValues['resource_group_name']?.trim()
        if (!rg) throw new Error('Please select a resource group.')
        response = await importDeployment(selectedModule.id, { keyVaultName: name, keyVaultResourceGroup: rg })
      } else if (isVirtualNetworkModule) {
        const rg = formValues['resource_group_name']?.trim()
        const vnetName = formValues['name']?.trim()
        if (!rg) throw new Error('Please select a resource group.')
        if (!vnetName) throw new Error('Please select a virtual network.')
        response = await importDeployment(selectedModule.id, { virtualNetworkName: vnetName, virtualNetworkResourceGroup: rg })
      } else {
        const rg = formValues['resource_group_name']?.trim()
        const resourceName = formValues['name']?.trim()
        const parentResourceName = formValues['__import_parent_name']?.trim()
        if (!rg) throw new Error('Please select a resource group.')
        if (!resourceName) throw new Error(`Please select a ${importResourceLabel.toLowerCase()}.`)
        response = await importDeployment(selectedModule.id, {
          resourceGroupName: rg,
          resourceName,
          parentResourceName
        })
      }
      router.push(`/deployment/${response.id}`)
    } catch (err) {
      const apiMessage = getErrorMessage(err)
      setError(apiMessage !== 'Request failed.' ? apiMessage : 'Failed to queue import.')
    } finally {
      setIsSubmitting(false)
      setSubmitStatusMessage(null)
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
        if (isAutoPopulatedField(fieldName)) {
          continue
        }

        const rawValue = formValues[fieldName] ?? ''

        if (fieldSchema.type === 'array') {
          const hasEnumOptions = (fieldSchema.enum?.length ?? 0) > 0
          if (hasEnumOptions) {
            // multi-select rendered field — values are comma-joined strings
            const selectedValues = parseMultiSelectInput(rawValue)
            if (requiredFields.has(fieldName) && selectedValues.length === 0) {
              throw new Error(`${fieldName} is required.`)
            }
            payload[fieldName] = selectedValues
          } else {
            // complex object array rendered as JSON textarea
            const trimmed = rawValue.trim()
            if (!trimmed || trimmed === '[]') {
              payload[fieldName] = []
            } else {
              try {
                payload[fieldName] = JSON.parse(trimmed)
              } catch {
                throw new Error(`${fieldName} must be a valid JSON array.`)
              }
            }
          }
          continue
        }

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

      // Inject the user-selected subnet_id for Windows Server (excluded from generic loop)
      if (isVirtualNetworkModule) {
        const parseJsonArray = (raw: string): unknown[] => {
          const trimmed = raw.trim()
          if (!trimmed || trimmed === '[]') return []
          const parsed = JSON.parse(trimmed)
          if (!Array.isArray(parsed)) throw new Error('subnets/nsgs must be a JSON array.')
          return parsed
        }
        try {
          payload['subnets'] = parseJsonArray(formValues['subnets'] ?? '')
        } catch {
          throw new Error('subnets must be a valid JSON array.')
        }
        try {
          payload['nsgs'] = parseJsonArray(formValues['nsgs'] ?? '')
        } catch {
          throw new Error('nsgs must be a valid JSON array.')
        }
      }

      // Inject the user-selected subnet_id for Windows Server (excluded from generic loop)
      if (isWindowsServerModule) {
        const subnetId = formValues['subnet_id'] ?? ''
        if (!subnetId) {
          throw new Error('Please select a subnet before deploying.')
        }
        payload['subnet_id'] = subnetId

        // Include domain join fields (excluded from generic loop); omit if domain_name is blank
        const domainName = (formValues['domain_name'] ?? '').trim()
        payload['domain_name'] = domainName
        if (domainName) {
          const joinUsername = (formValues['domain_join_username'] ?? '').trim()
          const joinPassword = (formValues['domain_join_password'] ?? '').trim()
          const dnsRaw = (formValues['dns_servers'] ?? '').trim()
          const dnsServers = dnsRaw ? dnsRaw.split(',').map(s => s.trim()).filter(s => s.length > 0) : []
          if (!joinUsername || !joinPassword) {
            throw new Error('Domain join account username and password are required when a domain name is set.')
          }
          if (dnsServers.length === 0) {
            throw new Error('At least one DC IP address is required in "DC / DNS Server IPs" when a domain name is set.')
          }
          payload['domain_join_username'] = joinUsername
          payload['domain_join_password'] = formValues['domain_join_password'] ?? ''
          payload['domain_join_ou_path'] = (formValues['domain_join_ou_path'] ?? '').trim()
          payload['dns_servers'] = dnsServers
        } else {
          payload['domain_join_username'] = ''
          payload['domain_join_password'] = ''
          payload['domain_join_ou_path'] = ''
          payload['dns_servers'] = []
        }
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
              setIsImportMode(false)
              setLookupResult(null)
              setLookupError(null)
              setImportOptions([])
              setImportOptionsError(null)
              setIsDomainJoinExpanded(false)
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
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
            <h2 style={{ marginTop: 0, marginBottom: 0 }}>
              {isImportMode ? 'Import Existing Resource' : 'Deployment Inputs'}
            </h2>
            {isImportSupportedModuleFull && (
              <button
                type="button"
                onClick={() => {
                  setIsImportMode((prev) => !prev)
                  setError(null)
                  setLookupResult(null)
                  setLookupError(null)
                  setImportOptions([])
                  setImportOptionsError(null)
                }}
                style={{
                  padding: '6px 12px',
                  fontSize: 13,
                  backgroundColor: isImportMode ? '#f3f4f6' : '#eff6ff',
                  border: `1px solid ${isImportMode ? '#9ca3af' : '#3b82f6'}`,
                  borderRadius: 4,
                  cursor: 'pointer',
                  color: isImportMode ? '#374151' : '#1d4ed8'
                }}
              >
                {isImportMode ? '← Back to Create' : 'Import Existing'}
              </button>
            )}
          </div>

          {Object.entries(properties)
            .filter(([fieldName]) => {
              if (isAutoPopulatedField(fieldName)) return false
              if (isWindowsServerModule && DOMAIN_JOIN_FIELDS.has(fieldName)) return false
              if (fieldName === 'subnet_2_service_endpoints' && subnetCount < 2) return false
              if (fieldName === 'subnet_3_service_endpoints' && subnetCount < 3) return false
              if (fieldName === 'subnet_4_service_endpoints' && subnetCount < 4) return false
              // subnets/nsgs for VNet are handled by the custom panel below the field loop
              if (isVirtualNetworkModule && !isImportMode && ['subnets', 'nsgs'].includes(fieldName)) return false
              if (!isImportMode) return true
              if (isImportOptionModule && !['resource_group_name', 'tags'].includes(fieldName)) return false
              if (isResourceGroupModule && ['location', 'tags'].includes(fieldName)) return false
              if (isStorageAccountModule && ['location', 'tags', 'account_tier', 'account_replication_type', 'name'].includes(fieldName)) return false
              if (isKeyVaultModule && ['location', 'tags', 'sku_name', 'name'].includes(fieldName)) return false
              if (isVirtualNetworkModule && ['address_space', 'subnet_count', 'subnet_1_service_endpoints', 'subnet_2_service_endpoints', 'subnet_3_service_endpoints', 'subnet_4_service_endpoints', 'enable_nsg', 'dns_servers', 'name'].includes(fieldName)) return false
              return true
            })
            .map(([fieldName, fieldSchema]) => {
              const isRequired = requiredFields.has(fieldName)
              const options = fieldSchema.enum || []
              const isObjectField = fieldSchema.type === 'object'
              const isArrayField = fieldSchema.type === 'array'
              const selectedArrayValues = isArrayField
                ? parseMultiSelectInput(formValues[fieldName] || '')
                : []
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
                      onChange={(e) => {
                        const rg = e.target.value
                        setFormValues((current) => ({
                          ...current,
                          [fieldName]: rg,
                          ...(isImportMode ? { name: '', __import_parent_name: '' } : {})
                        }))
                        if (isImportMode) { setLookupResult(null); setLookupError(null) }
                        if (isImportMode && isImportOptionModule && rg) {
                          void loadImportOptionsForResourceGroup(rg)
                        } else if (isImportMode && isImportOptionModule) {
                          setImportOptions([])
                          setImportOptionsError(null)
                        }
                      }}
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
                  ) : isArrayField && options.length > 0 ? (
                    <>
                      <select
                        multiple
                        value={selectedArrayValues}
                        onChange={(e) => {
                          const values = Array.from(e.target.selectedOptions).map((option) => option.value)
                          setFormValues((current) => ({ ...current, [fieldName]: values.join(',') }))
                        }}
                        style={{ width: '100%', padding: 8, minHeight: 88 }}
                        required={isRequired}
                      >
                        {options.map((option) => (
                          <option key={option} value={option}>
                            {option}
                          </option>
                        ))}
                      </select>
                      <div style={{ color: '#666', fontSize: 12, marginTop: 4 }}>
                        Select one or more options.
                      </div>
                    </>
                  ) : isArrayField ? (
                    <textarea
                      value={formValues[fieldName] || ''}
                      onChange={(e) => setFormValues((current) => ({ ...current, [fieldName]: e.target.value }))}
                      style={{ width: '100%', padding: 8, minHeight: 100, fontFamily: 'monospace', fontSize: 12 }}
                      placeholder={`[]`}
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
                          onChange={(e) => {
                            setFormValues((current) => ({ ...current, [fieldName]: e.target.value }))
                            if (isImportMode && isResourceGroupModule && fieldName === 'name') {
                              setLookupResult(null)
                              setLookupError(null)
                            }
                          }}
                          type={fieldSchema.sensitive ? 'password' : 'text'}
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
                  {(isWindowsServerModule && fieldName === 'rdp_allowed_cidr') && (
                    <div style={{ color: '#666', fontSize: 12, marginTop: 6, padding: 8, backgroundColor: '#f9fafb', borderRadius: 4 }}>
                      <strong>💡 Note:</strong> If left empty, <code>0.0.0.0/0</code> (allow RDP from anywhere) will be used. For production, restrict this to your IP range, e.g. <code>203.0.113.10/32</code>.
                    </div>
                  )}
                </div>
              )
            })}

          {isVirtualNetworkModule && !isImportMode && (
            <div style={{ marginBottom: 12, border: '1px solid #e0e7ff', borderRadius: 6 }}>
              <div style={{ padding: '10px 12px', backgroundColor: '#eef2ff', borderRadius: '6px 6px 0 0', borderBottom: '1px solid #e0e7ff' }}>
                <strong style={{ fontSize: 14, color: '#3730a3' }}>Explicit Subnets &amp; NSGs (optional)</strong>
                <p style={{ margin: '4px 0 0', fontSize: 12, color: '#4338ca' }}>
                  Leave these empty to use the legacy generated-subnet mode above. Fill them to define named subnets with specific CIDRs and inline NSG rules in the same deployment state.
                </p>
              </div>
              <div style={{ padding: 12 }}>
                <div style={{ marginBottom: 12 }}>
                  <label style={{ display: 'block', marginBottom: 4, fontSize: 14 }}>subnets (JSON array)</label>
                  <textarea
                    value={formValues['subnets'] || ''}
                    onChange={(e) => setFormValues((current) => ({ ...current, subnets: e.target.value }))}
                    style={{ width: '100%', padding: 8, minHeight: 110, fontFamily: 'monospace', fontSize: 12, boxSizing: 'border-box' }}
                    placeholder={`[
  { "name": "MGMT", "address_prefix": "10.1.1.0/24", "service_endpoints": [], "network_security_group_name": "my-mgmt-nsg" },
  { "name": "DMZ",  "address_prefix": "10.1.2.0/24", "service_endpoints": [], "network_security_group_name": "" }
]`}
                    spellCheck={false}
                  />
                  <div style={{ color: '#6b7280', fontSize: 12, marginTop: 4 }}>
                    Each object: <code>name</code>, <code>address_prefix</code>, optionally <code>network_security_group_name</code> (must match an entry in nsgs below) and <code>service_endpoints</code>.
                  </div>
                </div>
                <div style={{ marginBottom: 4 }}>
                  <label style={{ display: 'block', marginBottom: 4, fontSize: 14 }}>nsgs (JSON array)</label>
                  <textarea
                    value={formValues['nsgs'] || ''}
                    onChange={(e) => setFormValues((current) => ({ ...current, nsgs: e.target.value }))}
                    style={{ width: '100%', padding: 8, minHeight: 140, fontFamily: 'monospace', fontSize: 12, boxSizing: 'border-box' }}
                    placeholder={`[
  {
    "name": "my-mgmt-nsg",
    "tags": {},
    "security_rules": [
      {
        "name": "AllowRDP", "priority": 1000, "direction": "Inbound", "access": "Allow", "protocol": "Tcp",
        "source_port_range": "*", "destination_port_range": "3389",
        "source_address_prefix": "10.0.0.0/8", "destination_address_prefix": "*"
      }
    ]
  }
]`}
                    spellCheck={false}
                  />
                  <div style={{ color: '#6b7280', fontSize: 12, marginTop: 4 }}>
                    Each object: <code>name</code>, <code>security_rules</code> (array), optionally <code>tags</code>. Rule fields match the azurerm_network_security_group resource.
                  </div>
                </div>
              </div>
            </div>
          )}

          {isWindowsServerModule && (
            <div style={{ marginBottom: 12 }}>
              <label style={{ display: 'block', marginBottom: 4 }}>
                Subnet *
              </label>
              {!selectedLocation ? (
                <div style={{ color: '#6b7280', fontSize: 13, padding: '8px', backgroundColor: '#f9fafb', borderRadius: 4 }}>
                  Select a region above to see available subnets.
                </div>
              ) : availableVnetsForLocation.length === 0 ? (
                <div style={{ color: '#991b1b', fontSize: 13, padding: '8px', backgroundColor: '#fef2f2', borderRadius: 4, border: '1px solid #fca5a5' }}>
                  No virtual networks found in <strong>{selectedLocation}</strong>. Deploy a Virtual Network module in this region first before provisioning a Windows Server.
                </div>
              ) : (
                <select
                  value={formValues['subnet_id'] || ''}
                  onChange={(e) => setFormValues((current) => ({ ...current, subnet_id: e.target.value }))}
                  style={{ width: '100%', padding: 8 }}
                  required
                >
                  <option value="">Select a subnet…</option>
                  {availableVnetsForLocation.map((v) => (
                    <option key={v.subnetId} value={v.subnetId}>
                      {v.vnetName} › {v.subnetName}
                    </option>
                  ))}
                </select>
              )}
            </div>
          )}

          {isWindowsServerModule && Object.keys(properties).some(k => DOMAIN_JOIN_FIELDS.has(k)) && (
            <div style={{ marginBottom: 12, border: '1px solid #e5e7eb', borderRadius: 6 }}>
              <button
                type="button"
                onClick={() => setIsDomainJoinExpanded(prev => !prev)}
                style={{
                  width: '100%', display: 'flex', justifyContent: 'space-between', alignItems: 'center',
                  padding: '10px 12px', background: '#f9fafb', border: 'none', borderRadius: isDomainJoinExpanded ? '6px 6px 0 0' : 6,
                  cursor: 'pointer', fontWeight: 500, fontSize: 14, color: '#374151',
                  borderBottom: isDomainJoinExpanded ? '1px solid #e5e7eb' : 'none'
                }}
              >
                <span>Domain Join (Optional)</span>
                <span style={{ fontSize: 12, color: '#6b7280' }}>{isDomainJoinExpanded ? '▲ collapse' : '▼ expand'}</span>
              </button>
              {isDomainJoinExpanded && (
                <div style={{ padding: '12px 12px 4px' }}>
                  {(['domain_name', 'domain_join_username', 'domain_join_password', 'domain_join_ou_path', 'dns_servers'] as const)
                    .filter(fieldName => Object.prototype.hasOwnProperty.call(properties, fieldName))
                    .map(fieldName => {
                      const fieldSchema = properties[fieldName]
                      const domainName = formValues['domain_name']?.trim() ?? ''
                      if (fieldName !== 'domain_name' && !domainName) return null
                      const helpText = fieldSchema.validationMessage || fieldSchema.description
                      return (
                        <div key={fieldName} style={{ marginBottom: 12 }}>
                          <label style={{ display: 'block', marginBottom: 4, fontSize: 14 }}>
                            {fieldName === 'domain_name' ? 'Domain FQDN' :
                             fieldName === 'domain_join_username' ? 'Join Account' :
                             fieldName === 'domain_join_password' ? 'Join Account Password' :
                             fieldName === 'dns_servers' ? 'DC / DNS Server IPs *' :
                             'Target OU (optional)'}
                          </label>
                          <input
                            value={formValues[fieldName] || ''}
                            onChange={(e) => setFormValues(current => ({ ...current, [fieldName]: e.target.value }))}
                            type={fieldSchema.sensitive ? 'password' : 'text'}
                            style={{ width: '100%', padding: 8, boxSizing: 'border-box' }}
                            placeholder={
                              fieldName === 'domain_name' ? 'corp.example.com' :
                              fieldName === 'domain_join_username' ? 'CORP\\svc-domainjoin' :
                              fieldName === 'domain_join_password' ? '' :
                              fieldName === 'dns_servers' ? '10.0.0.4' :
                              'OU=Servers,DC=corp,DC=example,DC=com'
                            }
                            spellCheck={false}
                            autoCapitalize="none"
                          />
                          {helpText && (
                            <div style={{ color: '#6b7280', fontSize: 12, marginTop: 4 }}>{helpText}</div>
                          )}
                        </div>
                      )
                    })}
                </div>
              )}
            </div>
          )}

          {error && <div style={{ color: '#b91c1c', marginBottom: 10 }}>{error}</div>}
          {isSubmitting && submitStatusMessage && (
            <div style={{ color: '#1f2937', marginBottom: 10 }}>{submitStatusMessage}</div>
          )}

          {isImportMode && isImportSupportedModuleFull ? (
            <>
              {isImportOptionModule ? (
                <div style={{ marginBottom: 12 }}>
                  <label style={{ display: 'block', marginBottom: 4 }}>{importResourceLabel} *</label>
                  {!formValues['resource_group_name'] ? (
                    <div style={{ padding: 8, fontSize: 13, color: '#6b7280', backgroundColor: '#f9fafb', borderRadius: 4 }}>
                      Select a resource group above to see available {importResourceLabel.toLowerCase()} options.
                    </div>
                  ) : isLoadingImportOptions ? (
                    <div style={{ padding: 8, fontSize: 13, color: '#1d4ed8' }}>Loading {importResourceLabel.toLowerCase()} options...</div>
                  ) : importOptionsError ? (
                    <div style={{ padding: 8, fontSize: 13, color: '#991b1b', backgroundColor: '#fef2f2', borderRadius: 4 }}>{importOptionsError}</div>
                  ) : importOptions.length === 0 ? (
                    <div style={{ padding: 8, fontSize: 13, color: '#92400e', backgroundColor: '#fffbeb', borderRadius: 4 }}>
                      No unmanaged {importResourceLabel.toLowerCase()} resources found in <strong>{formValues['resource_group_name']}</strong>.
                    </div>
                  ) : (
                    <>
                      <select
                        value={formValues['name'] || ''}
                        onChange={(e) => {
                          const selected = importOptions.find((option) => option.name === e.target.value) ?? null
                          setFormValues((current) => ({
                            ...current,
                            name: e.target.value,
                            __import_parent_name: selected?.parentName ?? ''
                          }))
                          setLookupResult(selected ? { resourceId: selected.resourceId, location: selected.location, existingTags: selected.existingTags } : null)
                          setLookupError(null)
                        }}
                        style={{ width: '100%', padding: 8 }}
                      >
                        <option value="">Select a {importResourceLabel.toLowerCase()}…</option>
                        {importOptions.map((option) => (
                          <option key={option.resourceId} value={option.name}>
                            {option.parentName ? `${option.parentName} › ` : ''}{option.name}{option.summary ? ` (${option.summary})` : ''}
                          </option>
                        ))}
                      </select>
                      {formValues['name'] && (() => {
                        const selected = importOptions.find((option) => option.name === formValues['name'])
                        if (!selected) return null
                        return (
                          <div style={{ marginTop: 8, padding: 8, backgroundColor: '#f0f9ff', borderRadius: 4, fontSize: 12, color: '#0369a1' }}>
                            {selected.parentName ? <><strong>Parent:</strong> {selected.parentName} &nbsp;·&nbsp;</> : null}
                            <strong>Summary:</strong> {selected.summary || 'No additional details'}
                          </div>
                        )
                      })()}
                    </>
                  )}
                </div>
              ) : (
                <div style={{ marginBottom: 12 }}>
                  <button
                    type="button"
                    onClick={lookupCurrentResourceGroup}
                    disabled={isLookingUp || !formValues['name']?.trim()}
                    style={{
                      padding: '8px 14px',
                      backgroundColor: '#f0fdf4',
                      border: '1px solid #86efac',
                      borderRadius: 4,
                      cursor: (isLookingUp || !formValues['name']?.trim()) ? 'not-allowed' : 'pointer',
                      color: '#166534'
                    }}
                  >
                    {isLookingUp ? 'Looking up...' : 'Verify Resource Group in Azure'}
                  </button>
                  {lookupError && (
                    <div style={{ marginTop: 8, padding: 8, borderRadius: 4, backgroundColor: '#fef2f2', color: '#991b1b', fontSize: 13 }}>
                      {lookupError}
                    </div>
                  )}
                </div>
              )}

              {lookupResult && (
                <div style={{ marginBottom: 12, padding: 12, borderRadius: 4, backgroundColor: '#f0fdf4', border: '1px solid #86efac' }}>
                  <div style={{ fontWeight: 600, color: '#166534', marginBottom: 6 }}>✓ {isResourceGroupModule ? 'Resource group' : importResourceLabel} found</div>
                  <div style={{ fontSize: 13, color: '#374151', marginBottom: 4 }}>
                    <strong>Location:</strong> {lookupResult.location}
                  </div>
                  <div style={{ fontSize: 13, color: '#374151', marginBottom: 4 }}>
                    <strong>Resource ID:</strong>{' '}
                    <span style={{ fontFamily: 'monospace', wordBreak: 'break-all' }}>{lookupResult.resourceId}</span>
                  </div>
                  {Object.keys(lookupResult.existingTags).length > 0 ? (
                    <div style={{ fontSize: 13, color: '#374151' }}>
                      <strong>Existing tags (will be preserved):</strong>
                      <div style={{ marginTop: 4, display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                        {Object.entries(lookupResult.existingTags).map(([k, v]) => (
                          <span key={k} style={{ padding: '2px 8px', backgroundColor: '#dcfce7', borderRadius: 12, fontSize: 12 }}>
                            {k}: {v}
                          </span>
                        ))}
                      </div>
                    </div>
                  ) : (
                    <div style={{ fontSize: 13, color: '#6b7280' }}>No existing custom tags.</div>
                  )}
                </div>
              )}

              <div style={{ marginBottom: 12, padding: 8, backgroundColor: '#fffbeb', borderRadius: 4, border: '1px solid #fcd34d', fontSize: 12, color: '#92400e' }}>
                <strong>⚠️ Note:</strong> Location and existing tags will be read directly from Azure.
                {isResourceGroupModule && <> The portal will add <em>ManagedBy</em>, <em>Environment</em>, and <em>CreatedAt</em> tags after import.</>}
                {isStorageAccountModule && <> The portal will add a <em>ManagedBy</em> tag after import. Tier and replication type will be read from the existing account.</>}
                {isVirtualNetworkModule && <> Subnets, associated NSGs, and subnet NSG associations are captured from Azure into the same deployment state.</>}
              </div>

              <button
                onClick={submitImport}
                disabled={
                  isSubmitting ||
                  !selectedModule ||
                  (isImportOptionModule && (!formValues['resource_group_name']?.trim() || !formValues['name']?.trim())) ||
                  (!isImportOptionModule && !formValues['name']?.trim())
                }
              >
                {isSubmitting
                  ? 'Importing...'
                  : `Import ${isResourceGroupModule ? 'Resource Group' : importResourceLabel}`}
              </button>
            </>
          ) : (
            <button onClick={submitDeployment} disabled={
              isSubmitting ||
              !selectedModule ||
              (isWindowsServerModule && !formValues['subnet_id'])
            }>
              {isSubmitting ? 'Submitting...' : 'Create Deployment'}
            </button>
          )}
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