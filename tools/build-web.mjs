// Builds the static site into dist/: copies web/, adds rules/rules.json and fills in the API settings.
//   ALDANMA_API_URL            e.g. https://api.aldanma.cahitarslan.com (empty = message-only mode)
//   ALDANMA_TURNSTILE_SITEKEY  Cloudflare Turnstile site key (empty = no human check)
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const dist = new URL('dist/', root);
const api = (process.env.ALDANMA_API_URL || '').trim().replace(/\/$/, '');
const siteKey = (process.env.ALDANMA_TURNSTILE_SITEKEY || '').trim();

rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });
cpSync(new URL('web/', root), dist, { recursive: true });
cpSync(new URL('rules/rules.json', root), new URL('rules.json', dist));

const attr = (s) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
const indexUrl = new URL('index.html', dist);
const html = readFileSync(indexUrl, 'utf8')
  .replace('<meta name="aldanma-api" content="">', `<meta name="aldanma-api" content="${attr(api)}">`)
  .replace('<meta name="aldanma-turnstile" content="">', `<meta name="aldanma-turnstile" content="${attr(siteKey)}">`);
writeFileSync(indexUrl, html);

const turnstile = siteKey ? ' https://challenges.cloudflare.com' : '';
const csp = [
  "default-src 'self'",
  `script-src 'self'${turnstile}`,
  `frame-src${turnstile || " 'none'"}`,
  `connect-src 'self'${api ? ' ' + new URL(api).origin : ''}`,
  "style-src 'self'",
  "img-src 'self' data:",
  "font-src 'self'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join('; ');
writeFileSync(new URL('_headers', dist), `/*
  Content-Security-Policy: ${csp}
  Referrer-Policy: no-referrer
  X-Content-Type-Options: nosniff
  Permissions-Policy: camera=(), microphone=(), geolocation=()

/fonts/*
  Cache-Control: public, max-age=31536000, immutable

/img/*
  Cache-Control: public, max-age=604800
`);
console.log(`dist/ built (api: ${api || 'none'}, human check: ${siteKey ? 'on' : 'off'})`);
