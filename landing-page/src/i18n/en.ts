export const en = {
  header: {
    nav: {
      features: 'Features',
      screenshots: 'Screenshots',
      pricing: 'Pricing',
      download: 'Download',
      faq: 'FAQ',
    },
    cta: 'Download for Windows',
  },
  hero: {
    eyebrow: 'Windows desktop app',
    title: 'Backtest, optimize and trade US stocks from one app',
    lead:
      'Download market data, test strategies on the QuantConnect Lean engine, find the best parameters, measure the risk with Monte Carlo simulation, and trade on a paper account with live prices.',
    download: 'Download for Windows',
    viewSource: 'View on GitHub',
    imageAlt:
      'Stockhog paper trading page: watchlist with prices, minute candles, order ticket and positions',
  },
  features: {
    title: 'Everything you need before you risk real money',
    subtitle: 'A full research-to-trading workflow, running on your own machine.',
    items: [
      {
        title: 'Market data',
        desc:
          'Download and keep historical bars up to date for any US symbol, with only missing days re-fetched.',
      },
      {
        title: 'Backtesting',
        desc: 'Run strategies on the QuantConnect Lean engine, with several backtests executing in parallel.',
      },
      {
        title: 'Parameter optimization',
        desc: 'Grid-search a strategy’s parameters and compare results on an interactive heatmap.',
      },
      {
        title: 'Monte Carlo risk analysis',
        desc: 'Resample your trade history to see the range of drawdowns you could realistically face.',
      },
      {
        title: 'Paper trading with live data',
        desc: 'Trade on a live paper account with real-time quotes, a watchlist and an order ticket.',
      },
      {
        title: 'Safety first',
        desc: 'Risk limits, a kill switch and an audit log guard every order, with live trading opt-in only.',
      },
    ],
  },
  screenshots: {
    title: 'See it in action',
    items: [
      {
        key: 'paper-trading',
        file: 'paper-trading.png',
        caption: 'Watchlist, live candles, order ticket and positions',
      },
      {
        key: 'backtest',
        file: 'backtest.png',
        caption: 'Backtest results with equity curve and KPIs',
      },
      {
        key: 'backtest-trades',
        file: 'backtest-trades.png',
        caption: 'Trade-by-trade journal for a completed run',
      },
      {
        key: 'optimization',
        file: 'optimization.png',
        caption: 'Parameter optimization heatmap',
      },
      {
        key: 'risk-analysis',
        file: 'risk-analysis.png',
        caption: 'Monte Carlo drawdown risk analysis',
      },
    ],
    lightboxClose: 'Close',
    lightboxNext: 'Next screenshot',
    lightboxPrev: 'Previous screenshot',
  },
  howItWorks: {
    title: 'How it works',
    steps: [
      {
        title: 'Download market data',
        desc: 'Pick your symbols; Stockhog fetches historical bars and keeps them up to date.',
      },
      {
        title: 'Backtest and optimize',
        desc: 'Run a strategy, tune its parameters, and compare results side by side.',
      },
      {
        title: 'Trade on paper, then live',
        desc: 'Watch live prices and trade on a paper account; flip to live only when you choose to.',
      },
    ],
  },
  pricing: {
    title: 'Pricing',
    subtitle: 'Free today for backtesting. Pro adds optimization, risk analysis and live trading.',
    free: {
      name: 'Free',
      price: '$0',
      period: 'forever',
      cta: 'Download for Windows',
      items: [
        'Market data downloads',
        'Unlimited backtests',
        'Run history & comparison',
        'Paper trading',
        'English & Polish',
      ],
    },
    pro: {
      name: 'Pro',
      price: 'TBD',
      period: '',
      cta: 'Coming soon',
      items: [
        'Everything in Free',
        'Parallel parameter optimization',
        'Monte Carlo risk analysis',
        'Live trading with safeguards',
        'Priority support',
      ],
    },
  },
  download: {
    title: 'Download',
    requirementsTitle: 'Requirements',
    requirements: ['Windows 10 or 11, 64-bit', 'A free Alpaca account for market data and paper trading'],
    quickStartTitle: 'Quick start',
    quickStart: [
      'Run the installer (or unzip the portable archive)',
      'Add your Alpaca API keys to appsettings.json (Alpaca:ApiKey / Alpaca:ApiSecret), or set them as environment variables',
      'Run Stockhog.App.exe',
    ],
    smartScreenNote:
      'Windows SmartScreen may warn about an unrecognized app on first launch, since it isn’t code-signed yet. Click “More info”, then “Run anyway”.',
    loading: 'Checking the latest release…',
    errorFallback: 'No release is published yet, or GitHub could not be reached. See all releases on GitHub:',
    allReleases: 'View releases on GitHub',
    version: 'Version',
    publishedOn: 'Published on',
    size: 'Size',
    downloadButton: 'Download {version} for Windows',
    portable: 'Portable .zip',
  },
  faq: {
    title: 'Frequently asked questions',
    items: [
      {
        q: 'Is this financial advice?',
        a: 'No. Stockhog is a tool for research and testing. Nothing in the app or on this page is financial advice.',
      },
      {
        q: 'Paper or live trading?',
        a: 'The app defaults to a paper account. Live trading needs its own API keys and an explicit arm step before any order.',
      },
      {
        q: 'Which data provider is used?',
        a: 'Market data and brokerage come from Alpaca. You connect your own free Alpaca account.',
      },
      {
        q: 'Where are my data and API keys stored?',
        a: 'Everything stays on your computer: local files and your own Alpaca credentials. The app has no telemetry.',
      },
      {
        q: 'What markets are supported?',
        a: 'US equities for now, through Alpaca.',
      },
      {
        q: 'What stops a strategy from losing too much?',
        a: 'A risk policy checks every order against position and daily-loss limits, and a kill switch can halt trading instantly.',
      },
    ],
  },
  footer: {
    github: 'GitHub',
    disclaimer:
      'Not financial advice. Trading involves risk, and past or backtested performance does not guarantee future results.',
    license: 'License: to be announced.',
  },
}
