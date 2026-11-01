/** Network priority as people say it; the DSCP code is a detail for routers (shown in tooltips only). */
export const NET_PRIORITIES: { dscp: number; label: string }[] = [
  { dscp: 8, label: 'Low' },
  { dscp: 0, label: 'Normal' },
  { dscp: 34, label: 'High' },
  { dscp: 46, label: 'Highest' },
]

export const netPriorityLabel = (dscp: number | undefined | null) =>
  dscp == null || dscp < 0 ? 'Default' : (NET_PRIORITIES.find((p) => p.dscp === dscp)?.label ?? `Custom (DSCP ${dscp})`)
