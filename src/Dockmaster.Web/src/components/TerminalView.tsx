import { useEffect, useRef, useState } from 'react'
import { Terminal as XTerm } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { openTerminal } from '../lib/live'

/**
 * A shell in a container, in the page: bash where the image has it, sh otherwise. It follows the size of its box;
 * leaving the page closes the shell. `session` changes open a new one.
 */
export default function TerminalView({ agentId, container, session }: { agentId: number; container: string; session: number }) {
  const box = useRef<HTMLDivElement>(null)
  const [ended, setEnded] = useState<string | null>(null)

  useEffect(() => {
    if (!box.current) return
    const term = new XTerm({
      cursorBlink: true, fontSize: 13, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace', scrollback: 5000,
      theme: { background: '#0a0e13', foreground: '#e6edf3', cursor: '#3b82f6', selectionBackground: '#3b82f655' },
    })
    const fit = new FitAddon()
    term.loadAddon(fit)
    term.open(box.current)
    fit.fit()
    term.focus()
    const shell = openTerminal(agentId, container, term.cols, term.rows,
      text => term.write(text),
      (ok, message) => {
        setEnded(message || (ok ? 'closed' : 'failed'))
        term.write(`\r\n\x1b[90m[${message || 'closed'}]\x1b[0m\r\n`)
      })
    const input = term.onData(data => shell.send(data))
    const size = term.onResize(({ cols, rows }) => shell.resize(cols, rows))
    const observer = new ResizeObserver(() => { try { fit.fit() } catch { /* hidden */ } })
    observer.observe(box.current)
    return () => { observer.disconnect(); input.dispose(); size.dispose(); shell.close(); term.dispose(); setEnded(null) }
  }, [agentId, container, session])

  return (
    <>
      <div className="terminal" ref={box} />
      {ended && <p className="mini">The shell ended ({ended}).</p>}
    </>
  )
}
