// Finds phone numbers, IBANs and links in pasted text, in the browser.
// Only these short strings are sent to the server; the message itself never leaves the device.
// The server re-parses every string with the C# InputParser, so this only needs to find candidates.

const IBAN_RX = /(?<![A-Za-z0-9])TR ?\d{2}(?: ?\d{4}){5} ?\d{2}(?![0-9])/gi;
const PHONE_RXS = [
  /(?<![0-9+])(?:\+|00)[1-9][0-9 ()\-]{7,18}[0-9](?![0-9])/g,
  /(?<![0-9])(?:0 ?)?\(?(?:5\d{2}|[2-4]\d{2}|850|800|900)\)? ?\d{3}[ \-]?\d{2}[ \-]?\d{2}(?![0-9])/g,
  /(?<![0-9])444[ \-]?\d[ \-]?\d{3}(?![0-9])/g,
];
const MAX_ITEMS = 10;

const blank = (s) => ' '.repeat(s.length);

/** True when the whole input is a single phone number, IBAN or link rather than a message. */
export function isSingleItem(text) {
  const t = text.trim();
  if (t.length === 0 || t.length > 300) return false;
  if (!/\s/.test(t)) return true;
  // "0532 123 45 67" or "TR12 0006 ..." contain spaces but are still one item.
  if (/^[+0-9 ()\-]{7,22}$/.test(t)) return true;
  if (/^TR[0-9 ]{24,32}$/i.test(t)) return true;
  return false;
}

/**
 * @param {string} text pasted text
 * @param {{ extractLinks: (t: string) => { raw: string, host: string }[] }} engine message engine (for link parsing)
 * @returns {string[]} candidate strings in order of appearance, de-duplicated
 */
export function findCandidates(text, engine) {
  const found = [];
  for (const m of text.matchAll(IBAN_RX)) found.push({ at: m.index, value: m[0] });
  let masked = text.replace(IBAN_RX, blank);
  for (const rx of PHONE_RXS) {
    for (const m of masked.matchAll(rx)) found.push({ at: m.index, value: m[0] });
    masked = masked.replace(rx, blank);
  }
  let offset = 0;
  for (const link of engine.extractLinks(text)) {
    const at = text.toLowerCase().indexOf(link.host, offset);
    found.push({ at: at < 0 ? Number.MAX_SAFE_INTEGER : at, value: link.raw });
    if (at >= 0) offset = at;
  }
  found.sort((a, b) => a.at - b.at);
  const seen = new Set();
  const out = [];
  for (const f of found) {
    const key = f.value.replace(/[\s()\-]/g, '').toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(f.value.trim());
    if (out.length >= MAX_ITEMS) break;
  }
  return out;
}

/** Small JSON client with a timeout. Returns { ok, status, data }. Never throws. */
export async function callApi(base, path, body, timeoutMs = 9000) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await fetch(base.replace(/\/$/, '') + path, {
      method: body === undefined ? 'GET' : 'POST',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: ctrl.signal,
      credentials: 'omit',
    });
    let data = null;
    try { data = await res.json(); } catch { /* empty body */ }
    return { ok: res.ok, status: res.status, data };
  } catch {
    return { ok: false, status: 0, data: null };
  } finally {
    clearTimeout(timer);
  }
}
