import { Plus } from 'lucide-react'
import { useT } from '../i18n/I18nProvider'
import './Faq.css'

export function Faq() {
  const { t } = useT()

  return (
    <section id="faq" className="section section--tint" aria-labelledby="faq-title">
      <div className="container faq">
        <span className="subheading">{t.faq.subheading}</span>
        <h2 id="faq-title" className="heading-secondary">
          {t.faq.title}
        </h2>
        <div className="faq__list">
          {t.faq.items.map((item) => (
            <details key={item.q} className="faq__item">
              <summary>
                {item.q}
                <Plus aria-hidden="true" />
              </summary>
              <p>{item.a}</p>
            </details>
          ))}
        </div>
      </div>
    </section>
  )
}
