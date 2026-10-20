import { useMemo, useState } from 'react'
import type { MountEdit, ServiceSummary } from '../../lib/api'
import Modal from '../../components/Modal'
import { looksSecret, parseEnvFile } from '../../lib/fmt'
import { templates, type AppTemplate } from './apps'
import { dataPaths, freshVolumeName } from './dataPaths'
import SchedulePicker, { browserTimeZone, type ScheduleValue } from './SchedulePicker'

interface PortRow { host: string; container: string; udp: boolean }
interface EnvRow { name: string; value: string }
interface DataRow { name: string; target: string }

/** What the form sends: a new service (everything) or the settings of one (null = left as it is). */
export interface ServiceFormValue {
  name: string; image: string | null; ports: string[] | null; environment: EnvRow[] | null; restart: string | null; mounts: MountEdit[]
  /** a job's schedule: undefined = left as it is, null = taken off (the service is kept running again), a value = set */
  schedule?: ScheduleValue | null
}

/** The restart picker's entry for a job on a schedule (no docker restart policy: Dockmaster starts it). */
const SCHEDULE = 'schedule'
const RESTARTS: { value: string; text: string }[] = [
  { value: 'unless-stopped', text: 'always, unless stopped by hand' },
  { value: 'always', text: 'always' },
  { value: 'on-failure', text: 'only when it crashes' },
  { value: 'no', text: 'never' },
  { value: SCHEDULE, text: 'it is a job — runs on a schedule' },
]

const toRow = (p: string): PortRow => {
  const udp = p.endsWith('/udp')
  const parts = p.replace(/\/(tcp|udp)$/, '').split(':')
  return { host: parts.length > 1 ? parts[parts.length - 2] : '', container: parts[parts.length - 1], udp }
}
const fromRow = (r: PortRow) => (r.host.trim() ? `${r.host.trim()}:` : '') + r.container.trim() + (r.udp ? '/udp' : '')

/**
 * A service as a form: the image, the ports opened on the server, its settings (environment), when docker restarts
 * it — and for a new one where it keeps its data, or a ready-made app that fills all of it in. Values go into the
 * compose file; ${VARIABLES} in a value still come from the .env.
 */
export default function ServiceForm({ service, services, envFile, secretNames, running, onSave, onClose }: {
  /** the service to change; none = a new one */
  service?: ServiceSummary
  services: ServiceSummary[]
  /** the project's .env: the ${VARIABLES} a value may use */
  envFile: string
  /** the project's secrets: ${VARIABLES} too, filled in on deploy */
  secretNames: string[]
  running: boolean
  onSave: (value: ServiceFormValue, deploy: boolean) => Promise<boolean>
  onClose: () => void
}) {
  const isNew = !service
  // made once per form: the passwords in them stay while it is open
  const apps = useMemo(() => templates(), [])
  const [app, setApp] = useState<AppTemplate | null>(null)
  const [picking, setPicking] = useState(isNew)
  const [name, setName] = useState(service?.name ?? '')
  const [image, setImage] = useState(service?.image ?? '')
  const [ports, setPorts] = useState<PortRow[]>(() => (service?.portList ?? []).map(toRow))
  const [env, setEnv] = useState<EnvRow[]>(() => service?.environment?.map(e => ({ ...e })) ?? [])
  const vars = useMemo(() => parseEnvFile(envFile), [envFile])
  const varNames = useMemo(() => [...new Set([...Object.keys(vars), ...secretNames])].sort((a, b) => a.localeCompare(b)), [vars, secretNames])
  const initialRestart = service?.schedule ? SCHEDULE : service?.restart?.replace(/^"|"$/g, '').split(':')[0] ?? (service ? '' : 'unless-stopped')
  const [restart, setRestart] = useState(initialRestart)
  // a job's schedule: as written, or a nightly one in the browser's time zone to start from
  const [schedule, setSchedule] = useState<ScheduleValue>(() => service?.schedule
    ? { cron: service.schedule.cron, timeZone: service.schedule.timeZone ?? 'UTC', timeout: service.schedule.timeout ?? '' }
    : { cron: '0 3 * * *', timeZone: browserTimeZone(), timeout: '' })
  const isJob = restart === SCHEDULE
  const [data, setData] = useState<DataRow[]>([])
  const [busy, setBusy] = useState(false)

  const taken = new Set(services.map(s => s.name))
  function use(t: AppTemplate | null) {
    setApp(t); setPicking(false)
    if (!t) return
    let n = t.name, i = 2
    while (taken.has(n)) n = t.name + i++
    setName(n); setImage(t.image); setPorts(t.ports.map(toRow)); setEnv(t.env.map(e => ({ ...e })))
    const volumes = new Set(services.flatMap(s => s.mounts.map(m => m.source)))
    setData(t.data.map(d => { const v = freshVolumeName(n, volumes, d.suffix); volumes.add(v); return { name: v, target: d.target } }))
  }

  // a custom image: where the image keeps its data, when it is a known one
  const known = isNew && !app && image ? dataPaths({ image } as ServiceSummary).filter(p => !data.some(d => d.target === p)) : []

  const problem = ((): string | null => {
    if (isNew && !/^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(name)) return 'the service needs a name: letters, digits, -, _ (e.g. web)'
    if (isNew && taken.has(name)) return `there is a service named ${name} already`
    if (!image.trim() || /\s/.test(image.trim())) return 'the image is a name like nginx:1.27'
    for (const p of ports) {
      if (!/^\d{1,5}$/.test(p.container.trim())) return 'a port is a number, e.g. 80'
      if (p.host.trim() && !/^\d{1,5}$/.test(p.host.trim())) return 'the server\'s port is a number, e.g. 8080 (or empty: docker picks one)'
    }
    const hostPorts = ports.map(p => p.host.trim()).filter(Boolean)
    if (new Set(hostPorts).size !== hostPorts.length) return 'two ports open the same port on the server'
    const names = env.map(e => e.name.trim()).filter(Boolean)
    if (names.some(n => !/^[A-Za-z_][A-Za-z0-9_]*$/.test(n))) return 'a setting\'s name is letters, digits and _ (e.g. DB_HOST)'
    if (new Set(names).size !== names.length) return 'a setting is there twice'
    if (data.some(d => !/^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(d.name) || !d.target.startsWith('/'))) return 'data: a volume name, and a path in the container starting with /'
    if (isJob && !schedule.cron.trim()) return 'the schedule needs a cron expression'
    if (isJob && (service?.replicas ?? 1) > 1) return `a job runs one container at a time: set the replicas of ${service!.name} to 1 first`
    return null
  })()

  const portsNow = ports.filter(p => p.container.trim()).map(fromRow)
  const envNow = env.filter(e => e.name.trim()).map(e => ({ name: e.name.trim(), value: e.value }))
  // editing: only what changed goes into the file
  const imageChanged = isNew || image.trim() !== service.image
  const portsChanged = isNew || JSON.stringify(portsNow) !== JSON.stringify(service.portList ?? [])
  const envChanged = isNew || (service.environment !== null && JSON.stringify(envNow) !== JSON.stringify(service.environment))
  const restartChanged = isNew || (restart !== initialRestart && restart !== '' && !isJob)
  const scheduleNow: ScheduleValue = { cron: schedule.cron.trim(), timeZone: schedule.timeZone, timeout: schedule.timeout.trim() }
  const scheduleChanged = isJob
    ? isNew || !service.schedule || service.schedule.cron !== scheduleNow.cron || (service.schedule.timeZone ?? 'UTC') !== scheduleNow.timeZone || (service.schedule.timeout ?? '') !== scheduleNow.timeout
    : !!service?.schedule
  const changed = imageChanged || portsChanged || envChanged || restartChanged || scheduleChanged

  async function save(deploy: boolean) {
    if (problem || !changed) return
    setBusy(true)
    const ok = await onSave({
      name: name.trim(), image: imageChanged ? image.trim() : null, restart: restartChanged ? restart : null,
      ports: portsChanged ? portsNow : null, environment: envChanged ? envNow : null,
      // a job: its schedule (the server turns docker's restarts off with it); a job no more: the schedule goes, the restart chosen above stays
      schedule: isJob ? (scheduleChanged ? scheduleNow : undefined) : service?.schedule ? null : undefined,
      mounts: data.map(d => ({ keep: null, type: 'volume', source: d.name, target: d.target, readOnly: false })),
    }, deploy)
    setBusy(false)
    if (ok) onClose()
  }

  const setPort = (i: number, patch: Partial<PortRow>) => setPorts(ps => ps.map((p, j) => j === i ? { ...p, ...patch } : p))
  const setVar = (i: number, patch: Partial<EnvRow>) => setEnv(es => es.map((e, j) => j === i ? { ...e, ...patch } : e))

  if (picking) return (
    <Modal label="Add a service" wide onClose={onClose} onSubmit={() => use(null)}>
      <p className="mini">Pick a ready-made app — image, ports, settings and data are filled in, with passwords made up — or start from any image.</p>
      <div className="apps">
        {apps.map(t => (
          <button key={t.id} type="button" className="app-card" onClick={() => use(t)}>
            <b>{t.title}</b><span className="mini">{t.text}</span><code className="mini">{t.image}</code>
          </button>
        ))}
        <button type="submit" className="app-card custom"><b>Custom image</b><span className="mini">any image from Docker Hub or your registry</span></button>
      </div>
      <div className="toolbar form-actions flush"><button type="button" onClick={onClose}>Cancel</button></div>
    </Modal>
  )

  return (
    <Modal label={isNew ? `Add a service${app ? ` — ${app.title}` : ''}` : `Settings of ${service.name}`} wide onClose={onClose} onSubmit={() => { void save(!running) }}>
      <div className="row">
        {isNew && <div className="narrow"><label htmlFor="sf-name">Name</label><input id="sf-name" value={name} onChange={e => setName(e.target.value)} placeholder="web" autoFocus /></div>}
        <div><label htmlFor="sf-image">Image</label><input id="sf-image" value={image} onChange={e => setImage(e.target.value)} placeholder="nginx:1.27 or registry.example.com/app:1.4" /></div>
      </div>
      {!isNew && image !== service.image && <p className="mini">A new image (or tag) is pulled on the next <b>pull &amp; deploy</b>.</p>}

      <div className="field-label">Ports opened on the server</div>
      {ports.length === 0 && <p className="mini">None: only the other services of the project reach it (by its name{name ? <> — <code>{name}</code></> : ''}). Open a port to reach it from outside.</p>}
      {ports.map((p, i) => (
        <div key={i} className="form-row">
          <span className="mini">server</span>
          <input className="port" aria-label="port on the server" value={p.host} onChange={e => setPort(i, { host: e.target.value })} placeholder="8080" inputMode="numeric" />
          <span className="muted">→ container</span>
          <input className="port" aria-label="port in the container" value={p.container} onChange={e => setPort(i, { container: e.target.value })} placeholder="80" inputMode="numeric" />
          <label className="check-inline"><input type="checkbox" checked={p.udp} onChange={e => setPort(i, { udp: e.target.checked })} /> UDP</label>
          <button type="button" className="icon" aria-label="remove this port" onClick={() => setPorts(ps => ps.filter((_, j) => j !== i))}>✕</button>
        </div>
      ))}
      <button type="button" className="sm" onClick={() => setPorts(ps => [...ps, { host: '', container: '', udp: false }])}>+ port</button>
      {ports.some(p => p.host.trim()) && <p className="mini">Reached at <code>http://&lt;server&gt;:{ports.find(p => p.host.trim())!.host.trim()}</code>. A port held on the server allows one replica at a time (updates recreate instead of rolling).</p>}

      {service?.environment === null
        ? <p className="mini">The settings of this service are written in a way this form does not show — change them in the Compose tab.</p>
        : <>
          <div className="field-label">Settings (environment variables)</div>
          {varNames.length > 0 && <p className="mini">Type <code>${'{'}</code> in a value to pick one of the {varNames.length} variables of the project's .env and Secrets.</p>}
          {env.map((e, i) => (
            <div key={i}>
              <div className="form-row">
                <input className="env-name" aria-label="name" value={e.name} onChange={ev => setVar(i, { name: ev.target.value })} placeholder="NAME" />
                <span className="muted">=</span>
                <input className="env-value" aria-label={`value of ${e.name}`} value={e.value} onChange={ev => setVar(i, { value: ev.target.value })} placeholder="value, or ${VARIABLE} from the .env"
                  list={varNames.length > 0 ? 'sf-env-vars' : undefined} />
                <button type="button" className="icon" aria-label="remove this setting" onClick={() => setEnv(es => es.filter((_, j) => j !== i))}>✕</button>
              </div>
              <VarHint value={e.value} vars={vars} secrets={secretNames} />
            </div>
          ))}
          <datalist id="sf-env-vars">
            {varNames.map(n => <option key={n} value={'${' + n + '}'}>{secretNames.includes(n) ? 'a secret' : looksSecret(n) ? '••••••' : vars[n]}</option>)}
          </datalist>
          <button type="button" className="sm" onClick={() => setEnv(es => [...es, { name: '', value: '' }])}>+ setting</button>
          {env.some(e => /PASS|SECRET|TOKEN|KEY/i.test(e.name)) && <p className="mini">Passwords here are readable by anyone who can open the project. Kept apart and never shown again: the <i>Secrets</i> tab.</p>}
        </>}

      <div className="row">
        <div className="narrow">
          <label htmlFor="sf-restart">Restart it</label>
          <select id="sf-restart" className="wide-select" value={restart} onChange={e => setRestart(e.target.value)}>
            {restart === '' && <option value="">as docker does: never</option>}
            {RESTARTS.map(r => <option key={r.value} value={r.value}>{r.text}</option>)}
          </select>
        </div>
      </div>
      {isJob && <>
        <SchedulePicker value={schedule} onChange={setSchedule} />
        <p className="mini">A deploy creates the container but does not start it. Dockmaster starts it at each time on the schedule; the app does its work and exits, and the container waits for the next time. A run that ends with an error shows under Events, with its last log lines.{service?.schedule ? '' : ' Written as x-dockmaster.schedule, with restart: "no".'}</p>
      </>}

      {isNew && (
        <>
          <div className="field-label">Data that must survive an update</div>
          {data.length === 0 && known.length === 0 && <p className="mini">None. A database, uploads or settings need a volume — add one here or later in <b>Services → Data</b>.</p>}
          {data.map((d, i) => (
            <div key={i} className="form-row">
              <span className="mini">volume</span>
              <input aria-label="volume name" value={d.name} onChange={e => setData(ds => ds.map((x, j) => j === i ? { ...x, name: e.target.value } : x))} />
              <span className="muted">→</span>
              <input aria-label="path in the container" value={d.target} onChange={e => setData(ds => ds.map((x, j) => j === i ? { ...x, target: e.target.value } : x))} placeholder="/data" />
              <button type="button" className="icon" aria-label="remove" onClick={() => setData(ds => ds.filter((_, j) => j !== i))}>✕</button>
            </div>
          ))}
          {known.map(p => (
            <div key={p} className="callout warn"><div className="callout-line">
              <span>This image keeps its data in <code>{p}</code>.</span>
              <button type="button" className="sm primary" onClick={() => setData(ds => [...ds, { name: freshVolumeName(name || 'app', new Set(ds.map(x => x.name)), p.split('/').filter(Boolean).pop()), target: p }])}>keep it in a volume</button>
            </div></div>
          ))}
          <button type="button" className="sm" onClick={() => setData(ds => [...ds, { name: freshVolumeName(name || 'app', new Set(ds.map(x => x.name))), target: '' }])}>+ volume</button>
        </>
      )}

      {app?.hint && <p className="mini">After the deploy: {app.hint}.</p>}
      {problem && <p className="error">{problem}</p>}
      <div className="toolbar form-actions flush">
        <button className="primary" type="button" disabled={busy || !!problem || running || !changed} onClick={() => { void save(true) }}>{isNew ? 'Add & deploy' : 'Save & deploy'}</button>
        <button type="button" disabled={busy || !!problem || !changed} onClick={() => { void save(false) }}>{isNew ? 'Add only' : 'Save only'}</button>
        {isNew && <button type="button" onClick={() => setPicking(true)}>← apps</button>}
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </Modal>
  )
}

/** What the ${VARIABLES} of a value come to from the .env or Secrets, or which of them it lacks. */
function VarHint({ value, vars, secrets }: { value: string; vars: Record<string, string>; secrets: string[] }) {
  const used = [...value.matchAll(/\$\{([A-Za-z_][A-Za-z0-9_.-]*)(?:[:?-][^}]*)?\}/g)].map(m => m[1])
  if (used.length === 0) return null
  const missing = used.filter(n => !(n in vars) && !secrets.includes(n))
  if (missing.length > 0) return <div className="env-hint error">{missing.join(', ')} {missing.length === 1 ? 'is' : 'are'} neither in the .env nor a secret: compose leaves {missing.length === 1 ? 'it' : 'them'} empty</div>
  if (used.length === 1 && secrets.includes(used[0])) return <div className="env-hint mini">= •••••• (a secret)</div>
  const shown = used.length === 1 && value.trim() === '${' + used[0] + '}'
    ? (looksSecret(used[0]) ? '•••••• (from the .env)' : vars[used[0]] || '(empty in the .env)')
    : 'from the .env: ' + used.join(', ')
  return <div className="env-hint mini" title={looksSecret(used[0]) ? undefined : shown}>= {shown}</div>
}
