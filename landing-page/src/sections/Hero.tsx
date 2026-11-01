import { useMemo } from 'react'
import { useT } from '../i18n/I18nProvider'
import { GitHubIcon, WindowsIcon } from '../components/BrandIcons'
import { REPO_URL } from '../components/links'
import './Hero.css'

// The two headline results visible in backtest.png: update them whenever that screenshot is retaken.
const exampleRun = { annualReturn: 0.04954, maxDrawdown: -0.109 }

export function Hero() {
  const { t, language } = useT()
  const percent = useMemo(
    () =>
      new Intl.NumberFormat(language === 'pl' ? 'pl-PL' : 'en-US', {
        style: 'percent',
        maximumFractionDigits: 2,
        signDisplay: 'always',
      }),
    [language],
  )

  return (
    <section className="hero" aria-labelledby="hero-title">
      <div className="hero__inner">
        <div className="hero__text">
          <p className="hero__badge">{t.hero.badge}</p>
          <h1 id="hero-title" className="heading-primary">
            {t.hero.title}
          </h1>
          <p className="hero__lead">{t.hero.lead}</p>

          <div className="hero__actions">
            <a className="btn btn--primary" href="#download">
              <WindowsIcon />
              {t.hero.download}
            </a>
            <a className="btn btn--outline" href={REPO_URL}>
              <GitHubIcon />
              {t.hero.viewSource}
            </a>
          </div>

          <dl className="hero__proof">
            {t.hero.proof.map((item) => (
              <div key={item.label} className="hero__proof-item">
                <dt>{item.label}</dt>
                <dd>{item.value}</dd>
              </div>
            ))}
          </dl>
        </div>

        <div className="hero__media">
          <img
            className="shot"
            src="/screenshots/backtest.png"
            width={2040}
            height={1290}
            alt={t.hero.imageAlt}
            fetchPriority="high"
          />
          <div className="hero__card">
            <p className="hero__card-caption">{t.hero.card.caption}</p>
            <div className="hero__card-stats">
              <p>
                <span>{t.hero.card.annualReturn}</span>
                <strong className="profit">{percent.format(exampleRun.annualReturn)}</strong>
              </p>
              <p>
                <span>{t.hero.card.maxDrawdown}</span>
                <strong className="loss">{percent.format(exampleRun.maxDrawdown)}</strong>
              </p>
            </div>
          </div>
        </div>
      </div>
    </section>
  )
}
