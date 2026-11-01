import { Check } from 'lucide-react'
import { useT } from '../i18n/I18nProvider'
import { GitHubIcon } from '../components/BrandIcons'
import { RELEASES_URL } from '../components/links'
import './Pricing.css'

function CheckList({ items }: { items: string[] }) {
  return (
    <ul className="check-list">
      {items.map((item) => (
        <li key={item}>
          <Check aria-hidden="true" />
          {item}
        </li>
      ))}
    </ul>
  )
}

export function Pricing() {
  const { t } = useT()
  const { free, pro } = t.pricing

  return (
    <section id="pricing" className="section" aria-labelledby="pricing-title">
      <div className="container">
        <span className="subheading">{t.pricing.subheading}</span>
        <h2 id="pricing-title" className="heading-secondary">
          {t.pricing.title}
        </h2>
        <p className="section-lead">{t.pricing.lead}</p>

        <div className="plans">
          <article className="plan plan--free">
            <header className="plan__header">
              <h3 className="plan__name">{free.name}</h3>
              <p className="plan__price">
                {free.price} <span>/ {free.period}</span>
              </p>
            </header>
            <CheckList items={free.items} />
            <a className="btn btn--primary plan__cta" href="#download">
              {free.cta}
            </a>
          </article>

          <article className="plan plan--pro">
            <p className="plan__ribbon">{pro.ribbon}</p>
            <header className="plan__header">
              <h3 className="plan__name">{pro.name}</h3>
              <p className="plan__price">{pro.price}</p>
            </header>
            <CheckList items={pro.items} />
            <a className="btn btn--outline plan__cta" href={RELEASES_URL}>
              <GitHubIcon />
              {pro.cta}
            </a>
          </article>
        </div>
      </div>
    </section>
  )
}
