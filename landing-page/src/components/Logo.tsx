// Copper "piggy bank" square: the coin slot on top and a rising price line.
export function LogoMark({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 32 32" aria-hidden="true">
      <rect width="32" height="32" rx="8" fill="#d26a1a" />
      <rect x="11" y="5.5" width="10" height="2.6" rx="1.3" fill="#fff" opacity="0.85" />
      <polyline
        points="6,24 12,17 17,21 26,11"
        fill="none"
        stroke="#fff"
        strokeWidth="3"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

export function Logo({ href = '#top' }: { href?: string }) {
  return (
    <a className="logo" href={href}>
      <LogoMark className="logo__mark" />
      <span className="logo__text">Stockhog</span>
    </a>
  )
}
