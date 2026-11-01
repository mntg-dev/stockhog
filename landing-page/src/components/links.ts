export const REPO_URL = 'https://github.com/mntg-dev/stockhog'
export const RELEASES_URL = `${REPO_URL}/releases`
export const README_URL = `${REPO_URL}#readme`
export const ISSUES_URL = `${REPO_URL}/issues`

// Page sections linked from the header and the footer; labels come from t.header.nav.
export const sectionLinks = [
  { href: '#features', key: 'features' },
  { href: '#how-it-works', key: 'howItWorks' },
  { href: '#screenshots', key: 'screenshots' },
  { href: '#pricing', key: 'pricing' },
  { href: '#faq', key: 'faq' },
] as const

export const builtOn = [
  { name: 'QuantConnect Lean', url: 'https://www.lean.io' },
  { name: 'Alpaca', url: 'https://alpaca.markets' },
  { name: '.NET', url: 'https://dotnet.microsoft.com' },
  { name: 'Claude', url: 'https://www.anthropic.com/claude' },
]
