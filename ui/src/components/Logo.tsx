export function Logo({ size = 24 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 100 100" aria-hidden="true">
      <path d="M77.6 77.6 A39 39 0 1 1 77.6 22.4" fill="none" stroke="#7C8BFF" strokeWidth="11" strokeLinecap="round" />
      <rect x="30" y="46" width="11" height="24" rx="5.5" fill="#5B68E8" />
      <rect x="45" y="28" width="11" height="42" rx="5.5" fill="#2EE6A8" />
      <rect x="60" y="46" width="11" height="24" rx="5.5" fill="#5B68E8" />
    </svg>
  )
}
