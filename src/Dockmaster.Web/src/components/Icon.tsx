/**
 * A line icon on a 24×24 grid, stroked in the text colour (paths in icons.ts). Decorative: the button or link around
 * it carries the name. Line icons, because glyphs like ⤓ sit off-centre in most fonts.
 */
export default function Icon({ d, size = 18, stroke = 1.8, className }: { d: string; size?: number; stroke?: number; className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" width={size} height={size} fill="none" stroke="currentColor" strokeWidth={stroke}
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={d} /></svg>
  )
}
