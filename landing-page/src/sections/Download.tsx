import { useT } from '../i18n/I18nProvider'
import { useLatestRelease } from '../hooks/useLatestRelease'

const RELEASES_PAGE_URL = 'https://github.com/mntg-dev/stockhog/releases'

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
    <section id="download" className="download" aria-labelledby="download-title">
      <div className="section-inner">
        <h2 id="download-title">{t.download.title}</h2>

        <div className="download-card">
          {status === 'loading' && <p>{t.download.loading}</p>}

          {status === 'ready' && release && (
            <>
              <p className="download-meta">
                {t.download.version} {release.version} &middot; {t.download.publishedOn}{' '}
                {formatDate(release.publishedAt, language)} &middot; {t.download.size} {formatBytes(release.sizeBytes)}
              </p>
              <a className="button primary" href={release.assetUrl}>
                {t.download.downloadButton.replace('{version}', release.version)}
              </a>
              {release.shaAssetUrl && (
                <a className="sha-link" href={release.shaAssetUrl}>
                  SHA-256
                </a>
              )}
              {release.portableUrl && (
                <a className="sha-link" href={release.portableUrl}>
                  {t.download.portable}
                </a>
              )}
            </>
          )}

          {status === 'error' && (
            <>
              <p>{t.download.errorFallback}</p>
              <a className="button primary" href={RELEASES_PAGE_URL}>
                {t.download.allReleases}
              </a>
            </>
          )}
        </div>

        <div className="download-details">
          <div>
            <h3>{t.download.requirementsTitle}</h3>
            <ul>
              {t.download.requirements.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          </div>
          <div>
            <h3>{t.download.quickStartTitle}</h3>
            <ol>
              {t.download.quickStart.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ol>
          </div>
        </div>

        <p className="smartscreen-note">{t.download.smartScreenNote}</p>
      </div>
    </section>
  )
}
