import { useState } from 'react'
import { useT } from '../i18n/I18nProvider'

export function Header() {
  const { t, language, setLanguage } = useT()
  const [menuOpen, setMenuOpen] = useState(false)

  const navItems = [
    { href: '#features', label: t.header.nav.features },
    { href: '#screenshots', label: t.header.nav.screenshots },
    { href: '#pricing', label: t.header.nav.pricing },
    { href: '#download', label: t.header.nav.download },
    { href: '#faq', label: t.header.nav.faq },
  ]

  return (
    <header className="site-header">
      <div className="site-header-inner">
        <a className="brand" href="#top">
          Stockhog
        </a>

        <nav className={`main-nav ${menuOpen ? 'open' : ''}`} aria-label="Primary">
          {navItems.map((item) => (
            <a key={item.href} href={item.href} onClick={() => setMenuOpen(false)}>
              {item.label}
            </a>
          ))}
        </nav>

        <div className="header-actions">
          <div className="lang-switch" role="group" aria-label="Language">
            <button type="button" aria-pressed={language === 'en'} onClick={() => setLanguage('en')}>
              EN
            </button>
            <button type="button" aria-pressed={language === 'pl'} onClick={() => setLanguage('pl')}>
              PL
            </button>
          </div>

          <a className="button primary header-cta" href="#download">
            {t.header.cta}
          </a>

          <button
            type="button"
            className="menu-toggle"
            aria-expanded={menuOpen}
            aria-label="Toggle navigation menu"
            onClick={() => setMenuOpen((open) => !open)}
          >
            &#9776;
          </button>
        </div>
      </div>
    </header>
  )
}
