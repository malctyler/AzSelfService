import Link from 'next/link'
import { useEffect, useState } from 'react'
import { useRouter } from 'next/router'
import { onboardCustomer, type OnboardCustomerRequest } from '../../lib/api'
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

  const updateField = (field: keyof OnboardCustomerRequest, value: string) => {
    setForm((current) => ({ ...current, [field]: value }))
  }

  const submit = async () => {
    setIsBusy(true)
    setMessage(null)

    try {
      const payload = await onboardCustomer(form)
      setMessage(
        `Customer onboarded. User '${payload.username}' created with role '${payload.role}'. Secret ref: ${payload.spClientSecretSecretRefMasked}`
      )
    } catch (err: any) {
      setMessage(err?.response?.data?.message || 'Failed to onboard customer.')
    } finally {
      setIsBusy(false)
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

        <details style={{ marginBottom: 12 }}>
          <summary>Advanced: Override Key Vault Secret References</summary>
          <div style={{ marginTop: 10 }}>
            <label style={{ display: 'block', marginBottom: 4 }}>SP Client ID Secret Ref</label>
            <input value={form.spClientIdSecretRef || ''} onChange={(e) => updateField('spClientIdSecretRef', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

            <label style={{ display: 'block', marginBottom: 4 }}>SP Client Secret Secret Ref</label>
            <input value={form.spClientSecretSecretRef || ''} onChange={(e) => updateField('spClientSecretSecretRef', e.target.value)} style={{ width: '100%', padding: 8, marginBottom: 10 }} />

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
    </main>
  )
}