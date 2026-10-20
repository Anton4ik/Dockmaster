import type { ReactNode } from 'react'

/** Two-column table of facts; rows without a value are left out. */
export default function KeyValues({ rows, empty }: { rows: [string, ReactNode][]; empty?: string }) {
  const shown = rows.filter(([, v]) => v !== null && v !== undefined && v !== '' && v !== false)
  return (
    <div className="table-wrap"><table className="kv-table">
      <tbody>
        {shown.map(([k, v]) => <tr key={k}><th scope="row">{k}</th><td>{v}</td></tr>)}
        {shown.length === 0 && <tr><td className="muted">{empty ?? 'nothing'}</td></tr>}
      </tbody>
    </table></div>
  )
}
