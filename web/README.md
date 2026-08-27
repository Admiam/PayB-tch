# Paybitch Web

The web port of the Paybitch iOS app. React 19 · Vite 8 · TypeScript 6.

Same features as the phone app, same visual design, and — like the phone app —
**it has no backend**. Every group, member and expense lives in the browser's
`localStorage` and never leaves the device. That is why it deploys as plain
static files behind Caddy, with no API, no database and no accounts.

## Develop

```bash
npm install
npm run dev
```

| Command | What it does |
| --- | --- |
| `npm run dev` | Dev server on http://localhost:5173 |
| `npm run build` | Type-check, then build to `dist/` |
| `npm run preview` | Serve the production build locally |
| `npm test` | Domain test suite (money math, balances, settlement) |
| `npm run typecheck` | `tsc` only |
| `npm run lint` | oxlint |

### Test the money math

`src/domain/money.test.ts` and `balances.test.ts` are ported case-for-case from
the Swift suites. They exist so the two apps can never disagree about who owes
what — if you touch anything in `src/domain/`, run them.

Two rules there are load-bearing and easy to break by accident:

- Rounding is **banker's** (half to even), not half-up.
- When a split does not divide evenly, the **last member in the list** absorbs
  the remainder. It is not the largest-remainder method.

## Deploy

Target: a Debian VPS with Docker. One container builds nothing at runtime — it
serves the pre-built bundle and terminates TLS itself.

```bash
SITE_ADDRESS=pay.holu.be docker compose up -d --build
```

Caddy obtains and renews the Let's Encrypt certificate on its own. There is no
certbot step and nothing to schedule.

**Before the first run**, make sure:

1. The DNS `A` record for the hostname points at the VPS.
2. Ports **80 and 443** reach the container — Let's Encrypt validates over port
   80, so opening only 443 fails.

The `caddy-data` volume holds the certificate and ACME account key. Keep it;
recreating it on every deploy will eventually hit Let's Encrypt's rate limit.

To test the container locally, leave `SITE_ADDRESS` unset — it falls back to
`:80` and skips certificate issuance, which cannot work for `localhost` anyway.

## Layout

```
src/
├── domain/      money, splits, balances, settlement — pure, tested, no React
├── store/       the single app store (mirrors ModelData.swift)
├── lib/         localStorage persistence
├── components/  shared UI: glass controls, avatars, tiles, sheets
├── screens/     one file per screen
├── icons/       SF Symbol name → web icon
└── styles/      design tokens + per-area CSS
```

`src/domain/` knows nothing about React and is where every number comes from.
Screens never do arithmetic on money themselves.

## Notes on the port

- **Font**: Space Grotesk is self-hosted from the repo's own copy, converted to
  WOFF2 (137 kB → 51 kB). Not loaded from a CDN, so the app makes no external
  requests at all.
- **Icons**: the phone app stores SF Symbol names. Those names are kept in the
  data for portability and mapped to Lucide icons at render time in
  `src/icons/`. Two symbols have no Lucide equivalent and are drawn inline.
- **Avatar colours**: the phone app hashes the member id with Swift's
  `hashValue`, which is reseeded each launch — so colours there actually change
  between restarts. The web uses a fixed FNV-1a hash, making it more stable
  than the original rather than less.
- **Installable**: the manifest and meta tags make this an installable PWA, so
  adding it to a phone's home screen gives a standalone app window with no
  browser chrome.
