import Prism from 'prismjs'
import 'prismjs/components/prism-yaml'

/** The line of each service of a compose file (0-based), in the order written. */
export function serviceLines(compose: string): { name: string; line: number }[] {
  const out: { name: string; line: number }[] = []
  let inServices = false
  let indent = -1
  compose.split('\n').forEach((l, i) => {
    if (/^services:\s*(#.*)?$/.test(l)) { inServices = true; indent = -1; return }
    if (/^\S/.test(l)) { inServices = false; return }
    if (!inServices) return
    const m = /^(\s+)([A-Za-z0-9_.-]+):\s*(#.*)?$/.exec(l)
    if (!m) return
    if (indent < 0) indent = m[1].length
    if (m[1].length === indent) out.push({ name: m[2], line: i })
  })
  return out
}

/**
 * Yaml colours for a compose file, with the top-level sections (services:, volumes:…) and the name of each service
 * standing out: in a long file they are what the eye looks for. Service names get the `service` class, which the
 * "go to a service" chips also use to find where a service is drawn.
 */
export function highlightCompose(code: string): string {
  const services = new Set(serviceLines(code).map(s => s.line))
  const tokens = Prism.tokenize(code, Prism.languages.yaml)
  let line = 0
  let lineStart = true // nothing but the line break before this token
  for (const t of tokens) {
    if (typeof t !== 'string' && t.type === 'key') {
      const mark = services.has(line) ? 'service' : lineStart ? 'section' : null
      if (mark) t.alias = [...[t.alias ?? []].flat(), mark]
    }
    const text = textOf(t)
    line += text.split('\n').length - 1
    lineStart = text.endsWith('\n') || (lineStart && text === '')
  }
  return Prism.Token.stringify(Prism.util.encode(tokens), 'yaml')
}

const textOf = (t: Prism.TokenStream): string =>
  typeof t === 'string' ? t : Array.isArray(t) ? t.map(textOf).join('') : textOf(t.content)
