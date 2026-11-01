import { useT } from '../i18n/I18nProvider'
import { builtOn } from '../components/links'
import './BuiltOn.css'

// The reference design's "As featured in" strip, done honestly: the projects Stockhog is built on.
export function BuiltOn() {
  const { t } = useT()

  return (
    <section className="built-on" aria-labelledby="built-on-title">
      <div className="container">
        <h2 id="built-on-title" className="built-on__title">
          {t.builtOn.label}
        </h2>
        <ul className="built-on__list">
          {builtOn.map((item) => (
            <li key={item.name}>
              <a className="built-on__mark" href={item.url}>
                {item.name}
              </a>
            </li>
          ))}
        </ul>
      </div>
    </section>
  )
}
