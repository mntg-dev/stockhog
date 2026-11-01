import { useT, type Language } from '../i18n/I18nProvider'

const languages: Language[] = ['en', 'pl']

export function LanguageSwitch({ dark = false }: { dark?: boolean }) {
  const { t, language, setLanguage } = useT()

  return (
    <div className={`lang-switch${dark ? ' lang-switch--dark' : ''}`} role="group" aria-label={t.header.language}>
      {languages.map((code) => (
        <button key={code} type="button" aria-pressed={language === code} onClick={() => setLanguage(code)}>
          {code.toUpperCase()}
        </button>
      ))}
    </div>
  )
}
