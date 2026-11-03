# Security

## Reporting a vulnerability

Please report security issues privately through GitHub: **Security → Report a vulnerability** on this repository.
Do not open a public issue. You will get an answer within a week; fixes go into the next release and the advisory is
published once users can update.

Only the latest release is supported.

## How Stockhog protects your account

**Broker keys**
- Market data and paper keys are entered on the Settings page, tested against Alpaca first, and stored in a file
  encrypted with Windows DPAPI for your Windows user (`%LOCALAPPDATA%\Stockhog\alpaca-keys.bin`). The AI assistant key
  is stored the same way.
- Keys are never shown again, written to logs or to the audit trail; only a masked key id (`PKAB…WXYZ`) is.
- Live account keys are never entered in the app: they come only from configuration, together with two explicit
  opt-ins (`Alpaca:TradingMode=Live` and `Alpaca:LiveTradingEnabled=true`), and the app refuses to start if they are a
  copy of the paper or data keys. Testing keys never calls the live API.

**Orders**
- Paper trading is the default. Live orders also need the account number typed in each session ("arming").
- Every order goes through the same risk checks: order and position size, daily loss, open orders, positions held,
  share of equity per symbol, drawdown from the equity high and orders per day. A kill switch stops new orders.
- Only one copy of the app runs at a time, so two copies cannot trade the same account.
- The autopilot places orders only after a confirmation, only on the paper account, and is switched off after every
  restart. Its buys carry a stop loss held by the broker, so positions stay protected while the app is closed.
- Order requests are never retried automatically (a timed-out order may have been placed); each has a unique client
  order id, and reconciliation with the broker finds out what happened.
- Every order, refusal and safety change is written to an append-only audit trail.

**Your data**
- Everything stays on your computer: market data, results, the database and logs. The app sends no telemetry.
- The optional AI assistant sends your questions and the results it reads (runs, prices, account summary) to
  Anthropic with your own key. Its tools are read-only; the one that touches trading only pre-fills the order ticket,
  which you must check and send yourself.

**Network**
- HTTPS and WSS only, with the system's certificate checks.

**Supply chain and releases**
- Package versions are pinned centrally, including patched versions of vulnerable packages that the Lean engine pulls
  in. NuGet audit covers transitive packages and the CI build fails on high or critical findings; Dependabot proposes
  updates.
- Releases are built by GitHub Actions from a version tag, uploaded with short-lived OIDC credentials (no stored cloud
  keys), and published with SHA-256 checksums.

## Known limitations

- **The installer and the app are not code-signed yet**, so Windows SmartScreen warns on first start. Check the
  SHA-256 checksum published with the release.
- DPAPI protects the keys from other Windows users and other computers, not from malware running as your own user.
- The SQLite database (orders, runs, audit trail) and the logs are not encrypted; they contain no keys.
- DotNetZip, used by the Lean engine, has a path traversal advisory (GHSA-xhg6-9j5j-w4vf) with no fixed version. It
  applies to extracting untrusted archives; Lean only opens the zip files Stockhog writes itself from broker data.
