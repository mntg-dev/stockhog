import { Info, KeyRound, Monitor } from 'lucide-react'
import { useT } from '../i18n/I18nProvider'
import { useLatestRelease } from '../hooks/useLatestRelease'
import { WindowsIcon } from '../components/BrandIcons'
import { RELEASES_URL } from '../components/links'
import './Download.css'

// One icon per item in t.download.requirements, in the same order.
const requirementIcons = [Monitor, KeyRound]

function formatBytes(bytes: number) {
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function formatDate(iso: string, language: string) {
  return new Date(iso).toLocaleDateString(language === 'pl' ? 'pl-PL' : 'en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

export function Download() {
  const { t, language } = useT()
  const { release, status } = useLatestRelease()

  return (
    <section id="download" className="section" aria-labelledby="download-title">
      <div className="container">
        <div className="cta">
          <div className="cta__main">
            <span className="subheading">{t.download.subheading}</span>
            <h2 id="download-title" className="heading-secondary">
              {t.download.title}
            </h2>

            {status === 'loading' && (
              <>
                <p className="cta__meta">{t.download.loading}</p>
                <span className="cta__skeleton" aria-hidden="true" />
              </>
            )}

            {status === 'ready' && release && (
              <>
                <p className="cta__meta">
                  {t.download.version} {release.version} &middot; {t.download.publishedOn}{' '}
                  {formatDate(release.publishedAt, language)} &middot; {t.download.size}{' '}
                  {formatBytes(release.sizeBytes)}
                </p>
                <div className="cta__actions">
                  <a className="btn cta__btn" href={release.assetUrl}>
                    <WindowsIcon />
                    {t.download.downloadButton.replace('{version}', release.version)}
                  </a>
                  {release.portableUrl && (
                    <a className="cta__link" href={release.portableUrl}>
                      {t.download.portable}
                    </a>
                  )}
                  {release.shaAssetUrl && (
                    <a className="cta__link" href={release.shaAssetUrl}>
                      SHA-256
                    </a>
                  )}
                </div>
              </>
            )}

            {status === 'error' && (
              <>
                <p className="cta__meta">{t.download.errorFallback}</p>
                <div className="cta__actions">
                  <a className="btn cta__btn" href={RELEASES_URL}>
                    {t.download.allReleases}
                  </a>
                </div>
              </>
            )}
          </div>

          <div className="cta__panel">
            <h3 className="cta__panel-title">{t.download.requirementsTitle}</h3>
            <ul className="cta__requirements">
              {t.download.requirements.map((item, index) => {
                const Icon = requirementIcons[index] ?? Monitor
                return (
                  <li key={item}>
                    <Icon aria-hidden="true" />
                    {item}
                  </li>
                )
              })}
            </ul>

            <h3 className="cta__panel-title">{t.download.quickStartTitle}</h3>
            <ol className="cta__steps">
              {t.download.quickStart.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ol>
          </div>
        </div>

        <p className="cta__note">
          <Info aria-hidden="true" />
          {t.download.smartScreenNote}
        </p>
      </div>
    </section>
  )
}
