import type { en } from './en'

export const pl: typeof en = {
  header: {
    nav: {
      features: 'Funkcje',
      screenshots: 'Zrzuty ekranu',
      pricing: 'Cennik',
      download: 'Pobierz',
      faq: 'FAQ',
    },
    cta: 'Pobierz dla Windows',
  },
  hero: {
    eyebrow: 'Aplikacja desktopowa dla Windows',
    title: 'Testuj, optymalizuj i handluj akcjami z USA w jednej aplikacji',
    lead:
      'Pobieraj dane rynkowe, testuj strategie na silniku QuantConnect Lean, znajdź najlepsze parametry, zmierz ryzyko symulacją Monte Carlo i handluj na koncie demo z notowaniami na żywo.',
    download: 'Pobierz dla Windows',
    viewSource: 'Zobacz na GitHub',
    imageAlt:
      'Strona handlu na koncie demo w Stockhog: lista obserwowanych instrumentów, świece minutowe, zlecenie i pozycje',
  },
  features: {
    title: 'Wszystko, czego potrzebujesz, zanim zaryzykujesz prawdziwe pieniądze',
    subtitle: 'Pełny proces od badania strategii do handlu, działający na Twoim komputerze.',
    items: [
      {
        title: 'Dane rynkowe',
        desc:
          'Pobieraj i aktualizuj historyczne notowania dla każdego instrumentu z USA — dociągane są tylko brakujące dni.',
      },
      {
        title: 'Backtesting',
        desc: 'Testuj strategie na silniku QuantConnect Lean, z kilkoma backtestami uruchomionymi równolegle.',
      },
      {
        title: 'Optymalizacja parametrów',
        desc: 'Przeszukaj parametry strategii metodą grid-search i porównaj wyniki na interaktywnej mapie cieplnej.',
      },
      {
        title: 'Analiza ryzyka Monte Carlo',
        desc: 'Przelosuj historię transakcji, aby zobaczyć realny zakres możliwych obsunięć kapitału.',
      },
      {
        title: 'Handel demo',
        desc: 'Handluj na koncie demo z notowaniami w czasie rzeczywistym, listą obserwowanych i zleceniem.',
      },
      {
        title: 'Bezpieczeństwo na pierwszym miejscu',
        desc:
          'Limity ryzyka, wyłącznik awaryjny i log audytowy chronią każde zlecenie, a handel na żywo wymaga świadomego włączenia.',
      },
    ],
  },
  screenshots: {
    title: 'Zobacz aplikację w akcji',
    items: [
      {
        key: 'paper-trading',
        file: 'paper-trading.png',
        caption: 'Lista obserwowanych, świece na żywo, zlecenie i pozycje',
      },
      {
        key: 'backtest',
        file: 'backtest.png',
        caption: 'Wyniki backtestu z krzywą kapitału i kluczowymi wskaźnikami',
      },
      {
        key: 'backtest-trades',
        file: 'backtest-trades.png',
        caption: 'Dziennik transakcji dla zakończonego przebiegu',
      },
      {
        key: 'optimization',
        file: 'optimization.png',
        caption: 'Mapa cieplna optymalizacji parametrów',
      },
      {
        key: 'risk-analysis',
        file: 'risk-analysis.png',
        caption: 'Analiza ryzyka obsunięć metodą Monte Carlo',
      },
    ],
    lightboxClose: 'Zamknij',
    lightboxNext: 'Następny zrzut ekranu',
    lightboxPrev: 'Poprzedni zrzut ekranu',
  },
  howItWorks: {
    title: 'Jak to działa',
    steps: [
      {
        title: 'Pobierz dane rynkowe',
        desc: 'Wybierz instrumenty — Stockhog pobiera historyczne notowania i trzyma je na bieżąco.',
      },
      {
        title: 'Testuj i optymalizuj',
        desc: 'Uruchom strategię, dostosuj jej parametry i porównaj wyniki jeden przy drugim.',
      },
      {
        title: 'Handluj na demo, potem na żywo',
        desc:
          'Obserwuj notowania na żywo i handluj na koncie demo; przełącz na handel na żywo tylko, gdy sam zdecydujesz.',
      },
    ],
  },
  pricing: {
    title: 'Cennik',
    subtitle: 'Backtesting bezpłatnie już teraz. Pro dodaje optymalizację, analizę ryzyka i handel na żywo.',
    free: {
      name: 'Free',
      price: '0 zł',
      period: 'na zawsze',
      cta: 'Pobierz dla Windows',
      items: [
        'Pobieranie danych rynkowych',
        'Nielimitowane backtesty',
        'Historia i porównanie przebiegów',
        'Handel demo',
        'Polski i angielski',
      ],
    },
    pro: {
      name: 'Pro',
      price: 'Wkrótce',
      period: '',
      cta: 'Wkrótce',
      items: [
        'Wszystko z Free',
        'Równoległa optymalizacja parametrów',
        'Analiza ryzyka Monte Carlo',
        'Handel na żywo z zabezpieczeniami',
        'Priorytetowe wsparcie',
      ],
    },
  },
  download: {
    title: 'Pobierz',
    requirementsTitle: 'Wymagania',
    requirements: ['Windows 10 lub 11, 64-bit', 'Darmowe konto Alpaca do danych rynkowych i handlu demo'],
    quickStartTitle: 'Szybki start',
    quickStart: [
      'Uruchom instalator (albo rozpakuj wersję przenośną)',
      'Dodaj swoje klucze API Alpaca do appsettings.json (Alpaca:ApiKey / Alpaca:ApiSecret) albo ustaw je jako zmienne środowiskowe',
      'Uruchom Stockhog.App.exe',
    ],
    smartScreenNote:
      'Windows SmartScreen może ostrzec przed nierozpoznaną aplikacją przy pierwszym uruchomieniu, bo nie jest jeszcze podpisana cyfrowo. Kliknij „Więcej informacji”, a potem „Uruchom mimo to”.',
    loading: 'Sprawdzanie najnowszego wydania…',
    errorFallback: 'Żadne wydanie nie zostało jeszcze opublikowane albo GitHub jest niedostępny. Zobacz wszystkie wydania na GitHub:',
    allReleases: 'Zobacz wydania na GitHub',
    version: 'Wersja',
    publishedOn: 'Opublikowano',
    size: 'Rozmiar',
    downloadButton: 'Pobierz {version} dla Windows',
    portable: 'Wersja przenośna .zip',
  },
  faq: {
    title: 'Najczęściej zadawane pytania',
    items: [
      {
        q: 'Czy to porada finansowa?',
        a: 'Nie. Stockhog to narzędzie do badań i testowania. Nic w aplikacji ani na tej stronie nie jest poradą finansową.',
      },
      {
        q: 'Konto demo czy prawdziwe?',
        a: 'Aplikacja domyślnie używa konta demo. Handel na żywo wymaga własnych kluczy API i wyraźnego włączenia przed złożeniem jakiegokolwiek zlecenia.',
      },
      {
        q: 'Z jakiego dostawcy danych korzysta aplikacja?',
        a: 'Dane rynkowe i realizacja zleceń pochodzą od Alpaca. Łączysz własne, darmowe konto Alpaca.',
      },
      {
        q: 'Gdzie są przechowywane moje dane i klucze API?',
        a: 'Wszystko zostaje na Twoim komputerze: lokalne pliki i Twoje własne dane logowania do Alpaca. Aplikacja nie wysyła żadnej telemetrii.',
      },
      {
        q: 'Jakie rynki są wspierane?',
        a: 'Obecnie akcje z USA, za pośrednictwem Alpaca.',
      },
      {
        q: 'Co chroni przed nadmierną stratą strategii?',
        a: 'Polityka ryzyka sprawdza każde zlecenie względem limitów pozycji i dziennej straty, a wyłącznik awaryjny może natychmiast zatrzymać handel.',
      },
    ],
  },
  footer: {
    github: 'GitHub',
    disclaimer:
      'To nie jest porada finansowa. Handel wiąże się z ryzykiem, a wyniki historyczne lub z backtestów nie gwarantują przyszłych rezultatów.',
    license: 'Licencja: zostanie ogłoszona.',
  },
}
