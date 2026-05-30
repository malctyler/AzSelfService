import Link from 'next/link'
import { useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import {
  deleteAdminCustomer,
  getAdminCustomers,
  onboardCustomer,
  updateAdminCustomer,
  type AdminCustomerSummary,
  type OnboardCustomerRequest,
  type UpdateCustomerRequest
} from '../../lib/api'
import { useAuthStore } from '../../store/auth'

export default function AdminCustomersPage() {
  const router = useRouter()
  const hydrate = useAuthStore((state) => state.hydrate)
  const token = useAuthStore((state) => state.token)
  const user = useAuthStore((state) => state.user)

  const [form, setForm] = useState<OnboardCustomerRequest>({
    customerName: 'Dummy Tenant (Malcolm)',
    subscriptionId: 'dev-subscription-123',
    tenantId: 'dev-tenant-id',
    spClientId: '',
    spClientSecret: '',
    username: 'dummy-malcolm',
    password: 'Test@1234',
    email: 'malcolm@example.com',
    spClientIdSecretRef: '',
    spClientSecretSecretRef: '',
    spTenantIdSecretRef: '',
    spSubscriptionIdSecretRef: ''
  })

  const [message, setMessage] = useState<string | null>(null)
  const [isBusy, setIsBusy] = useState(false)
  const [customers, setCustomers] = useState<AdminCustomerSummary[]>([])
  const [selectedCustomerId, setSelectedCustomerId] = useState<string>('')
  const [updateForm, setUpdateForm] = useState<UpdateCustomerRequest>({
    customerName: '',
    subscriptionId: '',
    tenantId: '',
    isActive: true,
    email: '',
    spClientId: '',
    spClientSecret: '',
    spClientIdSecretRef: '',
    spClientSecretSecretRef: '',
    spTenantIdSecretRef: '',
    spSubscriptionIdSecretRef: ''
  })
  const [updateMessage, setUpdateMessage] = useState<string | null>(null)
  const [isUpdating, setIsUpdating] = useState(false)
  const [isDeleting, setIsDeleting] = useState(false)

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
    }
  }, [token, isAdminUser, router])

  useEffect(() => {
    if (!token || !isAdminUser) {
      return
    }

    getAdminCustomers()
      .then((data) => {
        setCustomers(data)
        if (data.length > 0) {
          setSelectedCustomerId(data[0].customerId)
          const first = data[0]
          setUpdateForm({
            customerName: first.customerName,
            subscriptionId: first.subscriptionId,
            tenantId: first.tenantId,
            isActive: first.isActive,
            email: first.email || '',
            spClientId: '',
            spClientSecret: '',
            spClientIdSecretRef: first.spClientIdSecretRef || '',
            spClientSecretSecretRef: '',
            spTenantIdSecretRef: first.spTenantIdSecretRef || '',
            spSubscriptionIdSecretRef: first.spSubscriptionIdSecretRef || ''
          })
        }
      })
      .catch(() => {
        setCustomers([])
      })
  }, [token, isAdminUser])

  const updateField = (field: keyof OnboardCustomerRequest, value: string) => {
    setForm((current) => ({ ...current, [field]: value }))
  }

  const submit = async () => {
    setIsBusy(true)
    setMessage(null)

    if (!form.spClientId?.trim() || !form.spClientSecret?.trim()) {
      setIsBusy(false)
      setMessage('SP Client ID and SP Client Secret are required for every customer.')
      return
    }

    try {
      const payload = await onboardCustomer(form)
      setMessage(
        `Customer onboarded. User '${payload.username}' created with role '${payload.role}'. Secret ref: ${payload.spClientSecretSecretRefMasked}`
      )
    } catch (err: unknown) {
      if (
        typeof err === 'object' &&
        err &&
        'response' in err &&
        typeof err.response === 'object' &&
        err.response &&
        'data' in err.response &&
        typeof err.response.data === 'object' &&
        err.response.data &&
        'message' in err.response.data &&
        typeof err.response.data.message === 'string'
      ) {
        setMessage(err.response.data.message)
      } else {
        setMessage('Failed to onboard customer.')
      }
    } finally {
      setIsBusy(false)
    }
  }

  const setSelectedCustomer = (customerId: string) => {
    setSelectedCustomerId(customerId)
    const selected = customers.find((x) => x.customerId === customerId)
    if (!selected) {
      return
    }

    setUpdateForm({
      customerName: selected.customerName,
      subscriptionId: selected.subscriptionId,
      tenantId: selected.tenantId,
      isActive: selected.isActive,
      email: selected.email || '',
      spClientId: '',
      spClientSecret: '',
      spClientIdSecretRef: selected.spClientIdSecretRef || '',
      spClientSecretSecretRef: '',
      spTenantIdSecretRef: selected.spTenantIdSecretRef || '',
      spSubscriptionIdSecretRef: selected.spSubscriptionIdSecretRef || ''
    })
    setUpdateMessage(null)
  }

  const updateExistingCustomer = async () => {
    if (!selectedCustomerId) {
      setUpdateMessage('Select a customer first.')
      return
    }

    if ((updateForm.spClientId || updateForm.spClientSecret) && (!updateForm.spClientId?.trim() || !updateForm.spClientSecret?.trim())) {
      setUpdateMessage('To rotate credentials, provide both SP Client ID and SP Client Secret.')
      return
    }

    setIsUpdating(true)
    setUpdateMessage(null)

    try {
      const updated = await updateAdminCustomer(selectedCustomerId, updateForm)
      setCustomers((current) => current.map((c) => (c.customerId === updated.customerId ? updated : c)))
      setUpdateMessage('Customer updated successfully.')
      setUpdateForm((current) => ({ ...current, spClientId: '', spClientSecret: '', spClientSecretSecretRef: '' }))
    } catch (err: unknown) {
      if (
        typeof err === 'object' &&
        err &&
        'response' in err &&
        typeof err.response === 'object' &&
        err.response &&
        'data' in err.response &&
        typeof err.response.data === 'object' &&
        err.response.data &&
        'message' in err.response.data &&
        typeof err.response.data.message === 'string'
      ) {
        setUpdateMessage(err.response.data.message)
      } else {
        setUpdateMessage('Failed to update customer.')
      }
    } finally {
      setIsUpdating(false)
    }
  }

  const deleteExistingCustomer = async () => {
    if (!selectedCustomerId) {
      setUpdateMessage('Select a customer first.')
      return
    }

    const selected = customers.find((x) => x.customerId === selectedCustomerId)
    const label = selected?.customerName || selectedCustomerId
    const confirmed = window.confirm(`Delete customer "${label}"? This will remove the customer and related records.`)
    if (!confirmed) {
      return
    }

    setIsDeleting(true)
    setUpdateMessage(null)

    try {
      await deleteAdminCustomer(selectedCustomerId)
      const remaining = customers.filter((c) => c.customerId !== selectedCustomerId)
      setCustomers(remaining)

      if (remaining.length > 0) {
        setSelectedCustomerId(remaining[0].customerId)
        const first = remaining[0]
        setUpdateForm({
          customerName: first.customerName,
          subscriptionId: first.subscriptionId,
          tenantId: first.tenantId,
          isActive: first.isActive,
          email: first.email || '',
          spClientId: '',
          spClientSecret: '',
          spClientIdSecretRef: first.spClientIdSecretRef || '',
          spClientSecretSecretRef: '',
          spTenantIdSecretRef: first.spTenantIdSecretRef || '',
          spSubscriptionIdSecretRef: first.spSubscriptionIdSecretRef || ''
        })
      } else {
        setSelectedCustomerId('')
        setUpdateForm({
          customerName: '',
          subscriptionId: '',
          tenantId: '',
          isActive: true,
          email: '',
          spClientId: '',
          spClientSecret: '',
          spClientIdSecretRef: '',
          spClientSecretSecretRef: '',
          spTenantIdSecretRef: '',
          spSubscriptionIdSecretRef: ''
        })
      }

      setUpdateMessage('Customer deleted successfully.')
    } catch (err: unknown) {
      if (
        typeof err === 'object' &&
        err &&
        'response' in err &&
        typeof err.response === 'object' &&
        err.response &&
        'data' in err.response &&
        typeof err.response.data === 'object' &&
        err.response.data &&
        'message' in err.response.data &&
        typeof err.response.data.message === 'string'
      ) {
        setUpdateMessage(err.response.data.message)
      } else {
        setUpdateMessage('Failed to delete customer.')
      }
    } finally {
      setIsDeleting(false)
    }
  }

  return (
    <main style={{ maxWidth: 960, margin: '2rem auto', padding: '0 1rem' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Admin Customer Onboarding</h1>
        <Link href="/dashboard">Back to Dashboard</Link>
      </header>

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16 }}>
        <p style={{ color: '#555' }}>
          Use this to onboard your first dummy customer with existing tenant/subscription details.
        </p>

        <label style={{ display: 'block', marginBottom: 4 }}>Customer Name</label>
        <input value={form.customerName} onChange={(e) => updateField('customerName', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

        <label style={{ display: 'block', marginBottom: 4 }}>Subscription ID</label>
        <input value={form.subscriptionId} onChange={(e) => updateField('subscriptionId', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

        <label style={{ display: 'block', marginBottom: 4 }}>Tenant ID</label>
        <input value={form.tenantId} onChange={(e) => updateField('tenantId', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

        <label style={{ display: 'block', marginBottom: 4 }}>Username</label>
        <input value={form.username} onChange={(e) => updateField('username', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

        <label style={{ display: 'block', marginBottom: 4 }}>Password</label>
        <input type="password" value={form.password} onChange={(e) => updateField('password', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

        <label style={{ display: 'block', marginBottom: 4 }}>Email (optional)</label>
        <input value={form.email || ''} onChange={(e) => updateField('email', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

        <div style={{ padding: 10, border: '1px solid #f59e0b', borderRadius: 6, backgroundColor: '#fffbeb', marginBottom: 12, color: '#7c2d12' }}>
          SP App ID and SP Secret values are mandatory. These values are stored in Key Vault during onboarding and only references are saved in customer metadata.
        </div>

        <label style={{ display: 'block', marginBottom: 4 }}>SP Client ID (required)</label>
        <input
          value={form.spClientId}
          onChange={(e) => updateField('spClientId', e.target.value)}
          style={{ width: '100%', padding: 8, marginBottom: 10 }}
          placeholder="00000000-0000-0000-0000-000000000000"
          required
        />

        <label style={{ display: 'block', marginBottom: 4 }}>SP Client Secret (required)</label>
        <input
          type="password"
          value={form.spClientSecret}
          onChange={(e) => updateField('spClientSecret', e.target.value)}
          style={{ width: '100%', padding: 8, marginBottom: 10 }}
          placeholder="Service principal client secret"
          required
        />

        <label style={{ display: 'block', marginBottom: 4 }}>SP Client ID Secret Ref (optional override)</label>
        <input
          value={form.spClientIdSecretRef || ''}
          onChange={(e) => updateField('spClientIdSecretRef', e.target.value)}
          style={{ width: '100%', padding: 8, marginBottom: 10 }}
          placeholder="customer-<guid>-sp-client-id or full Key Vault secret URI"
        />

        <label style={{ display: 'block', marginBottom: 4 }}>SP Client Secret Secret Ref (optional override)</label>
        <input
          value={form.spClientSecretSecretRef || ''}
          onChange={(e) => updateField('spClientSecretSecretRef', e.target.value)}
          style={{ width: '100%', padding: 8, marginBottom: 10 }}
          placeholder="customer-<guid>-sp-client-secret or full Key Vault secret URI"
        />

        <details style={{ marginBottom: 12 }}>
          <summary>Advanced: Optional Tenant/Subscription Secret Reference Overrides</summary>
          <div style={{ marginTop: 10 }}>
            <label style={{ display: 'block', marginBottom: 4 }}>SP Tenant ID Secret Ref</label>
            <input value={form.spTenantIdSecretRef || ''} onChange={(e) => updateField('spTenantIdSecretRef', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

            <label style={{ display: 'block', marginBottom: 4 }}>SP Subscription ID Secret Ref</label>
            <input value={form.spSubscriptionIdSecretRef || ''} onChange={(e) => updateField('spSubscriptionIdSecretRef', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />
          </div>
        </details>

        <button onClick={submit} disabled={isBusy}>
          {isBusy ? 'Onboarding...' : 'Onboard Customer'}
        </button>

        {message && <p style={{ marginTop: 12 }}>{message}</p>}
      </section>

      <section style={{ border: '1px solid #ddd', borderRadius: 8, padding: 16, marginTop: 16 }}>
        <h2 style={{ marginTop: 0 }}>Manage Existing Customers</h2>

        {customers.length === 0 ? (
          <p style={{ color: '#555' }}>No customers found.</p>
        ) : (
          <>
            <label style={{ display: 'block', marginBottom: 4 }}>Select Customer</label>
            <select
              value={selectedCustomerId}
              onChange={(e) => setSelectedCustomer(e.target.value)}
              style={{ width: '100%', padding: 8, marginBottom: 12 }}
            >
              {customers.map((customer) => (
                <option key={customer.customerId} value={customer.customerId}>
                  {customer.customerName} ({customer.subscriptionId})
                </option>
              ))}
            </select>

            <label style={{ display: 'block', marginBottom: 4 }}>Customer Name</label>
            <input value={updateForm.customerName} onChange={(e) => setUpdateForm((c) => ({ ...c, customerName: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

            <label style={{ display: 'block', marginBottom: 4 }}>Subscription ID</label>
            <input value={updateForm.subscriptionId} onChange={(e) => setUpdateForm((c) => ({ ...c, subscriptionId: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

            <label style={{ display: 'block', marginBottom: 4 }}>Tenant ID</label>
            <input value={updateForm.tenantId} onChange={(e) => setUpdateForm((c) => ({ ...c, tenantId: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

            <label style={{ display: 'block', marginBottom: 4 }}>Email</label>
            <input value={updateForm.email || ''} onChange={(e) => setUpdateForm((c) => ({ ...c, email: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

            <label style={{ display: 'block', marginBottom: 4 }}>
              <input
                type="checkbox"
                checked={updateForm.isActive}
                onChange={(e) => setUpdateForm((c) => ({ ...c, isActive: e.target.checked }))}
                style={{ marginRight: 8 }}
              />
              Customer is active
            </label>

            <details style={{ marginTop: 10, marginBottom: 12 }}>
              <summary>Optional: rotate SP credentials and/or update refs</summary>
              <div style={{ marginTop: 10 }}>
                <label style={{ display: 'block', marginBottom: 4 }}>SP Client ID (leave blank to keep existing)</label>
                <input value={updateForm.spClientId || ''} onChange={(e) => setUpdateForm((c) => ({ ...c, spClientId: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

                <label style={{ display: 'block', marginBottom: 4 }}>SP Client Secret (leave blank to keep existing)</label>
                <input type="password" value={updateForm.spClientSecret || ''} onChange={(e) => setUpdateForm((c) => ({ ...c, spClientSecret: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

                <label style={{ display: 'block', marginBottom: 4 }}>SP Client ID Secret Ref</label>
                <input value={updateForm.spClientIdSecretRef || ''} onChange={(e) => setUpdateForm((c) => ({ ...c, spClientIdSecretRef: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

                <label style={{ display: 'block', marginBottom: 4 }}>SP Client Secret Secret Ref</label>
                <input value={updateForm.spClientSecretSecretRef || ''} onChange={(e) => setUpdateForm((c) => ({ ...c, spClientSecretSecretRef: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

                <label style={{ display: 'block', marginBottom: 4 }}>SP Tenant ID Secret Ref</label>
                <input value={updateForm.spTenantIdSecretRef || ''} onChange={(e) => setUpdateForm((c) => ({ ...c, spTenantIdSecretRef: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

                <label style={{ display: 'block', marginBottom: 4 }}>SP Subscription ID Secret Ref</label>
                <input value={updateForm.spSubscriptionIdSecretRef || ''} onChange={(e) => setUpdateForm((c) => ({ ...c, spSubscriptionIdSecretRef: e.target.value }))} style={{ width: '100%', padding: 8, marginBottom: 10 }} />
              </div>
            </details>

            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <button onClick={updateExistingCustomer} disabled={isUpdating || isDeleting}>
                {isUpdating ? 'Updating...' : 'Update Customer'}
              </button>
              <button onClick={deleteExistingCustomer} disabled={isUpdating || isDeleting} style={{ backgroundColor: '#fee2e2', color: '#991b1b' }}>
                {isDeleting ? 'Deleting...' : 'Delete Customer'}
              </button>
            </div>

            {updateMessage && <p style={{ marginTop: 12 }}>{updateMessage}</p>}
          </>
        )}
      </section>
    </main>
  )
}