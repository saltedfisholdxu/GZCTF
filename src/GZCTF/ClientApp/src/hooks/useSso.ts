import useSWR from 'swr'

export interface SsoClientConfig {
  enabled: boolean
  localAuthenticationEnabled: boolean
  localCredentialManagementEnabled: boolean
  registrationEnabled: boolean
  backchannelLogoutEnabled: boolean
  authority: string | null
  clientId: string | null
}

const fallbackConfig: SsoClientConfig = {
  enabled: false,
  localAuthenticationEnabled: true,
  localCredentialManagementEnabled: true,
  registrationEnabled: true,
  backchannelLogoutEnabled: false,
  authority: null,
  clientId: null,
}

const fetchConfig = async (url: string): Promise<SsoClientConfig> => {
  const response = await fetch(url, { credentials: 'include' })
  if (!response.ok) throw new Error('无法读取 SSO 配置')
  return response.json()
}

export const useSso = () => {
  const { data, error, mutate } = useSWR<SsoClientConfig>('/api/sso/config', fetchConfig, {
    refreshInterval: 0,
    revalidateOnFocus: false,
    revalidateOnReconnect: false,
    shouldRetryOnError: false,
  })

  return { config: data ?? fallbackConfig, error, mutate }
}

export const startSsoLogin = (returnUrl: string) => {
  window.location.href = `/api/sso/login?returnUrl=${encodeURIComponent(returnUrl)}`
}

export const submitSsoLogout = () => {
  const form = document.createElement('form')
  form.method = 'POST'
  form.action = '/api/sso/logout'
  form.style.display = 'none'
  document.body.appendChild(form)
  form.submit()
}
