import { useT } from '../i18n/I18nProvider'
import { Logo } from '../components/Logo'
import { GitHubIcon } from '../components/BrandIcons'
import { LanguageSwitch } from '../components/LanguageSwitch'
import { builtOn, ISSUES_URL, README_URL, RELEASES_URL, REPO_URL, sectionLinks } from '../components/links'
import './Footer.css'

const currentYear = new Date().getFullYear()

export function Footer() {
  const { t } = useT()

  const product = sectionLinks.map((link) => ({ href: link.href, label: t.header.nav[link.key] }))
  const resources = [
    { href: '#download', label: t.header.cta },
    { href: RELEASES_URL, label: t.footer.releases },
    { href: README_URL, label: t.footer.readme },
    { href: ISSUES_URL, label: t.footer.issues },
  ]

  return (
    <footer className="footer">
      <div className="container">
        <div className="footer__grid">
          <div className="footer__brand">
            <Logo />
            <p className="footer__tagline">{t.footer.tagline}</p>
            <a className="footer__social" href={REPO_URL} aria-label={t.footer.github}>
              <GitHubIcon />
            </a>
          </div>

          <nav className="footer__col" aria-labelledby="footer-product">
            <h2 id="footer-product" className="footer__heading">
              {t.footer.product}
            </h2>
            <ul className="footer__links">
              {product.map((link) => (
                <li key={link.href}>
                  <a className="footer__link" href={link.href}>
                    {link.label}
                  </a>
                </li>
              ))}
            </ul>
          </nav>

          <nav className="footer__col" aria-labelledby="footer-resources">
            <h2 id="footer-resources" className="footer__heading">
              {t.footer.resources}
            </h2>
            <ul className="footer__links">
              {resources.map((link) => (
                <li key={link.href}>
                  <a className="footer__link" href={link.href}>
                    {link.label}
                  </a>
                </li>
              ))}
            </ul>
          </nav>

          <nav className="footer__col" aria-labelledby="footer-built-on">
            <h2 id="footer-built-on" className="footer__heading">
              {t.footer.builtOn}
            </h2>
            <ul className="footer__links">
              {builtOn.map((item) => (
                <li key={item.name}>
                  <a className="footer__link" href={item.url}>
                    {item.name}
                  </a>
                </li>
              ))}
            </ul>
          </nav>

          <div className="footer__col">
            <h2 className="footer__heading">{t.footer.language}</h2>
            <LanguageSwitch dark />
          </div>
        </div>

        <div className="footer__bottom">
          <p>{t.footer.disclaimer}</p>
          <p>
            &copy; {currentYear} Stockhog &middot; {t.footer.license}
          </p>
        </div>
      </div>
    </footer>
  )
}
