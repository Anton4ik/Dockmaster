/** One line of a line diff: kept, only in the old text, or only in the new one. */
export interface DiffLine { kind: 'same' | 'old' | 'new'; text: string }

/**
 * A line diff of two texts (longest common subsequence). Compose files are a few hundred lines, so the plain
 * quadratic table is fine; beyond a size where it would not be, everything counts as changed.
 */
export function diffLines(before: string, after: string): DiffLine[] {
  const a = before.replace(/\r\n/g, '\n').split('\n'), b = after.replace(/\r\n/g, '\n').split('\n')
  if (a.length * b.length > 4_000_000) return [...a.map(text => ({ kind: 'old' as const, text })), ...b.map(text => ({ kind: 'new' as const, text }))]
  // lcs[i][j]: common lines of a[i..] and b[j..]
  const lcs = Array.from({ length: a.length + 1 }, () => new Uint32Array(b.length + 1))
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--)
      lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1])
  const out: DiffLine[] = []
  let i = 0, j = 0
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) { out.push({ kind: 'same', text: a[i] }); i++; j++ }
    else if (lcs[i + 1][j] >= lcs[i][j + 1]) out.push({ kind: 'old', text: a[i++] })
    else out.push({ kind: 'new', text: b[j++] })
  }
  while (i < a.length) out.push({ kind: 'old', text: a[i++] })
  while (j < b.length) out.push({ kind: 'new', text: b[j++] })
  return out
}

/** The changed lines with `context` unchanged lines around them; runs of unchanged lines in between become one `…` gap. */
export function withContext(lines: DiffLine[], context = 3): (DiffLine | { kind: 'gap'; count: number })[] {
  const near = lines.map((_, i) => lines.slice(Math.max(0, i - context), i + context + 1).some(l => l.kind !== 'same'))
  const out: (DiffLine | { kind: 'gap'; count: number })[] = []
  let skipped = 0
  lines.forEach((l, i) => {
    if (near[i]) {
      if (skipped) { out.push({ kind: 'gap', count: skipped }); skipped = 0 }
      out.push(l)
    } else skipped++
  })
  if (skipped && out.length) out.push({ kind: 'gap', count: skipped })
  return out
}
