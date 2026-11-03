export const en = {
  header: {
    nav: {
      features: 'Features',
      howItWorks: 'How it works',
      screenshots: 'Screenshots',
      pricing: 'Pricing',
      faq: 'FAQ',
    },
    cta: 'Download',
    skipLink: 'Skip to content',
    openMenu: 'Open menu',
    closeMenu: 'Close menu',
    language: 'Language',
  },
  hero: {
    badge: 'Free · Open source · Windows',
    title: 'Backtest, optimize and trade US stocks from one app',
    lead:
      'Download market data, test strategies on the QuantConnect Lean engine, find the best parameters, measure the risk with Monte Carlo simulation, and trade on a paper account with live prices.',
    download: 'Download for Windows',
    viewSource: 'View on GitHub',
    imageAlt:
      'Stockhog backtest page: strategy settings, equity curve, drawdown chart and key results for an AAPL run',
    proof: [
      { value: '7', label: 'built-in strategies' },
      { value: '1 min', label: 'bars for any US stock' },
      { value: '0', label: 'telemetry' },
    ],
    card: {
      caption: 'Example run, AAPL',
      annualReturn: 'Annual return',
      maxDrawdown: 'Max drawdown',
    },
  },
  builtOn: {
    label: 'Built on',
  },
  features: {
    subheading: 'Features',
    title: 'Everything you need before you risk real money',
    items: [
      {
        title: 'Market data',
        desc: 'Download minute bars for any US stock and keep them up to date. Only missing days are fetched.',
      },
      {
        title: 'Backtesting',
        desc: 'Run 7 built-in strategies on the QuantConnect Lean engine, with several backtests in parallel.',
      },
      {
        title: 'Parameter optimization',
        desc: 'Grid-search the parameters on a heatmap, then re-check the best ones on another period.',
      },
      {
        title: 'Monte Carlo risk',
        desc: 'Resample your trades to see the range of drawdowns you could realistically face.',
      },
      {
        title: 'Compare symbols',
        desc: 'Test one strategy on up to 100 symbols, then sort, filter and chart the results.',
      },
      {
        title: 'AI assistant',
        desc: 'Ask Claude about your runs and positions. It can prefill an order ticket, but never sends an order.',
      },
      {
        title: 'Paper trading',
        desc: 'Trade on a paper account with live quotes, minute candles, a watchlist and an order ticket.',
      },
      {
        title: 'Safety first',
        desc: 'Risk limits, a kill switch and an audit log guard every order. Live trading is opt-in only.',
      },
    ],
  },
  howItWorks: {
    subheading: 'How it works',
    title: 'From an idea to a tested strategy in four steps',
    steps: [
      {
        title: 'Download data and backtest',
        desc:
          'Pick a symbol and a strategy. Stockhog fetches minute bars from Alpaca and runs the backtest on Lean, with every trade in a journal.',
        file: 'backtest-trades.png',
        imageAlt: 'Trade log of a completed backtest, one row per trade',
      },
      {
        title: 'Optimize, then check on another period',
        desc:
          'Search a grid of parameters and read the results on a heatmap. Re-run the best trials on a period they have never seen, to catch overfitting.',
        file: 'optimization.png',
        imageAlt: 'Parameter optimization heatmap with the best trials listed',
      },
      {
        title: 'Stress-test the drawdowns',
        desc:
          'A Monte Carlo simulation reshuffles your trades thousands of times and shows how deep a drawdown could really get.',
        file: 'risk-analysis.png',
        imageAlt: 'Monte Carlo drawdown distribution with percentiles',
      },
      {
        title: 'Paper trade, go live only when you choose',
        desc:
          'Watch live prices and trade on a paper account. Live trading needs its own keys and an explicit arm step.',
        file: 'paper-trading.png',
        imageAlt: 'Paper trading page with watchlist, minute candles, order ticket and positions',
      },
    ],
  },
  screenshots: {
    subheading: 'Screenshots',
    title: 'See it in action',
    items: [
      {
        key: 'backtest',
        file: 'backtest.png',
        caption: 'Backtest results with equity curve and KPIs',
      },
      {
        key: 'paper-trading',
        file: 'paper-trading.png',
        caption: 'Watchlist, live candles, order ticket and positions',
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
    open: 'Open full size: {caption}',
    lightboxClose: 'Close',
    lightboxNext: 'Next screenshot',
    lightboxPrev: 'Previous screenshot',
  },
  pricing: {
    subheading: 'Pricing',
    title: 'Free today, Pro later',
    lead: 'Free today for backtesting. Pro adds optimization, risk analysis and live trading.',
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
      ribbon: 'Coming soon',
      cta: 'Watch releases on GitHub',
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
    subheading: 'Download',
    title: 'Get Stockhog for Windows',
    requirementsTitle: 'Requirements',
    requirements: ['Windows 10 or 11, 64-bit', 'A free Alpaca account for market data and paper trading'],
    quickStartTitle: 'Quick start',
    quickStart: [
      'Run the installer (or unzip the portable archive) and start Stockhog',
      'Open Settings, paste your Alpaca paper keys and click “Test connection”, then “Save keys”',
      'Restart Stockhog: market data and paper trading are ready',
    ],
    smartScreenNote:
      'Windows SmartScreen may warn about an unrecognized app on first launch, since it isn’t code-signed yet. Click “More info”, then “Run anyway”.',
    loading: 'Checking the latest release…',
    errorFallback: 'No release is published yet, or the release info could not be loaded. See all releases on GitHub.',
    allReleases: 'View releases on GitHub',
    version: 'Version',
    publishedOn: 'Published on',
    size: 'Size',
    downloadButton: 'Download {version} for Windows',
    portable: 'Portable .zip',
  },
  faq: {
    subheading: 'FAQ',
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
        a: 'On your computer: local files, and your Alpaca keys encrypted for your Windows user (DPAPI). The app has no telemetry. If you turn on the optional AI assistant, your questions and the results it reads are sent to Anthropic with your own API key.',
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
    tagline: 'Backtest, optimize and trade US stocks on your own machine.',
    product: 'Product',
    resources: 'Resources',
    builtOn: 'Built on',
    language: 'Language',
    releases: 'Releases',
    readme: 'Documentation',
    issues: 'Report an issue',
    github: 'Stockhog on GitHub',
    disclaimer:
      'Not financial advice. Trading involves risk, and past or backtested performance does not guarantee future results.',
    license: 'License: to be announced.',
  },
}
