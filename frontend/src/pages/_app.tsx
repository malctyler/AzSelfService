import { useEffect } from 'react'
import type { AppProps } from 'next/app'
import { useAuthStore } from '../store/auth'
import Link from 'next/link'

export default function App({ Component, pageProps }: AppProps) {
  const hydrate = useAuthStore((state) => state.hydrate)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  return (
    <>
      <Component {...pageProps} />
      <nav>
        <ul>
          <li>
            <Link href="/storage-accounts">Storage Accounts</Link>
          </li>
        </ul>
      </nav>
    </>
  )
}
