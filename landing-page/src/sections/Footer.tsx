import { useT } from '../i18n/I18nProvider'

export function Footer() {
  const { t } = useT()

  return (
    <footer className="site-footer">
      <div className="section-inner">
        <a href="https://github.com/mntg-dev/stockhog">{t.footer.github}</a>
        <p className="disclaimer">{t.footer.disclaimer}</p>
        <p className="license">{t.footer.license}</p>
      </div>
    </footer>
  )
}
