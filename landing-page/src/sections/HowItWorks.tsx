import { useT } from '../i18n/I18nProvider'
import './HowItWorks.css'

export function HowItWorks() {
  const { t } = useT()

  return (
    <section id="how-it-works" className="section section--tint" aria-labelledby="how-it-works-title">
      <div className="container">
        <span className="subheading">{t.howItWorks.subheading}</span>
        <h2 id="how-it-works-title" className="heading-secondary">
          {t.howItWorks.title}
        </h2>

        <ol className="steps">
          {t.howItWorks.steps.map((step, index) => (
            <li className="step" key={step.title}>
              <div className="step__text">
                <p className="step__number" aria-hidden="true">
                  {String(index + 1).padStart(2, '0')}
                </p>
                <h3 className="heading-tertiary">{step.title}</h3>
                <p className="step__desc">{step.desc}</p>
              </div>
              <div className="step__img-box">
                <img
                  className="shot"
                  src={`/screenshots/${step.file}`}
                  width={2022}
                  height={1281}
                  alt={step.imageAlt}
                  loading="lazy"
                />
              </div>
            </li>
          ))}
        </ol>
      </div>
    </section>
  )
}
