[Türkçe](README.md) | **English**

# Aldanma

Live: **https://aldanma.cahitarslan.com**

**Look before you click.** Paste a phone number, an IBAN, a link, a marketplace listing or a whole SMS/WhatsApp message and see how risky it is, and how many people have already reported it.

Aldanma is an open-source scam checker built for Türkiye. A general chatbot can tell you whether a message *sounds* like a scam. It cannot tell you that three people reported this exact IBAN last week, that the national CERT lists this domain as malicious, or that the site was registered four days ago. Those facts are what this project collects.

- **One box.** The input is recognised automatically: phone number, IBAN, link, or free text (every number, IBAN and link inside the text is checked too).
- **Community reports.** People who were scammed report the numbers, IBANs and sites involved. Identifiers that appear in the same report are linked, so one report can warn about a whole scam setup.
- **Public block list.** The malicious URL list published by [USOM](https://www.usom.gov.tr) is imported twice a day, so lookups are useful from day one.
- **Domain age.** Registration dates come from RDAP (the JSON successor of WHOIS). Most scam sites are days old.
- **Known patterns.** An open rule set recognises 21 Turkish scam types (fake enforcement notices, cargo, e-Devlet, toll roads, deposit fraud, fake jobs, "mum, my phone broke"...) and points to the official way to verify.
- **Never "safe".** There are three results: high risk, suspicious, no known signs. The tool says what it knows, not more.
- **Readable by everyone.** Large type with a text-size switch, high contrast, big touch targets, light theme by default and an optional dark theme. Verdicts use road-sign shapes so their meaning does not depend on colour.

## Privacy by design

| Data | What happens |
|---|---|
| A pasted message | Analysed **in the browser**. Only the phone numbers, IBANs and links found in it are sent for lookup. The text never leaves the device. |
| A lookup | Processed in memory, not stored, not logged. |
| A reported phone number or IBAN | Stored only as an HMAC-SHA256 keyed hash plus a masked form (`+90532 *** 45 67`). A leaked database does not reveal the numbers. |
| The reporter | No name, phone or e-mail is asked. The IP address is kept only as a keyed hash, to count distinct reporters and stop repeats. |
| Individual reports | Never published. Lookups show only counts, dates and the most common scam types. |
| "Was this right?" answers | Only the level, score and rule ids the engine returned. No text, no identifiers. |
| A message sent for review | Only when the user ticks a consent box after answering "Was this right?": to report a wrong result, or, as an optional offer after "yes", to share a correctly judged example. Phone numbers and IBANs in it are masked before it is stored. Read only by moderators. |

Official domains (e.g. `turkiye.gov.tr`, bank domains) and short codes cannot be reported, and owners can file a dispute. An accepted dispute removes the identifier from scoring.

## How it gets better (without learning on its own)

Nothing is learned automatically: unlabelled input would teach the tool mistakes, and scammers could poison it on purpose. Instead the project keeps a reviewed data loop:

1. **Community reports weigh, but never decide alone.** Reporters are counted by network (IPv4 /24, IPv6 /64), each network may name at most 20 identifiers a day, and reports from fewer than three networks cannot make anything "high risk" by themselves (a test enforces this).
2. **"Was this right?"** under every result stores a labelled answer (level, score, rule ids). It measures the rules in real use and is the training set for a future model.
3. **Review queue.** A user who says a result was wrong may send the message, with consent and masked; one who says it was right may share it the same way (claim `confirmed`), which a moderator can add to the corpus as a checked example. A moderator labels it; `tools/suggest-rules.mjs` asks Claude for candidate rule changes from the labelled ones, and a person decides. Accepted changes go through the corpus, the measurements and the C#/JS parity tests like any other rule change.
4. **A model later.** Once a few thousand labelled examples exist, a small, explainable model can be added next to the rules.

## How the score works

```
input ──► InputParser ──► identifiers (phone / IBAN / domain / URL)  +  message text
                               │                                            │
              ┌────────────────┼──────────────────┐                         ▼
              ▼                ▼                  ▼                  MessageEngine
      community reports   USOM block list    RDAP domain age     (rules/rules.json)
              └────────────────┼──────────────────┘                         │
                               ▼                                            ▼
                          RiskScorer ──────────────► score 0-100 = max(message, each identifier)
                                                     55+ high · 25-54 suspicious · below: no known signs
```

| Signal | Weight |
|---|---:|
| Distinct reporters: 1 / 2 / 3 / 5 / 10+ | 25 / 40 / 55 / 70 / 85 |
| Reported in the last 30 days | +5 |
| In the USOM malicious URL list | 80 |
| Domain registered < 30 days ago / < 180 days ago | 35 / 15 |
| Lookalike spelling of an official domain (`yap1kredi.com.tr`) | 40 |
| Foreign phone number / premium-rate 0900 line | 20 / 30 |
| Foreign IBAN | 15 |
| Message patterns (category, language, intent combinations) | see [rules/rules.json](rules/rules.json) |

All weights and every scoring sentence live in [`rules/rules.json`](rules/rules.json). The C# and JavaScript engines read the same file and must produce field-by-field identical output for all 304 test inputs (checked in CI).

## Architecture

Two decisions shape the code:

1. **A pure core.** `Aldanma.Core` holds the whole domain: input parsing, the message engine and the risk scorer. It has no dependencies, does no I/O and takes every fact (reports, block-list hits, domain age) as input. That makes it easy to test (most tests need no database) and lets the browser run the same rules through its JavaScript twin. This is the useful part of hexagonal/onion architecture without the ceremony.
2. **Vertical slices in the API.** `Aldanma.Api/Features/` has one folder per use case (`Check`, `Reports`, `Disputes`, `Feedback`, `Submissions`, `Moderation`, `Meta`, `Blocklist`), each holding its endpoint and its service. Cross-cutting pieces live in `Infrastructure/` (hashing, RDAP, Turnstile, client IP, rate limits, admin access, error handling, telemetry) and the EF Core model in `Data/`.

What was left out on purpose: repository interfaces over EF Core (the `DbContext` already is one), MediatR-style dispatch, and a separate application layer. With six small use cases they would add files, not clarity. Only the external services that tests replace (`IDomainRegistrationLookup`, `IHumanCheck`) have interfaces.

**Operations.** Every error, including malformed JSON, returns the same `{ "error", "traceId" }` shape; unexpected errors are logged with the trace id and return a generic message. Logs are structured (JSON outside Development) and never contain a looked-up or reported value. OpenTelemetry traces cover HTTP, outgoing calls and PostgreSQL; metrics include lookups by kind and level, reports, disputes, rate-limit hits and block-list imports. `/health` is liveness, `/health/ready` checks the database.

**Security.** Keyed hashes instead of stored numbers; flood protection in two layers (Cloudflare at the edge stops volume; in the app a per-client token bucket on every endpoint, a server-wide concurrency cap, request deadlines, Kestrel connection and header limits and a cap on outbound RDAP calls protect the database and the bill; see [docs/DEPLOY.md](docs/DEPLOY.md#abuse-and-ddos-protection)); per-endpoint rate limits (including the admin endpoints); a 64 KB request limit; Turnstile on reports and disputes; restrictive headers on the API and a CSP without inline scripts or styles on the site; the client IP is taken from a proxy header only when configured, so it cannot be forged to dodge limits; the API refuses to start without a proper hash key.

## Repository layout

| Path | What |
|---|---|
| `rules/rules.json` | The open rule set: categories, institutions, official domains, intent layer, identifier signals, bank codes |
| `src/Aldanma.Core` | Dependency-free .NET library: `MessageEngine`, `InputParser`, `PhoneNumber`, `Iban`, `RiskScorer` |
| `src/Aldanma.Api` | ASP.NET Core minimal API: `Features/` (one folder per use case), `Infrastructure/`, `Data/` (EF Core + PostgreSQL) |
| `web/` | Static site (no framework): `engine.js` (JS twin of the message engine), `lookup.js`, `app.js` (check form), `result.js`, `reporting.js`, `controls.js`, `dom.js` |
| `corpus/` | 279 labelled synthetic messages + parity inputs ([schema](corpus/SCHEMA.md)) |
| `tests/Aldanma.Tests` | xUnit: engine, parity, identifiers, scoring, API integration tests |
| `test/` | Node tests for the JS engine and candidate extraction |
| `tools/` | `evaluate.mjs` (accuracy on any corpus), `parity.mjs`, `build-web.mjs`, `suggest-rules.mjs` (Claude-assisted rule suggestions from the review queue) |

## Run it locally

Requirements: .NET 10 SDK, Node.js 22, PostgreSQL 16 (or Docker).

```bash
# 1. Database (Docker), or point appsettings.Development.json at your own PostgreSQL
docker run -d --name aldanma-db -e POSTGRES_USER=aldanma -e POSTGRES_PASSWORD=dev-password -p 5432:5432 postgres:16

# 2. API on http://localhost:5080 (applies migrations on start)
dotnet run --project src/Aldanma.Api

# 3. Static site on http://localhost:5173
ALDANMA_API_URL=http://localhost:5080 npm run build:web
npx serve dist -l 5173        # or: cd dist && python -m http.server 5173
```

Tests:

```bash
npm test                      # JS engine and candidate extraction
node tools/parity.mjs --check # expected JS output is current
dotnet test                   # .NET; API tests run when ALDANMA_TEST_DATABASE is set:
ALDANMA_TEST_DATABASE="Host=localhost;Username=aldanma;Password=dev-password" dotnet test
```

## API

| Method | Path | Body | Notes |
|---|---|---|---|
| POST | `/api/lookup` | `{ "items": ["0532 123 45 67", "TR..", "site.xyz"] }` | Up to 10 identifiers. What the web site uses. |
| POST | `/api/check` | `{ "text": "...", "sender": "mobile" }` | Server-side analysis of a whole message (for API users). Not stored. |
| POST | `/api/reports` | `{ "inputs": [...], "category", "channel", "loss", "occurredOn": "2026-11", "humanToken" }` | Up to 5 identifiers per report |
| POST | `/api/disputes` | `{ "input", "contact", "reason", "humanToken" }` | Owner objections |
| POST | `/api/feedback` | `{ "correct", "level", "score", "rulesVersion", "kinds", "categories", "signals" }` | "Was this right?" Rule ids only |
| POST | `/api/submissions` | `{ "text", "sender", "claim", "level", "rulesVersion", "consent": true, "humanToken" }` | Send a message for review (masked). `claim`: `missed`, `false-alarm` or `confirmed` |
| GET | `/api/meta` | | Rule version and database counts |
| * | `/api/admin/*` | header `X-Admin-Key` | Disputes, report rejection, review queue, feedback export |

Rate limits per client (IPv4 address, IPv6 /64):

| Scope | Limit |
|---|---|
| Every endpoint together | 120 requests/minute, bursts of 30 |
| `/api/check`, `/api/lookup` | 30/minute |
| `/api/reports` | 10/hour |
| `/api/disputes` | 3/hour |
| `/api/feedback` | 30/hour |
| `/api/submissions` | 5/hour |
| `/api/admin/*` | 60/minute |

A rejected request gets 429 with a `Retry-After` header. Server-wide, at most 200 requests run at once per instance.

## Deploying

See [docs/DEPLOY.md](docs/DEPLOY.md). The live site runs the static front end on Cloudflare Workers and the API with PostgreSQL on a single VPS (Docker, managed with Coolify). A free setup (Cloudflare Workers + Neon PostgreSQL + Google Cloud Run) is described too.

## Accuracy and limits

The message engine is measured on a synthetic corpus; numbers and their caveats are in [docs/MEASUREMENT.md](docs/MEASUREMENT.md). In short: rules tuned on a split look perfect on that split, and the only honest numbers are blind ones. The community database is the part that gets better with use; it starts empty apart from the USOM list.

Known gaps: `.tr` domains have no public RDAP service, so their age is unknown; bank codes and official domains are hand-maintained; the corpus is Turkish only.

## Contributing

Missed a scam or flagged a genuine message? See [CONTRIBUTING.md](CONTRIBUTING.md). Rule changes must keep train/holdout accuracy and C#/JS parity.

## License

MIT. Fonts: Atkinson Hyperlegible Next and Mono by the Braille Institute, SIL Open Font License 1.1 (`web/fonts/`). Illustrations generated with Google Gemini.

This is an information tool, not legal or financial advice, and it is not affiliated with any institution it mentions. See [LEGAL.tr.md](LEGAL.tr.md).
