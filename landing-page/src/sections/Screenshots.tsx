import { useRef, useState, type KeyboardEvent } from 'react'
import { ChevronLeft, ChevronRight, X } from 'lucide-react'
import { useT } from '../i18n/I18nProvider'
import './Screenshots.css'

export function Screenshots() {
  const { t } = useT()
  const items = t.screenshots.items
  const [activeIndex, setActiveIndex] = useState(0)
  const dialogRef = useRef<HTMLDialogElement>(null)

  const active = items[activeIndex]

  function show(delta: number) {
    setActiveIndex((current) => (current + delta + items.length) % items.length)
  }

  function openLightbox(index: number) {
    setActiveIndex(index)
    dialogRef.current?.showModal()
  }

  function onDialogKeyDown(event: KeyboardEvent<HTMLDialogElement>) {
    if (event.key === 'ArrowRight') show(1)
    if (event.key === 'ArrowLeft') show(-1)
  }

  return (
    <section id="screenshots" className="section gallery" aria-labelledby="screenshots-title">
      <div className="container">
        <span className="subheading">{t.screenshots.subheading}</span>
        <h2 id="screenshots-title" className="heading-secondary">
          {t.screenshots.title}
        </h2>

        <ul className="gallery__grid">
          {items.map((item, index) => (
            <li key={item.key} className="gallery__item">
              <button
                type="button"
                className="gallery__button"
                onClick={() => openLightbox(index)}
                aria-label={t.screenshots.open.replace('{caption}', item.caption)}
              >
                <figure>
                  <img src={`/screenshots/${item.file}`} alt="" width={2040} height={1290} loading="lazy" />
                  <figcaption>{item.caption}</figcaption>
                </figure>
              </button>
            </li>
          ))}
        </ul>
      </div>

      <dialog ref={dialogRef} className="lightbox" onKeyDown={onDialogKeyDown} aria-label={active.caption}>
        <button
          type="button"
          className="lightbox__btn lightbox__close"
          onClick={() => dialogRef.current?.close()}
          aria-label={t.screenshots.lightboxClose}
        >
          <X aria-hidden="true" />
        </button>
        <button
          type="button"
          className="lightbox__btn lightbox__prev"
          onClick={() => show(-1)}
          aria-label={t.screenshots.lightboxPrev}
        >
          <ChevronLeft aria-hidden="true" />
        </button>
        <img src={`/screenshots/${active.file}`} alt={active.caption} />
        <button
          type="button"
          className="lightbox__btn lightbox__next"
          onClick={() => show(1)}
          aria-label={t.screenshots.lightboxNext}
        >
          <ChevronRight aria-hidden="true" />
        </button>
        <p className="lightbox__caption">{active.caption}</p>
      </dialog>
    </section>
  )
}
