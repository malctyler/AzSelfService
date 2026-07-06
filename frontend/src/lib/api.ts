import axios from 'axios'

const apiClient = axios.create({
  baseURL: process.env.NEXT_PUBLIC_API_BASE_URL || 'http://localhost:5000'
})

apiClient.interceptors.request.use((config) => {
  if (typeof window !== 'undefined') {
    const token = localStorage.getItem('azselfservice_token')
    if (token) {
      config.headers.Authorization = `Bearer ${token}`
    }
  }
  return config
})

export type KeyVaultValidationResponse = {
  isValid: boolean
  errorMessage?: string
}

export type KeyVaultDeployResponse = {
  success: boolean
  errorMessage?: string
}

export async function validateKeyVault(request: { name: string; resourceGroup: string; location: string }): Promise<KeyVaultValidationResponse> {
  const response = await apiClient.post<KeyVaultValidationResponse>('/api/keyvault/validate', request)
  return response.data
}

export async function deployKeyVault(request: { name: string; resourceGroup: string; location: string }): Promise<KeyVaultDeployResponse> {
  const response = await apiClient.post<KeyVaultDeployResponse>('/api/keyvault/deploy', request)
  return response.data
}

export type AuthUser = {
  userId: string
  customerId: string
  username: string
  role: string
  email?: string
}

export type LoginResponse = {
  token: string
  expiresAtUtc: string
  user: AuthUser
}

export type ModuleSummary = {
  id: string
  name: string
  version: string
  terraformPath: string
  description?: string
  isPublished: boolean
  isDeprecated: boolean
  schema: {
    type?: string
    properties?: Record<string, {
      type?: string
      enum?: string[]
      minLength?: number
      pattern?: string
      description?: string
      validationMessage?: string
      sensitive?: boolean
    }>
    required?: string[]
  }
  uiSchema?: unknown
}

export type AllowedRegion = {
  code: string
  sortOrder: number
}

export async function getAllowedRegions(): Promise<AllowedRegion[]> {
  const response = await apiClient.get<AllowedRegion[]>('/api/admin/regions')
  return response.data
}

export async function updateAllowedRegions(codes: string[]): Promise<AllowedRegion[]> {
  const response = await apiClient.put<AllowedRegion[]>('/api/admin/regions', { codes })
  return response.data
}

export type DeploymentDetails = {
  id: string
  moduleId: string
  moduleName: string
  moduleVersion: string
  status: string
  errorMessage?: string
  retryCount: number
  terraformStatePath?: string
  createdAtUtc: string
  updatedAtUtc: string
  completedAtUtc?: string
  inputs: Record<string, unknown>
  outputs?: Record<string, unknown>
}

export type DeploymentLog = {
  id: number
  timestampUtc: string
  level: string
  message: string
  context?: unknown
}

export type ManagedResourceSummary = {
  deploymentId: string
  moduleId: string
  moduleName: string
  moduleVersion: string
  status: string
  resourceName: string
  resourceLocation: string
  resourceId: string
  terraformStatePath?: string
  createdAtUtc: string
  updatedAtUtc: string
  completedAtUtc?: string
}

export type OnboardCustomerRequest = {
  customerName: string
  subscriptionId: string
  tenantId: string
  spClientId: string
  spClientSecret: string
  username: string
  password: string
  email?: string
  spClientIdSecretRef?: string
  spClientSecretSecretRef?: string
  spTenantIdSecretRef?: string
  spSubscriptionIdSecretRef?: string
}

export type OnboardCustomerResponse = {
  customerId: string
  userId: string
  username: string
  role: string
  createdAtUtc: string
  spClientSecretSecretRefMasked: string
}

export type AdminCustomerSummary = {
  customerId: string
  customerName: string
  subscriptionId: string
  tenantId: string
  isActive: boolean
  username?: string
  email?: string
  spClientIdSecretRef?: string
  spClientSecretSecretRefMasked?: string
  spTenantIdSecretRef?: string
  spSubscriptionIdSecretRef?: string
  updatedAtUtc: string
}

export type UpdateCustomerRequest = {
  customerName: string
  subscriptionId: string
  tenantId: string
  isActive: boolean
  email?: string
  spClientId?: string
  spClientSecret?: string
  spClientIdSecretRef?: string
  spClientSecretSecretRef?: string
  spTenantIdSecretRef?: string
  spSubscriptionIdSecretRef?: string
}

export async function login(username: string, password: string): Promise<LoginResponse> {
  const response = await apiClient.post<LoginResponse>('/api/auth/login', { username, password })
  return response.data
}

export async function getModules(): Promise<ModuleSummary[]> {
  const response = await apiClient.get<ModuleSummary[]>('/api/modules')
  return response.data
}

export async function getModuleById(id: string): Promise<ModuleSummary> {
  const response = await apiClient.get<ModuleSummary>(`/api/modules/${id}`)
  return response.data
}

export async function registerModule(modulePath: string): Promise<ModuleSummary> {
  const response = await apiClient.post<ModuleSummary>('/api/admin/modules/register', {
    modulePath
  })
  return response.data
}

export async function getAdminModules(): Promise<ModuleSummary[]> {
  const response = await apiClient.get<ModuleSummary[]>('/api/admin/modules')
  return response.data
}

export async function publishModule(id: string): Promise<ModuleSummary> {
  const response = await apiClient.post<ModuleSummary>(`/api/admin/modules/${id}/publish`)
  return response.data
}

export async function deprecateModule(id: string): Promise<ModuleSummary> {
  const response = await apiClient.post<ModuleSummary>(`/api/admin/modules/${id}/deprecate`)
  return response.data
}

export async function onboardCustomer(request: OnboardCustomerRequest): Promise<OnboardCustomerResponse> {
  const response = await apiClient.post<OnboardCustomerResponse>('/api/admin/customers/onboard', request)
  return response.data
}

export async function getAdminCustomers(): Promise<AdminCustomerSummary[]> {
  const response = await apiClient.get<AdminCustomerSummary[]>('/api/admin/customers')
  return response.data
}

export async function updateAdminCustomer(customerId: string, request: UpdateCustomerRequest): Promise<AdminCustomerSummary> {
  const response = await apiClient.put<AdminCustomerSummary>(`/api/admin/customers/${customerId}`, request)
  return response.data
}

export async function deleteAdminCustomer(customerId: string): Promise<void> {
  await apiClient.delete(`/api/admin/customers/${customerId}`)
}

export async function createDeployment(moduleId: string, inputs: Record<string, unknown>) {
  const response = await apiClient.post<{ id: string; status: string; createdAtUtc: string }>('/api/deployments', {
    moduleId,
    inputs
  })
  return response.data
}

export type ArmLookupResult = {
  resourceId: string
  location: string
  existingTags: Record<string, string>
}

export type ImportResourceOption = {
  name: string
  resourceId: string
  location: string
  existingTags: Record<string, string>
  summary: string
  parentName?: string
}

// Keep old name as alias
export type ResourceGroupLookupResult = ArmLookupResult

export async function lookupResourceGroup(name: string): Promise<ArmLookupResult> {
  const response = await apiClient.get<ArmLookupResult>('/api/deployments/lookup-resource-group', {
    params: { name }
  })
  return response.data
}

export async function lookupStorageAccount(name: string, resourceGroup: string): Promise<ArmLookupResult> {
  const response = await apiClient.get<ArmLookupResult>('/api/deployments/lookup-storage-account', {
    params: { name, resourceGroup }
  })
  return response.data
}

export type StorageAccountSummary = {
  name: string
  resourceId: string
  location: string
  existingTags: Record<string, string>
}

export async function listStorageAccounts(resourceGroup: string): Promise<StorageAccountSummary[]> {
  const response = await apiClient.get<StorageAccountSummary[]>('/api/deployments/list-storage-accounts', {
    params: { resourceGroup }
  })
  return response.data
}

export type KeyVaultSummary = {
  name: string
  resourceId: string
  location: string
  existingTags: Record<string, string>
}

export async function listKeyVaults(resourceGroup: string): Promise<KeyVaultSummary[]> {
  const response = await apiClient.get<KeyVaultSummary[]>('/api/deployments/list-key-vaults', {
    params: { resourceGroup }
  })
  return response.data
}

export type VirtualNetworkSubnet = {
  name: string
  subnetId: string
  addressPrefix: string
}

export type VirtualNetworkSummary = {
  name: string
  resourceId: string
  location: string
  addressSpace: string
  subnets: VirtualNetworkSubnet[]
  existingTags: Record<string, string>
}

export async function listVirtualNetworks(resourceGroup: string): Promise<VirtualNetworkSummary[]> {
  const response = await apiClient.get<VirtualNetworkSummary[]>('/api/deployments/list-virtual-networks', {
    params: { resourceGroup }
  })
  return response.data
}

export async function listImportOptions(moduleId: string, resourceGroup: string): Promise<ImportResourceOption[]> {
  const response = await apiClient.get<ImportResourceOption[]>('/api/deployments/import-options', {
    params: { moduleId, resourceGroup }
  })
  return response.data
}

export async function importDeployment(
  moduleId: string,
  payload: {
    resourceName?: string
    parentResourceName?: string
    resourceGroupName?: string
    environment?: string
    storageAccountName?: string
    storageAccountResourceGroup?: string
    keyVaultName?: string
    keyVaultResourceGroup?: string
    virtualNetworkName?: string
    virtualNetworkResourceGroup?: string
  }
) {
  const response = await apiClient.post<{ id: string; status: string; createdAtUtc: string }>('/api/deployments/import', {
    moduleId,
    ...payload
  })
  return response.data
}

export type StorageNameAvailabilityCheckResult = {
  nameChecked: string
  isAvailable: boolean
  message?: string
}

export async function checkStorageAccountNameAvailability(name: string): Promise<StorageNameAvailabilityCheckResult> {
  const response = await apiClient.post<StorageNameAvailabilityCheckResult>('/api/deployments/check-storage-name', {
    name
  })
  return response.data
}

export async function getDeployment(id: string): Promise<DeploymentDetails> {
  const response = await apiClient.get<DeploymentDetails>(`/api/deployments/${id}`)
  return response.data
}

export async function getManagedResources(): Promise<ManagedResourceSummary[]> {
  const response = await apiClient.get<ManagedResourceSummary[]>('/api/deployments')
  return response.data
}

export type VNetInfo = {
  deploymentId: string
  vnetName: string
  location: string
  subnetName: string
  subnetId: string
  addressPrefix?: string
}

export async function getVNetDeployments(): Promise<VNetInfo[]> {
  const summaries = await getManagedResources()
  const vnetSummaries = summaries.filter(
    (s) => s.moduleName === 'virtual-network' && s.status === 'SUCCEEDED'
  )
  const details = await Promise.all(
    vnetSummaries.map((s) => getDeployment(s.deploymentId))
  )
  return details.flatMap((d) => {
    const vnetName = (d.outputs?.vnet_name as { value?: unknown } | undefined)?.value
    const locationRaw = d.inputs?.location
    if (typeof vnetName !== 'string' || typeof locationRaw !== 'string') {
      return []
    }

    const subnetDetails = (d.outputs?.subnet_details as { value?: unknown } | undefined)?.value
    if (Array.isArray(subnetDetails)) {
      return subnetDetails
        .filter((entry): entry is { name?: unknown; id?: unknown; address_prefix?: unknown } => typeof entry === 'object' && entry !== null)
        .filter((entry) => typeof entry.name === 'string' && typeof entry.id === 'string')
        .map((entry) => ({
          deploymentId: d.id,
          vnetName,
          location: locationRaw,
          subnetName: entry.name as string,
          subnetId: entry.id as string,
          addressPrefix: typeof entry.address_prefix === 'string' ? entry.address_prefix : undefined
        }))
    }

    const subnetName = (d.outputs?.subnet_name as { value?: unknown } | undefined)?.value
    const subnetId = (d.outputs?.subnet_id as { value?: unknown } | undefined)?.value
    if (typeof subnetName !== 'string' || typeof subnetId !== 'string') {
      return []
    }

    return [{
      deploymentId: d.id,
      vnetName,
      location: locationRaw,
      subnetName,
      subnetId
    }]
  })
}

export async function getDeploymentLogs(id: string, sinceId?: number): Promise<DeploymentLog[]> {
  const response = await apiClient.get<DeploymentLog[]>(`/api/deployments/${id}/logs`, {
    params: sinceId ? { sinceId } : undefined
  })
  return response.data
}

export async function destroyDeployment(id: string): Promise<{ id: string; status: string; createdAtUtc: string }> {
  const response = await apiClient.post<{ id: string; status: string; createdAtUtc: string }>(`/api/deployments/${id}/destroy`)
  return response.data
}

export async function retryDeployment(id: string, inputs: Record<string, unknown>): Promise<{ id: string; status: string; createdAtUtc: string }> {
  const response = await apiClient.post<{ id: string; status: string; createdAtUtc: string }>(`/api/deployments/${id}/retry`, { inputs })
  return response.data
}

export async function rebuildDeployment(id: string): Promise<{ destroyDeploymentId: string; redeployDeploymentId: string; status: string; createdAtUtc: string }> {
  const response = await apiClient.post<{ destroyDeploymentId: string; redeployDeploymentId: string; status: string; createdAtUtc: string }>(`/api/deployments/${id}/rebuild`)
  return response.data
}

export async function rebuildAllDeployments(): Promise<{ batchId: string; deploymentCount: number; destroyCount: number; redeployCount: number }> {
  const response = await apiClient.post<{ batchId: string; deploymentCount: number; destroyCount: number; redeployCount: number }>('/api/deployments/rebuild-all')
  return response.data
}

export async function deleteFailedDeployment(id: string): Promise<void> {
  await apiClient.delete(`/api/deployments/${id}`)
}

export type StorageAccount = {
  id: string;
  name: string;
  region: string;
  resourceGroup: string;
  createdAt: string;
};

export const getStorageAccounts = async (): Promise<StorageAccount[]> => {
  const response = await axios.get<StorageAccount[]>('/api/storageaccounts');
  return response.data;
};

export const createStorageAccount = async (storageAccount: Omit<StorageAccount, 'id' | 'createdAt'>): Promise<StorageAccount> => {
  const response = await axios.post<StorageAccount>('/api/storageaccounts', storageAccount);
  return response.data;
};

export type SoftwarePackageValidationResponse = {
  isValid: boolean
  packageId?: string
  version?: string
  errors: string[]
}

export type SoftwarePackageCatalogItem = {
  id: string
  scope: string
  customerId?: string
  packageId: string
  version: string
  displayName: string
  publisher: string
  os: string
  architecture: string
  installerType: string
  blobPath: string
  zipSha256: string
  isPublished: boolean
  createdAt: string
  updatedAt: string
}

export async function getSoftwarePackagesForDeployment(scope?: 'platform' | 'customer' | 'all'): Promise<SoftwarePackageCatalogItem[]> {
  const response = await apiClient.get<SoftwarePackageCatalogItem[]>('/api/software-packages', {
    params: {
      scope
    }
  })
  return response.data
}

export type UploadSoftwarePackageRequest = {
  scope: 'platform' | 'customer'
  storageAccountName: string
  containerName: string
  isPublished: boolean
  packageFile: File
  customerId?: string
}

export async function validateSoftwarePackage(packageFile: File): Promise<SoftwarePackageValidationResponse> {
  const formData = new FormData()
  formData.append('PackageFile', packageFile)

  const response = await apiClient.post<SoftwarePackageValidationResponse>('/api/admin/software-packages/validate', formData, {
    headers: {
      'Content-Type': 'multipart/form-data'
    }
  })

  return response.data
}

export async function uploadSoftwarePackage(request: UploadSoftwarePackageRequest): Promise<SoftwarePackageCatalogItem> {
  const formData = new FormData()
  formData.append('scope', request.scope)
  formData.append('storageAccountName', request.storageAccountName)
  formData.append('containerName', request.containerName)
  formData.append('isPublished', request.isPublished ? 'true' : 'false')
  formData.append('PackageFile', request.packageFile)
  if (request.customerId) {
    formData.append('customerId', request.customerId)
  }

  const response = await apiClient.post<SoftwarePackageCatalogItem>('/api/admin/software-packages/upload', formData, {
    headers: {
      'Content-Type': 'multipart/form-data'
    }
  })

  return response.data
}

export type PublishSoftwarePackageRequest = {
  scope: 'platform' | 'customer'
  customerId?: string
  packageId: string
  version: string
  displayName: string
  publisher: string
  os: string
  architecture: string
  installerType: string
  blobPath: string
  zipSha256: string
  manifestJson?: string
  isPublished: boolean
}

export async function publishSoftwarePackage(request: PublishSoftwarePackageRequest): Promise<SoftwarePackageCatalogItem> {
  const response = await apiClient.post<SoftwarePackageCatalogItem>('/api/admin/software-packages/publish', request)
  return response.data
}

export async function getSoftwarePackageCatalog(scope?: 'platform' | 'customer', customerId?: string): Promise<SoftwarePackageCatalogItem[]> {
  const response = await apiClient.get<SoftwarePackageCatalogItem[]>('/api/admin/software-packages', {
    params: {
      scope,
      customerId
    }
  })
  return response.data
}