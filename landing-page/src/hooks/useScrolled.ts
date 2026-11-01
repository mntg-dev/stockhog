import { useEffect, useState } from 'react'

// True once the page has scrolled past the first `offset` pixels. An IntersectionObserver on a
// sentinel at the top of the page, like the reference design's sticky nav, so no scroll listener.
export function useScrolled(offset = 80) {
  const [scrolled, setScrolled] = useState(false)

  useEffect(() => {
    const sentinel = document.createElement('div')
    sentinel.setAttribute('aria-hidden', 'true')
    sentinel.style.cssText = `position:absolute;top:0;left:0;width:1px;height:${offset}px;pointer-events:none;`
    document.body.prepend(sentinel)

    const observer = new IntersectionObserver(([entry]) => setScrolled(!entry.isIntersecting))
    observer.observe(sentinel)

    return () => {
      observer.disconnect()
      sentinel.remove()
    }
  }, [offset])

  return scrolled
}
