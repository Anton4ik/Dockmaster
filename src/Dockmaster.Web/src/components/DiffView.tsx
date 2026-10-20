import { useMemo } from 'react'
import { diffLines, withContext } from '../lib/diff'

/** What changed between two texts, line by line: removed lines red with −, added ones green with +, the rest folded. */
export default function DiffView({ before, after, same = 'no changes' }: { before: string; after: string; same?: string }) {
  const rows = useMemo(() => withContext(diffLines(before, after)), [before, after])
  if (rows.length === 0) return <p className="muted mini">{same}</p>
  return (
    <pre className="diff">
      {rows.map((r, i) => r.kind === 'gap'
        ? <div key={i} className="diff-gap">… {r.count} unchanged line{r.count === 1 ? '' : 's'}</div>
        : <div key={i} className={'diff-' + r.kind}><span className="diff-mark">{r.kind === 'old' ? '−' : r.kind === 'new' ? '+' : ' '}</span>{r.text || ' '}</div>)}
    </pre>
  )
}
