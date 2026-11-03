import type { en } from './en'

export const pl: typeof en = {
  header: {
    nav: {
      features: 'Funkcje',
      howItWorks: 'Jak to działa',
      screenshots: 'Zrzuty ekranu',
      pricing: 'Cennik',
      faq: 'FAQ',
    },
    cta: 'Pobierz',
    skipLink: 'Przejdź do treści',
    openMenu: 'Otwórz menu',
    closeMenu: 'Zamknij menu',
    language: 'Język',
  },
  hero: {
    badge: 'Darmowa · Open source · Windows',
    title: 'Testuj, optymalizuj i handluj akcjami z USA w jednej aplikacji',
    lead:
      'Pobieraj dane rynkowe, testuj strategie na silniku QuantConnect Lean, znajdź najlepsze parametry, zmierz ryzyko symulacją Monte Carlo i handluj na koncie demo z notowaniami na żywo.',
    download: 'Pobierz dla Windows',
    viewSource: 'Zobacz na GitHub',
    imageAlt:
      'Strona backtestu w Stockhog: ustawienia strategii, krzywa kapitału, wykres obsunięć i kluczowe wyniki dla AAPL',
    proof: [
      { value: '7', label: 'wbudowanych strategii' },
      { value: '1 min', label: 'świece dla akcji z USA' },
      { value: '0', label: 'telemetrii' },
    ],
    card: {
      caption: 'Przykładowy przebieg, AAPL',
      annualReturn: 'Zwrot roczny',
      maxDrawdown: 'Maks. obsunięcie',
    },
  },
  builtOn: {
    label: 'Zbudowane na',
  },
  features: {
    subheading: 'Funkcje',
    title: 'Wszystko, czego potrzebujesz, zanim zaryzykujesz prawdziwe pieniądze',
    items: [
      {
        title: 'Dane rynkowe',
        desc: 'Pobieraj świece minutowe dla każdej akcji z USA i trzymaj je na bieżąco. Dociągane są tylko brakujące dni.',
      },
      {
        title: 'Backtesting',
        desc: 'Uruchamiaj 7 wbudowanych strategii na silniku QuantConnect Lean, kilka backtestów równolegle.',
      },
      {
        title: 'Optymalizacja parametrów',
        desc: 'Przeszukaj siatkę parametrów na mapie cieplnej, a potem sprawdź najlepsze na innym okresie.',
      },
      {
        title: 'Ryzyko Monte Carlo',
        desc: 'Przelosuj swoje transakcje, aby zobaczyć realny zakres możliwych obsunięć kapitału.',
      },
      {
        title: 'Porównanie instrumentów',
        desc: 'Przetestuj jedną strategię na maksymalnie 100 instrumentach, a potem sortuj, filtruj i porównuj wyniki.',
      },
      {
        title: 'Asystent AI',
        desc: 'Zapytaj Claude o swoje przebiegi i pozycje. Może wypełnić zlecenie, ale nigdy go nie wysyła.',
      },
      {
        title: 'Handel demo',
        desc: 'Handluj na koncie demo z notowaniami na żywo, świecami minutowymi, listą obserwowanych i zleceniem.',
      },
      {
        title: 'Bezpieczeństwo przede wszystkim',
        desc: 'Limity ryzyka, wyłącznik awaryjny i log audytowy chronią każde zlecenie. Handel na żywo trzeba świadomie włączyć.',
      },
    ],
  },
  howItWorks: {
    subheading: 'Jak to działa',
    title: 'Od pomysłu do sprawdzonej strategii w czterech krokach',
    steps: [
      {
        title: 'Pobierz dane i uruchom backtest',
        desc:
          'Wybierz instrument i strategię. Stockhog pobiera świece minutowe z Alpaca i uruchamia backtest na silniku Lean, a każda transakcja trafia do dziennika.',
        file: 'backtest-trades.png',
        imageAlt: 'Dziennik transakcji zakończonego backtestu, jeden wiersz na transakcję',
      },
      {
        title: 'Optymalizuj i sprawdź na innym okresie',
        desc:
          'Przeszukaj siatkę parametrów i odczytaj wyniki z mapy cieplnej. Uruchom najlepsze próby na okresie, którego nie widziały, aby wyłapać przeuczenie.',
        file: 'optimization.png',
        imageAlt: 'Mapa cieplna optymalizacji parametrów z listą najlepszych prób',
      },
      {
        title: 'Sprawdź odporność na obsunięcia',
        desc:
          'Symulacja Monte Carlo tysiące razy przetasowuje Twoje transakcje i pokazuje, jak głębokie może być obsunięcie kapitału.',
        file: 'risk-analysis.png',
        imageAlt: 'Rozkład obsunięć kapitału z symulacji Monte Carlo z percentylami',
      },
      {
        title: 'Handluj na demo, na żywo tylko gdy zechcesz',
        desc:
          'Obserwuj notowania na żywo i handluj na koncie demo. Handel na żywo wymaga osobnych kluczy i wyraźnego uzbrojenia.',
        file: 'paper-trading.png',
        imageAlt: 'Strona handlu demo z listą obserwowanych, świecami minutowymi, zleceniem i pozycjami',
      },
    ],
  },
  screenshots: {
    subheading: 'Zrzuty ekranu',
    title: 'Zobacz aplikację w akcji',
    items: [
      {
        key: 'backtest',
        file: 'backtest.png',
        caption: 'Wyniki backtestu z krzywą kapitału i kluczowymi wskaźnikami',
      },
      {
        key: 'paper-trading',
        file: 'paper-trading.png',
        caption: 'Lista obserwowanych, świece na żywo, zlecenie i pozycje',
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
    open: 'Otwórz w pełnym rozmiarze: {caption}',
    lightboxClose: 'Zamknij',
    lightboxNext: 'Następny zrzut ekranu',
    lightboxPrev: 'Poprzedni zrzut ekranu',
  },
  pricing: {
    subheading: 'Cennik',
    title: 'Free już dziś, Pro wkrótce',
    lead: 'Backtesting bezpłatnie już teraz. Pro dodaje optymalizację, analizę ryzyka i handel na żywo.',
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
      price: 'Do ustalenia',
      ribbon: 'Wkrótce',
      cta: 'Obserwuj wydania na GitHub',
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
    subheading: 'Pobierz',
    title: 'Pobierz Stockhog dla Windows',
    requirementsTitle: 'Wymagania',
    requirements: ['Windows 10 lub 11, 64-bit', 'Darmowe konto Alpaca do danych rynkowych i handlu demo'],
    quickStartTitle: 'Szybki start',
    quickStart: [
      'Uruchom instalator (albo rozpakuj wersję przenośną) i otwórz Stockhog',
      'Otwórz Ustawienia, wklej klucze konta paper Alpaca i kliknij „Sprawdź połączenie”, a potem „Zapisz klucze”',
      'Uruchom Stockhog ponownie: dane rynkowe i handel na koncie paper są gotowe',
    ],
    smartScreenNote:
      'Windows SmartScreen może ostrzec przed nierozpoznaną aplikacją przy pierwszym uruchomieniu, bo nie jest jeszcze podpisana cyfrowo. Kliknij „Więcej informacji”, a potem „Uruchom mimo to”.',
    loading: 'Sprawdzanie najnowszego wydania…',
    errorFallback: 'Żadne wydanie nie zostało jeszcze opublikowane albo nie udało się pobrać informacji o wydaniu. Zobacz wszystkie wydania na GitHub.',
    allReleases: 'Zobacz wydania na GitHub',
    version: 'Wersja',
    publishedOn: 'Opublikowano',
    size: 'Rozmiar',
    downloadButton: 'Pobierz {version} dla Windows',
    portable: 'Wersja przenośna .zip',
  },
  faq: {
    subheading: 'FAQ',
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
        a: 'Na Twoim komputerze: lokalne pliki oraz klucze Alpaca zaszyfrowane dla Twojego użytkownika Windows (DPAPI). Aplikacja nie wysyła telemetrii. Jeśli włączysz opcjonalnego asystenta AI, Twoje pytania i wyniki, które on czyta, trafiają do Anthropic z Twoim własnym kluczem API.',
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
    tagline: 'Testuj, optymalizuj i handluj akcjami z USA na własnym komputerze.',
    product: 'Produkt',
    resources: 'Zasoby',
    builtOn: 'Zbudowane na',
    language: 'Język',
    releases: 'Wydania',
    readme: 'Dokumentacja',
    issues: 'Zgłoś problem',
    github: 'Stockhog na GitHub',
    disclaimer:
      'To nie jest porada finansowa. Handel wiąże się z ryzykiem, a wyniki historyczne lub z backtestów nie gwarantują przyszłych rezultatów.',
    license: 'Licencja: zostanie ogłoszona.',
  },
}
