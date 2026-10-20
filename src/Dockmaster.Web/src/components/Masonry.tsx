import { useLayoutEffect, useRef, type ReactNode } from 'react'

// the grid's rows are this tall (see .masonry); a card spans as many as its height and the gap under it take
const ROW = 4
const GAP = 16

/**
 * Cards in the columns of a grid, each right under the one above it in its column: a short card does not leave the
 * height of its tallest neighbour empty under it. The cards stay where React put them (nothing is moved between
 * parents, so a half-typed form keeps its text); only how many rows each spans is set, again whenever one changes height.
 */
export default function Masonry({ className, children }: { className?: string; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const box = ref.current
    if (!box) return
    const place = () => {
      for (const el of Array.from(box.children) as HTMLElement[])
        el.style.gridRowEnd = `span ${Math.max(1, Math.ceil((el.getBoundingClientRect().height + GAP) / ROW))}`
    }
    const sizes = new ResizeObserver(place)
    // a card that shows up later (one that waits for its data) is watched too
    const watch = () => {
      sizes.disconnect()
      for (const el of Array.from(box.children)) sizes.observe(el)
      place()
    }
    const cards = new MutationObserver(watch)
    cards.observe(box, { childList: true })
    watch()
    return () => { sizes.disconnect(); cards.disconnect() }
  }, [])
  return <div ref={ref} className={'masonry' + (className ? ' ' + className : '')}>{children}</div>
}
