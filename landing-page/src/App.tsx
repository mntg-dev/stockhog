import { I18nProvider } from './i18n/I18nProvider'
import { Header } from './sections/Header'
import { Hero } from './sections/Hero'
import { BuiltOn } from './sections/BuiltOn'
import { Features } from './sections/Features'
import { HowItWorks } from './sections/HowItWorks'
import { Screenshots } from './sections/Screenshots'
import { Pricing } from './sections/Pricing'
import { Download } from './sections/Download'
import { Faq } from './sections/Faq'
import { Footer } from './sections/Footer'

function App() {
  return (
    <I18nProvider>
      <div id="top">
        <Header />
        <main id="main">
          <Hero />
          <BuiltOn />
          <Features />
          <HowItWorks />
          <Screenshots />
          <Pricing />
          <Download />
          <Faq />
        </main>
        <Footer />
      </div>
    </I18nProvider>
  )
}

export default App
