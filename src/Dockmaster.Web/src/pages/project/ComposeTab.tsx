import type { Agent } from '../../lib/api'
import { useEffect, useMemo, useRef, useState } from 'react'
import CodeEditor from '../../components/CodeEditor'
import { highlightCompose, serviceLines } from '../../lib/composeOutline'
import { composeFor, templates } from './apps'
import type { ProjectForm } from './ProjectEdit'

/** Name, server and ready timeout of the project, and its compose file with a word on rolling updates. */
export default function ComposeTab({ form, agentId, isNew, agents, onChange }: {
  form: ProjectForm
  /** the server it runs on: the chosen one, or the only one there is */
  agentId: number
  isNew: boolean
  agents: Agent[]
  onChange: (patch: Partial<ProjectForm>) => void
}) {
  const apps = useMemo(() => templates(), [])
  return (
    <>
      <div className="row">
        <div><label htmlFor="p-name">Project name (compose project, lowercase)</label><input id="p-name" value={form.name} onChange={e => onChange({ name: e.target.value })} disabled={!isNew} placeholder="shop" /></div>
        <div><label htmlFor="p-agent">Server (where it runs)</label>
          <select id="p-agent" value={agentId} onChange={e => onChange({ agentId: Number(e.target.value) })}>
            <option value={0}>— choose —</option>
            {agents.map(a => <option key={a.id} value={a.id}>{a.name}{a.online ? '' : ' (offline)'}</option>)}
          </select>
        </div>
        <div className="narrow"><label htmlFor="p-timeout">Ready timeout, s</label><input id="p-timeout" type="number" value={form.timeout} onChange={e => onChange({ timeout: Number(e.target.value) })} /></div>
      </div>
      {isNew && (
        <>
          <div className="field-label">Start from a ready-made app <span className="mini">— or write the compose file below; more services are added later in the Services tab</span></div>
          <div className="app-chips">
            {apps.map(t => (
              <button key={t.id} type="button" className="chip" title={t.text}
                onClick={() => onChange({ compose: composeFor(t), ...(form.name ? {} : { name: t.id.replace(/[^a-z0-9-]/g, '') }) })}>{t.title}</button>
            ))}
          </div>
        </>
      )}
      <label htmlFor="p-compose" className="compose-head">docker-compose.yml</label>
      <ServiceJump compose={form.compose} />
      <CodeEditor textareaId="p-compose" language="yaml" highlight={highlightCompose} value={form.compose} onChange={compose => onChange({ compose })} minHeight="max(360px, calc(100vh - 380px))" />
      <p className="mini">
        Rolling update (zero downtime) applies to services with <code>deploy.replicas &gt; 1</code> (or <code>x-dockmaster: {'{'}rolling: true{'}'}</code>) that have no <code>container_name</code> and no published <code>ports</code>:
        replicas are replaced <code>deploy.update_config.parallelism</code> at a time with a pause of <code>deploy.update_config.delay</code> between them.
        A new replica is ready when its <code>healthcheck</code> is healthy; without a healthcheck it must stay running for <code>deploy.update_config.monitor</code> (default 10s).
        Label <code>dockmaster.pre-stop-hook</code> runs inside the old replica before it is stopped (drain).
      </p>
    </>
  )
}

/**
 * The compose file's services as a row of chips that stays in view while the file scrolls: one click shows a service
 * (and puts the cursor on its name, except on a touch screen, where that would open the keyboard); the chip of the
 * service on screen is lit.
 */
function ServiceJump({ compose }: { compose: string }) {
  const names = useMemo(() => serviceLines(compose).map(s => s.name), [compose])
  const bar = useRef<HTMLDivElement>(null)
  const [current, setCurrent] = useState<string | null>(null)

  // how far below the window's top the bar sits once stuck: a service shown is scrolled to just under it
  const barBottom = () => bar.current ? (parseFloat(getComputedStyle(bar.current).top) || 0) + bar.current.offsetHeight : 0
  const drawn = () => [...document.querySelectorAll<HTMLElement>('.code-editor-pre .token.service')]

  useEffect(() => {
    let frame = 0
    const spy = () => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => {
        const line = barBottom() + 8
        const passed = drawn().filter(el => el.getBoundingClientRect().top <= line)
        setCurrent(passed.length ? passed[passed.length - 1].textContent : null)
      })
    }
    spy()
    window.addEventListener('scroll', spy, { passive: true })
    window.addEventListener('resize', spy)
    return () => { cancelAnimationFrame(frame); window.removeEventListener('scroll', spy); window.removeEventListener('resize', spy) }
  }, [compose])

  // the lit chip stays visible in a row scrolled sideways
  useEffect(() => { bar.current?.querySelector('.on')?.scrollIntoView({ block: 'nearest', inline: 'nearest' }) }, [current])

  if (names.length < 2) return null

  function go(name: string) {
    const i = names.indexOf(name)
    const el = drawn()[i]
    if (!el) return
    window.scrollTo({ top: window.scrollY + el.getBoundingClientRect().top - barBottom() - 12, behavior: 'smooth' })
    el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash')
    const area = document.getElementById('p-compose') as HTMLTextAreaElement | null
    if (area && !matchMedia('(pointer: coarse)').matches) {
      const lines = compose.split('\n')
      const line = serviceLines(compose)[i].line
      const at = lines.slice(0, line).reduce((n, l) => n + l.length + 1, 0) + lines[line].indexOf(name)
      area.focus({ preventScroll: true })
      area.setSelectionRange(at, at + name.length)
    }
  }

  return (
    <div className="service-bar" ref={bar} role="navigation" aria-label="services of the compose file">
      <span className="mini">services</span>
      {names.map(n => <button key={n} type="button" className={'chip' + (n === current ? ' on' : '')} onClick={() => go(n)}>{n}</button>)}
    </div>
  )
}
