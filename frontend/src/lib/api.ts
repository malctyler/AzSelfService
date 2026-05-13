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
    properties?: Record<string, { type?: string; enum?: string[]; minLength?: number; pattern?: string }>
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

export async function createDeployment(moduleId: string, inputs: Record<string, unknown>) {
  const response = await apiClient.post<{ id: string; status: string; createdAtUtc: string }>('/api/deployments', {
    moduleId,
    inputs
  })
  return response.data
}

export async function getDeployment(id: string): Promise<DeploymentDetails> {
  const response = await apiClient.get<DeploymentDetails>(`/api/deployments/${id}`)
  return response.data
}

export async function getDeploymentLogs(id: string, sinceId?: number): Promise<DeploymentLog[]> {
  const response = await apiClient.get<DeploymentLog[]>(`/api/deployments/${id}/logs`, {
    params: sinceId ? { sinceId } : undefined
  })
  return response.data
}