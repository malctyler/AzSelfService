import { useEffect } from 'react'
import type { AppProps } from 'next/app'
import { useAuthStore } from '../store/auth'

export default function App({ Component, pageProps }: AppProps) {
  const hydrate = useAuthStore((state) => state.hydrate)

  useEffect(() => {
    hydrate()
  }, [hydrate])

  return <Component {...pageProps} />
}
