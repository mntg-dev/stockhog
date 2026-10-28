import { useEffect, useState } from 'react'

export interface LatestRelease {
  version: string
  publishedAt: string
  assetName: string
  assetUrl: string
  sizeBytes: number
  shaAssetUrl?: string
  portableUrl?: string
}

type Status = 'loading' | 'ready' | 'error'

const CACHE_KEY = 'emtrading-latest-release-v2'
// Written by the release workflow next to the installers in Azure Storage.
const MANIFEST_URL = 'https://emtradingstorage.blob.core.windows.net/releases/latest.json'
const RELEASES_API_URL = 'https://api.github.com/repos/mntg-dev/em-trading/releases/latest'

interface ManifestFile {
  name: string
  url: string
  sha256Url: string
  size: number
}

interface ReleaseManifest {
  version: string
  publishedAt: string
  msi: ManifestFile
  zip?: ManifestFile
}

interface GitHubAsset {
  name: string
  size: number
  browser_download_url: string
}

interface GitHubRelease {
  tag_name: string
  published_at: string
  assets: GitHubAsset[]
}

async function fetchJson<T>(url: string, headers?: HeadersInit): Promise<T> {
  const response = await fetch(url, { headers })
  if (!response.ok) throw new Error(`${url} returned ${response.status}`)
  return (await response.json()) as T
}

async function fromManifest(): Promise<LatestRelease> {
  const manifest = await fetchJson<ReleaseManifest>(MANIFEST_URL)
  return {
    version: manifest.version,
    publishedAt: manifest.publishedAt,
    assetName: manifest.msi.name,
    assetUrl: manifest.msi.url,
    sizeBytes: manifest.msi.size,
    shaAssetUrl: manifest.msi.sha256Url,
    portableUrl: manifest.zip?.url,
  }
}

// Fallback for when the storage manifest is missing, e.g. before the first upload.
async function fromGitHub(): Promise<LatestRelease> {
  const data = await fetchJson<GitHubRelease>(RELEASES_API_URL, { Accept: 'application/vnd.github+json' })

  const msiAsset = data.assets.find((asset) => asset.name.endsWith('.msi'))
  const zipAsset = data.assets.find((asset) => asset.name.endsWith('.zip'))
  const primary = msiAsset ?? zipAsset
  if (!primary) throw new Error('The latest release has no .msi or .zip asset')
  const shaAsset = data.assets.find((asset) => asset.name === `${primary.name}.sha256`)

  return {
    version: data.tag_name,
    publishedAt: data.published_at,
    assetName: primary.name,
    assetUrl: primary.browser_download_url,
    sizeBytes: primary.size,
    shaAssetUrl: shaAsset?.browser_download_url,
    portableUrl: msiAsset ? zipAsset?.browser_download_url : undefined,
  }
}

export function useLatestRelease() {
  const [release, setRelease] = useState<LatestRelease | null>(null)
  const [status, setStatus] = useState<Status>('loading')

  useEffect(() => {
    const cached = window.sessionStorage.getItem(CACHE_KEY)
    if (cached) {
      setRelease(JSON.parse(cached) as LatestRelease)
      setStatus('ready')
      return
    }

    let cancelled = false

    fromManifest()
      .catch(fromGitHub)
      .then((parsed) => {
        if (cancelled) return
        window.sessionStorage.setItem(CACHE_KEY, JSON.stringify(parsed))
        setRelease(parsed)
        setStatus('ready')
      })
      .catch(() => {
        if (!cancelled) setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [])

  return { release, status }
}
