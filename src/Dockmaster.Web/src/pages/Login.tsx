import { useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { api, ApiError, errorText, type Me } from '../lib/api'
import { useStoredState } from '../lib/hooks'
import { restartLive } from '../lib/live'

/** Sign-in with a name and password; on the first run it makes the first admin instead. Goes back to where it came from. */
export default function Login({ me, onLogin }: { me: Me; onLogin: () => Promise<void> }) {
  // the name typed last time: most people sign in as the same one
  const [name, setName] = useStoredState('dm.login.name', 'admin')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const setup = !me.hasPassword

  async function submit(e: FormEvent) {
    e.preventDefault()
    setError(null); setBusy(true)
    try {
      if (setup) await api('/auth/setup', { method: 'POST', json: { name, password, code } })
      await api('/auth/login', { method: 'POST', json: { name, password } })
      await restartLive()
      // who signed in is read before leaving: the app would otherwise still think nobody is, and come back here
      await onLogin()
      const ret = params.get('returnUrl')
      navigate(ret && ret.startsWith('/') && ret !== '/login' ? ret : '/', { replace: true })
    } catch (err) {
      setError(err instanceof ApiError && err.status === 401 && !err.message.includes('disabled') ? 'Wrong name or password.' : errorText(err))
    } finally { setBusy(false) }
  }

  return (
    <div className="login">
      <div className="card">
        <h1>⚓ Dockmaster</h1>
        {setup && <p className="muted">First run: choose the admin's name and password (8+ characters). More people are added later on the Users page.</p>}
        <form onSubmit={submit}>
          {setup && <>
            <label htmlFor="login-code">Setup code</label>
            <input id="login-code" value={code} onChange={e => setCode(e.target.value)} autoComplete="one-time-code" autoCapitalize="characters" spellCheck={false}
              required placeholder="XXXX-XXXX-XXXX" />
            <p className="muted">The server prints it to its log when it starts: <code>docker logs dockmaster</code>.</p>
          </>}
          <label htmlFor="login-name">{setup ? 'Admin name' : 'Name'}</label>
          <input id="login-name" value={name} onChange={e => setName(e.target.value)} autoComplete="username" autoCapitalize="none" spellCheck={false} required autoFocus={!name} />
          <label htmlFor="login-password">Password</label>
          <input id="login-password" type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete={setup ? 'new-password' : 'current-password'}
            autoFocus={!!name} required minLength={setup ? 8 : 1} />
          {error && <p className="error">{error}</p>}
          <div className="form-actions"><button className="primary" type="submit" disabled={busy}>{setup ? 'Create admin & sign in' : 'Sign in'}</button></div>
        </form>
      </div>
    </div>
  )
}
