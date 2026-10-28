import { useT } from '../i18n/I18nProvider'

export function Hero() {
  const { t } = useT()

  return (
    <section className="hero" aria-labelledby="hero-title">
      <div className="hero-text">
        <p className="eyebrow">{t.hero.eyebrow}</p>
        <h1 id="hero-title">{t.hero.title}</h1>
        <p className="lead">{t.hero.lead}</p>
        <div className="actions">
          <a className="button primary" href="https://github.com/mntg-dev/em-trading/releases">
            {t.hero.download}
          </a>
          <a className="button" href="https://github.com/mntg-dev/em-trading">
            {t.hero.viewSource}
          </a>
        </div>
      </div>
      <img
        className="hero-shot"
        src="/screenshots/paper-trading.png"
        width={2040}
        height={1290}
        alt={t.hero.imageAlt}
      />
    </section>
  )
}
