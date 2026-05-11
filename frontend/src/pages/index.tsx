import { useEffect } from 'react'
import { useRouter } from 'next/router'

export default function HomePage() {
  const router = useRouter()

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('azselfservice_token') : null
    router.replace(token ? '/dashboard' : '/login')
  }, [router])

  return <div style={{ padding: 24 }}>Loading...</div>
}
