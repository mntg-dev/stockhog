import {
  ChartCandlestick,
  ChartColumn,
  Database,
  Dices,
  FlaskConical,
  Grid3x3,
  ShieldCheck,
  Sparkles,
} from 'lucide-react'
import { useT } from '../i18n/I18nProvider'
import './Features.css'

// One icon per item in t.features.items, in the same order.
const icons = [Database, FlaskConical, Grid3x3, Dices, ChartColumn, Sparkles, ChartCandlestick, ShieldCheck]

export function Features() {
  const { t } = useT()

  return (
    <section id="features" className="section" aria-labelledby="features-title">
      <div className="container">
        <span className="subheading">{t.features.subheading}</span>
        <h2 id="features-title" className="heading-secondary">
          {t.features.title}
        </h2>

        <ul className="features">
          {t.features.items.map((item, index) => {
            const Icon = icons[index] ?? Sparkles
            return (
              <li className="feature" key={item.title}>
                <span className="feature__icon">
                  <Icon aria-hidden="true" />
                </span>
                <h3 className="feature__title">{item.title}</h3>
                <p className="feature__text">{item.desc}</p>
              </li>
            )
          })}
        </ul>
      </div>
    </section>
  )
}
