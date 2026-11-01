/**
 * The header every page shares (PM-35): the title, the page's actions on the right, and one live sentence that says
 * what is going on, in the same voice as the Autopilot page.
 */
export function PageHeader({ title, actions, beside, children }: { title: string; actions?: React.ReactNode; beside?: React.ReactNode; children?: React.ReactNode }) {
  return (
    <header className="pg-head">
      <div className="pg-title-row">
        <h2 className="page-title">{title}</h2>
        {beside}
        <div className="spacer" />
        {actions && <div className="pg-actions">{actions}</div>}
      </div>
      {children && <p className="pg-status">{children}</p>}
    </header>
  )
}

/** A coloured dot and a label, for roles and event kinds. */
export function Dot({ role, children, title }: { role: string; children: React.ReactNode; title?: string }) {
  return (
    <span className={`count role-${role}`} title={title}>
      <i />
      {children}
    </span>
  )
}

/** "1 process", "3 processes". */
export const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/** "a, b and c". */
export function joinAnd(parts: React.ReactNode[]): React.ReactNode[] {
  return parts.flatMap((p, i) => (i === 0 ? [p] : [i === parts.length - 1 ? ' and ' : ', ', p]))
}
