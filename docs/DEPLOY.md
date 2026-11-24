# Deploying Aldanma

Two parts: a static site (`dist/`) and a .NET API with PostgreSQL. Pick one of the setups below.

## Secrets

| Setting | Env var (API) | Notes |
|---|---|---|
| Database | `ConnectionStrings__Database` | Npgsql connection string. Add `Maximum Pool Size=20;Command Timeout=10;Timeout=5` so a flood cannot open more connections than the database allows or hold them on slow queries. Keep pool size x max instances below the database's connection limit. |
| Limits | `Aldanma__Limits__*`, `Aldanma__Rdap__Max*` | Optional; defaults in `appsettings.json`, see "Abuse and DDoS protection" below. |
| Hash key | `Aldanma__HashKey` | **Required.** `openssl rand -base64 32`. Back it up: if it changes, every stored phone/IBAN hash is orphaned. |
| Admin key | `Aldanma__AdminKey` | Enables `/api/admin/*`. Empty = disabled. |
| Turnstile secret | `Aldanma__TurnstileSecret` | Cloudflare Turnstile (free). Empty = no human check; do not launch without it. |
| Site origin | `Aldanma__AllowedOrigins__0` | e.g. `https://aldanma.cahitarslan.com` (CORS) |
| Client IP header | `Aldanma__ClientIpHeader` | Header your proxy sets with the real client IP: `CF-Connecting-IP` behind Cloudflare, `X-Forwarded-For` behind Caddy or Cloud Run. Empty = socket address. Never set it when the API is reachable without that proxy, or rate limits can be dodged. |
| Moderation e-mails | `Aldanma__Notify__ResendApiKey`, `Aldanma__Notify__From`, `Aldanma__Notify__To` | Optional. A short mail when a report, dispute or message for review arrives: counts only, never a number, IBAN, address or message text. Needs a [Resend](https://resend.com) API key with sending access only, and a sender on a domain verified there (e.g. `Aldanma <aldanma@mail.example.com>`). Any of the three empty = off. Bursts are batched (`Aldanma__Notify__BatchDelaySeconds`, default 60) and at most one mail goes out per `Aldanma__Notify__MinIntervalSeconds` (default 1800), so a flood cannot fill the inbox or use up the mail quota. |
| USOM block list | `Aldanma__Usom__*` | Optional; defaults in `appsettings.json`. Reads the paged API at `https://siberguvenlik.gov.tr/api/address/index` (the old `usom.gov.tr/url-list.txt` now redirects to an HTML page). The list is stored in PostgreSQL, so a redeploy keeps it. Only the first import and a weekly full resync (`FullSyncDays`, default 7; it also catches removals) read the whole list, about 7 minutes; every `IntervalHours` (default 6) a refresh reads only what was added since the last import, a few seconds. A failed or empty download is logged as an error and keeps the previous list. |
| Telemetry | `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional. Any OpenTelemetry collector (Grafana Cloud, Honeycomb, a local collector). Empty = no export. |

The API refuses to start outside Development without a hash key of 32+ characters.

Static site build variables: `ALDANMA_API_URL` (public API origin) and `ALDANMA_TURNSTILE_SITEKEY`.

## Option A: free (Cloudflare + Neon + Cloud Run)

1. **Database: Neon.** Create a project (free plan), copy the connection string, convert it to Npgsql form:
   `Host=<host>;Database=<db>;Username=<user>;Password=<password>;SSL Mode=Require;Maximum Pool Size=20;Command Timeout=10;Timeout=5`.
   In the Neon console cap the compute size (autoscaling maximum) so a flood cannot scale the database bill.
2. **API: Google Cloud Run.** From the repository root:
   ```bash
   gcloud run deploy aldanma-api --source . --region europe-west3 --allow-unauthenticated \
     --max-instances 3 --concurrency 80 \
     --set-env-vars "Aldanma__AllowedOrigins__0=https://aldanma.cahitarslan.com,Aldanma__ClientIpHeader=X-Forwarded-For" \
     --set-secrets "ConnectionStrings__Database=aldanma-db:latest,Aldanma__HashKey=aldanma-hash:latest,Aldanma__TurnstileSecret=aldanma-turnstile:latest,Aldanma__AdminKey=aldanma-admin:latest"
   ```
   Map a custom domain such as `api.aldanma.cahitarslan.com`. Note: the USOM import runs inside the API on a timer, so with
   scale-to-zero it only runs while an instance is up. Either set `--min-instances 1` (may leave the free tier) or call the
   import from Cloud Scheduler (planned).
3. **Static site: Cloudflare Workers.** Import the GitHub repository in Workers & Pages.
   Build command: `npm run build:web` with the two build variables set. Deploy command: `npx wrangler deploy`.
   Add the custom domain `aldanma.cahitarslan.com`.
4. **Turnstile:** create a widget for the site domain in the Cloudflare dashboard; site key goes to the build, secret to the API.

Free-tier limits change; check them when creating the accounts.

## Option B: one VPS (Docker Compose)

```bash
git clone https://github.com/cahitarslan/aldanma && cd aldanma
cp .env.example .env   # fill in the secrets
docker compose up -d --build
```

The API listens on `127.0.0.1:8080`. Put a reverse proxy with TLS in front (Caddy, or Coolify which does it for you):

```
api.aldanma.cahitarslan.com {
  reverse_proxy 127.0.0.1:8080
}
```

Back up the `db-data` volume (e.g. nightly `pg_dump`) and the `.env` file. The static site can stay on Cloudflare.

## Abuse and DDoS protection

Two layers, each doing what it is good at:

- **The edge stops volume.** A flood of millions of requests must never reach the API; Cloudflare absorbs it.
- **The app protects the database and the bill.** Whatever gets through is limited per client and server-wide, so it cannot
  exhaust database connections, outbound RDAP calls or Cloud Run instances.

### At the edge (Cloudflare)

1. **Proxy the API through Cloudflare** (orange cloud on `api.aldanma.cahitarslan.com`) and make it the only way in:
   - Cloud Run: put it behind a load balancer with `--ingress internal-and-cloud-load-balancing`, or at least verify
     [Authenticated Origin Pulls](https://developers.cloudflare.com/ssl/origin-configuration/authenticated-origin-pull/).
   - VPS: allow ports 80/443 only from [Cloudflare's IP ranges](https://www.cloudflare.com/ips/) in the firewall.
   - Then set `Aldanma__ClientIpHeader=CF-Connecting-IP`. Only once direct access is closed: otherwise anyone can send
     a fake `CF-Connecting-IP` and dodge the per-client limits.
2. **WAF rate-limiting rule** (Security, WAF, Rate limiting rules): for `URI Path starts with /api/`, block an IP for
   10 minutes when it sends more than 100 requests in 10 seconds. It is the edge twin of the app's per-client bucket,
   set looser so the app limits only matter for traffic the edge let through.
3. **Bot Fight Mode** on (Security, Bots). Turnstile already guards reports, disputes and submissions.
4. **Emergency switch:** "Under Attack" mode (Overview, Quick actions) puts a browser check in front of every request.
   Turn it on during an attack and off afterwards: it also blocks API clients that are not browsers.

### On the hosting

- **Cloud Run:** `--max-instances 3 --concurrency 80` (in the deploy command above) caps both the bill and the database
  connections (3 instances x pool 20 = 60). Add a budget alert in Google Cloud Billing as a second line.
- **Neon:** cap the autoscaling maximum so a flood cannot scale the database bill.

### In the app (defaults; configurable under `Aldanma:Limits` and `Aldanma:Rdap`)

| Limit | Default |
|---|---|
| Every request, per client (IPv4 address, IPv6 /64) | 120/minute, bursts of 30; then 429 with `Retry-After` |
| Requests in flight per instance | 200, then a queue of 50, then 429 |
| Per-endpoint limits | see the README's API section |
| Connections per instance | 1000 |
| Request headers must arrive within / keep-alive idle | 15 s / 60 s |
| Header size / request line / body | 16 KB / 8 KB / 64 KB |
| Request deadline | 10 s (20 s for `/api/check` and `/api/lookup`), then 504 |
| New RDAP fetches per request / in flight per instance | 3 / 8; when busy the domain age is skipped, not waited for |
| `/api/meta` | output-cached for 60 s |
| Database pool / command timeout | 20 / 10 s (connection string) |

`/health` and `/health/ready` are never rate limited, so the platform keeps seeing a live instance while it sheds load.
Watch `aldanma.rate_limited` and `aldanma.rdap.skipped` in the metrics: a sustained rise means the edge rule needs tightening.

## After deploying

- `GET /health` returns `Healthy` (liveness, no database); `GET /health/ready` also checks the database.
- Logs are JSON lines on stdout. Traces and metrics (including `aldanma.lookups`, `aldanma.reports`,
  `aldanma.rate_limited`, `aldanma.blocklist.imports`) go to `OTEL_EXPORTER_OTLP_ENDPOINT` when it is set.
- `GET /api/meta` shows the USOM import under `blocklistImport` (state, pages done of total, entries so far, seconds left).
  The first full import takes about 7 minutes; `blocklistCount` follows within 5 minutes of it finishing.
  A `failed` state means "USOM import failed" is in the logs with the details.
- With moderation e-mails on, send one test report and expect a mail within a couple of minutes.
- Check that a lookup from the site works and that reports need the Turnstile check.

## Updating the live site

- Front end: Cloudflare Workers Builds rebuilds and redeploys the site automatically on every push to `main`.
  If the Worker's GitHub connection breaks, pushes are ignored silently; push a new commit once it is fixed.
- API: Coolify currently redeploys only when a deploy is started by hand in the Coolify panel.
  Automatic deploys through a GitHub webhook are planned.
