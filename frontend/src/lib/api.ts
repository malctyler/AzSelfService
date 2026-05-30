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
    }>
    required?: string[]
  }
  uiSchema?: unknown
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