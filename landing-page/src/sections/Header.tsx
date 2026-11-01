import { useEffect, useRef, useState } from 'react'
import { Menu, X } from 'lucide-react'
import { useT } from '../i18n/I18nProvider'
import { Logo } from '../components/Logo'
import { LanguageSwitch } from '../components/LanguageSwitch'
import { sectionLinks } from '../components/links'
import { useScrolled } from '../hooks/useScrolled'
import { useMediaQuery } from '../hooks/useMediaQuery'
import './Header.css'

export function Header() {
  const { t } = useT()
  const [menuOpen, setMenuOpen] = useState(false)
  const scrolled = useScrolled()
  const isMobile = useMediaQuery('(max-width: 59em)')
  const [wasMobile, setWasMobile] = useState(isMobile)
  const navRef = useRef<HTMLElement>(null)
  const menuButtonRef = useRef<HTMLButtonElement>(null)

  // Leaving the mobile layout closes the menu, so it doesn't pop up again on the way back.
  if (wasMobile !== isMobile) {
    setWasMobile(isMobile)
    if (!isMobile) setMenuOpen(false)
  }

  const overlayOpen = isMobile && menuOpen

  // While the overlay is open: lock scrolling, make the page behind it inert, and move focus into it.
  useEffect(() => {
    if (!overlayOpen) return
    const background = [document.getElementById('main'), document.querySelector('footer')]
    document.documentElement.style.overflow = 'hidden'
    background.forEach((element) => element?.setAttribute('inert', ''))
    navRef.current?.querySelector('a')?.focus()

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      setMenuOpen(false)
      menuButtonRef.current?.focus()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => {
      document.documentElement.style.overflow = ''
      background.forEach((element) => element?.removeAttribute('inert'))
      window.removeEventListener('keydown', onKeyDown)
    }
  }, [overlayOpen])

  const classes = ['header', scrolled && 'header--scrolled', overlayOpen && 'header--open'].filter(Boolean).join(' ')

  return (
    <header className={classes}>
      <a className="skip-link" href="#main">
        {t.header.skipLink}
      </a>

      <div className="header__inner">
        <Logo />

        <nav ref={navRef} id="main-nav" className="main-nav" aria-label="Primary" inert={isMobile && !menuOpen}>
          <ul className="main-nav__list">
            {sectionLinks.map((link) => (
              <li key={link.href}>
                <a className="main-nav__link" href={link.href} onClick={() => setMenuOpen(false)}>
                  {t.header.nav[link.key]}
                </a>
              </li>
            ))}
            <li className="main-nav__mobile-only">
              <a className="btn btn--primary" href="#download" onClick={() => setMenuOpen(false)}>
                {t.header.cta}
              </a>
            </li>
            <li className="main-nav__mobile-only">
              <LanguageSwitch />
            </li>
          </ul>
        </nav>

        <div className="header__actions">
          <LanguageSwitch />
          <a className="btn btn--primary btn--small" href="#download">
            {t.header.cta}
          </a>
        </div>

        <button
          ref={menuButtonRef}
          type="button"
          className="header__menu-btn"
          aria-expanded={overlayOpen}
          aria-controls="main-nav"
          aria-label={overlayOpen ? t.header.closeMenu : t.header.openMenu}
          onClick={() => setMenuOpen((open) => !open)}
        >
          {overlayOpen ? <X aria-hidden="true" /> : <Menu aria-hidden="true" />}
        </button>
      </div>
    </header>
  )
}
