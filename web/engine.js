// Aldanma message engine. Dependency-free; runs in the browser and in Node.
// The message text never leaves this file: there are no network calls.
// C# counterpart: src/Aldanma.Core/MessageEngine.cs. Both engines read the same
// rule set and must produce identical output (see tools/parity.mjs).

const FOLD = {
  'ç': 'c', 'Ç': 'c', 'ğ': 'g', 'Ğ': 'g', 'ı': 'i', 'İ': 'i', 'ö': 'o', 'Ö': 'o',
  'ş': 's', 'Ş': 's', 'ü': 'u', 'Ü': 'u', 'â': 'a', 'Â': 'a', 'î': 'i', 'Î': 'i',
  'û': 'u', 'Û': 'u', '–': '-', '—': '-', '₺': ' tl ',
};
const PUNCTUATION = ',;:!?()"\'`';
const TURKISH_LETTERS = 'çÇğĞıİöÖşŞüÜâÂîÎûÛ';
const TURKISH_UPPER = 'ÇĞİÖŞÜ';
const TURKISH_LOWER = 'çğıöşü';
const TRIM_END = '.,;:!?)]}>"\'»”’';
const TRIM_START = '([{<"\'«“‘';
const DIGIT_LETTER = { '0': 'o', '1': 'l', '3': 'e', '4': 'a', '5': 's', '7': 't' };
const PERSONAL_SENDERS = ['mobile', 'foreign', 'whatsapp'];

export const SENDERS = ['unknown', 'header', 'mobile', 'foreign', 'whatsapp'];
export const LEVELS = { high: 'high', suspicious: 'suspicious', noSigns: 'no-signs' };

function isSpace(c) {
  return c === 32 || c === 9 || c === 10 || c === 13 || c === 160 || c === 11 || c === 12;
}

const has = (o, k) => Object.prototype.hasOwnProperty.call(o, k);

/** Lower-cases the text, folds Turkish letters to ASCII and turns everything else into spaces. */
export function fold(text) {
  let out = '';
  let prevSpace = true;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    let e;
    if (c >= 65 && c <= 90) e = String.fromCharCode(c + 32);
    else if (isSpace(c)) e = ' ';
    else if (c === 46) {
      // A sentence-final dot becomes a space; dots inside "1.500" or "t.c" stay.
      const n = i + 1 < text.length ? text.charCodeAt(i + 1) : 32;
      const alnum = (n >= 48 && n <= 57) || (n >= 65 && n <= 90) || (n >= 97 && n <= 122);
      e = alnum ? '.' : ' ';
    }
    else if (PUNCTUATION.indexOf(text[i]) >= 0) e = ' ';
    else if (c >= 33 && c <= 126) e = text[i];
    else if (c === 0x307) e = '';
    else if (has(FOLD, text[i])) e = FOLD[text[i]];
    else e = ' ';
    for (let j = 0; j < e.length; j++) {
      const ch = e[j];
      if (ch === ' ') {
        if (!prevSpace) out += ' ';
        prevSpace = true;
      } else {
        out += ch;
        prevSpace = false;
      }
    }
  }
  if (out.length > 0 && out[out.length - 1] === ' ') out = out.substring(0, out.length - 1);
  return out;
}

function asciiLower(s) {
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    out += c >= 65 && c <= 90 ? String.fromCharCode(c + 32) : s[i];
  }
  return out;
}

function splitWords(text) {
  const words = [];
  let start = -1;
  for (let i = 0; i <= text.length; i++) {
    const space = i === text.length || isSpace(text.charCodeAt(i));
    if (space) {
      if (start >= 0) { words.push({ start, end: i }); start = -1; }
    } else if (start < 0) start = i;
  }
  return words;
}

function allDigits(s) {
  if (s.length === 0) return false;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c < 48 || c > 57) return false;
  }
  return true;
}

function allLetters(s) {
  if (s.length === 0) return false;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c < 97 || c > 122) return false;
  }
  return true;
}

export function levenshtein(a, b) {
  const n = a.length, m = b.length;
  if (n === 0) return m;
  if (m === 0) return n;
  let prev = new Array(m + 1);
  let curr = new Array(m + 1);
  for (let j = 0; j <= m; j++) prev[j] = j;
  for (let i = 1; i <= n; i++) {
    curr[0] = i;
    for (let j = 1; j <= m; j++) {
      const cost = a.charCodeAt(i - 1) === b.charCodeAt(j - 1) ? 0 : 1;
      let d = prev[j] + 1;
      if (curr[j - 1] + 1 < d) d = curr[j - 1] + 1;
      if (prev[j - 1] + cost < d) d = prev[j - 1] + cost;
      curr[j] = d;
    }
    const t = prev; prev = curr; curr = t;
  }
  return prev[m];
}

function fill(template, values) {
  let s = template;
  for (const k of Object.keys(values)) s = s.split('{' + k + '}').join(values[k]);
  return s;
}

/**
 * Builds an engine from a rule set.
 * @param {object} rules contents of rules/rules.json
 */
export function createEngine(rules) {
  const rx = (p) => new RegExp(p);
  const institutions = rules.institutions.map((k) => ({ ...k, _rx: k.keywords.map(rx) }));
  const institutionById = {};
  for (const k of institutions) institutionById[k.id] = k;
  const categories = rules.categories.map((k) => ({ ...k, _strong: k.strong.map(rx), _weak: k.weak.map(rx) }));
  const languageSignals = rules.languageSignals.map((k) => ({ ...k, _rx: k.patterns.map(rx) }));
  const I = rules.intent || { classes: [], combinations: [] };
  const intentClasses = I.classes.map((k) => ({ id: k.id, _rx: k.patterns.map(rx) }));
  const D = rules.domains;
  const S = rules.sender.signals;
  const W = rules.writing;
  const digitLettersRx = rx(W.digitLetters.pattern);
  const mobileRx = rx('(^|[^0-9])(\\+?90 ?|0)?5\\d{2} ?\\d{3} ?\\d{2} ?\\d{2}($|[^0-9])');
  const phoneRx = rx('(^|[^0-9])(0 ?850|0 ?5\\d{2}|444|\\+90|0 ?[2-4]\\d{2}) ?\\d{3} ?\\d{2,4}');
  const bareTlds = {};
  for (const t of D.knownTlds) bareTlds[t] = true;
  for (const t of D.suspiciousTlds) bareTlds[t] = true;

  // First labels of official domains (used for the lookalike comparison)
  const officialLabels = [];
  for (const k of institutions) {
    for (const d of k.domains) {
      const label = d.substring(0, d.indexOf('.'));
      if (label.length >= 5) officialLabels.push({ label, domain: d, institution: k });
    }
  }

  function hasSuffix(host, suffix) {
    return host === suffix || (host.length > suffix.length && host.substring(host.length - suffix.length - 1) === '.' + suffix);
  }

  function registeredDomain(host) {
    const parts = host.split('.');
    let n = 2;
    for (const s of D.secondLevelSuffixes) {
      if (hasSuffix(host, s) && host !== s) { n = s.split('.').length + 1; break; }
    }
    if (parts.length <= n) return host;
    return parts.slice(parts.length - n).join('.');
  }

  /** Tries to read a link from a single word; returns null when the word is not a link. */
  function parseLink(word) {
    let s = word;
    while (s.length > 0 && TRIM_START.indexOf(s[0]) >= 0) s = s.substring(1);
    const lower = asciiLower(s);
    let scheme = '';
    const i = lower.indexOf('https://');
    const j = lower.indexOf('http://');
    if (i >= 0 && (j < 0 || i <= j)) { scheme = 'https'; s = s.substring(i + 8); }
    else if (j >= 0) { scheme = 'http'; s = s.substring(j + 7); }
    else {
      const w = lower.indexOf('www.');
      if (w > 0) s = s.substring(w);
      else {
        // Glued forms such as "Takip:site.com/x"
        const dot = s.indexOf('.');
        let cut = -1;
        for (let q = 0; q < s.length && (dot < 0 || q < dot); q++) {
          if (':=('.indexOf(s[q]) >= 0) cut = q;
        }
        if (cut >= 0) s = s.substring(cut + 1);
      }
    }
    while (s.length > 0 && TRIM_END.indexOf(s[s.length - 1]) >= 0) s = s.substring(0, s.length - 1);
    if (s.length < 4) return null;
    // Turkish suffix: "turkiye.gov.tr'den"
    const lastDot = s.lastIndexOf('.');
    for (let q = lastDot + 1; q < s.length; q++) {
      if (s[q] === "'" || s[q] === '’') { s = s.substring(0, q); break; }
    }
    let authorityEnd = s.length;
    for (let q = 0; q < s.length; q++) {
      if (s[q] === '/' || s[q] === '?' || s[q] === '#') { authorityEnd = q; break; }
    }
    let host = asciiLower(s.substring(0, authorityEnd));
    const path = asciiLower(s.substring(authorityEnd));
    const at = host.lastIndexOf('@');
    if (at >= 0) {
      if (scheme === '') return null; // e-mail address
      host = host.substring(at + 1);
    }
    const colon = host.indexOf(':');
    if (colon >= 0) host = host.substring(0, colon);
    while (host.length > 0 && host[host.length - 1] === '.') host = host.substring(0, host.length - 1);
    const hasWww = host.indexOf('www.') === 0;
    if (hasWww) host = host.substring(4);
    const labels = host.split('.');
    if (labels.length < 2) return null;
    let special = false;
    for (const label of labels) {
      if (label.length === 0) return null;
      if (label.indexOf('xn--') === 0) special = true;
      for (let q = 0; q < label.length; q++) {
        const c = label.charCodeAt(q);
        const alnum = (c >= 97 && c <= 122) || (c >= 48 && c <= 57) || c === 45;
        if (c > 127) {
          if (c >= 0x2000 && c <= 0x2BFF) return null; // punctuation and symbols
          special = true;
        } else if (!alnum) return null;
      }
    }
    let ip = false;
    if (labels.length === 4) {
      ip = true;
      for (const label of labels) if (!allDigits(label) || label.length > 3) ip = false;
    }
    const tld = labels[labels.length - 1];
    if (!ip) {
      const validTld = (allLetters(tld) && tld.length >= 2 && tld.length <= 24) || tld.indexOf('xn--') === 0;
      if (!validTld) return null;
      if (scheme === '' && !hasWww && !bareTlds[tld]) return null;
      if (scheme === '' && !hasWww && labels[labels.length - 2].length < 2) return null;
    }
    return { raw: (scheme ? scheme + '://' : '') + (hasWww ? 'www.' : '') + host + path, scheme, host, path, ip, tld: ip ? '' : tld, special };
  }

  function findOfficial(host) {
    for (const k of institutions) {
      for (const d of k.domains) if (hasSuffix(host, d)) return { institution: k, domain: d };
    }
    for (const s of D.publicSuffixes) if (hasSuffix(host, s) && host !== s) return { institution: null, domain: s };
    for (const d of D.knownDomains) if (hasSuffix(host, d)) return { institution: null, domain: d };
    return null;
  }

  function findImpersonation(host) {
    const parts = host.split('.').join('-').split('-');
    const noHyphen = host.split('-').join('');
    for (const k of institutions) {
      for (const t of k.tokens) {
        if (t.length >= 5) {
          if (host.indexOf(t) >= 0 || noHyphen.indexOf(t) >= 0) return { institution: k, token: t };
        } else {
          for (const p of parts) {
            if (p === t || p.indexOf(t) === 0 || (t.length === 4 && p.length > 4 && p.substring(p.length - 4) === t)) {
              return { institution: k, token: t };
            }
          }
        }
      }
    }
    return null;
  }

  function findLookalike(registered) {
    const first = registered.substring(0, registered.indexOf('.'));
    let plain = '';
    for (let i = 0; i < first.length; i++) {
      const ch = first[i];
      plain += has(DIGIT_LETTER, ch) ? DIGIT_LETTER[ch] : ch;
    }
    const noHyphen = plain.split('-').join('');
    for (const r of officialLabels) {
      const limit = r.label.length >= 9 ? 2 : 1;
      for (const candidate of [plain, noHyphen]) {
        if (candidate.length < 4) continue;
        if (candidate === r.label) return r;
        const diff = candidate.length - r.label.length;
        if (diff > limit || diff < -limit) continue;
        if (levenshtein(candidate, r.label) <= limit) return r;
      }
    }
    return null;
  }

  function inspectLink(b) {
    const s = { raw: b.raw, host: b.host, registeredDomain: b.ip ? b.host : registeredDomain(b.host), official: false, channel: false, signals: [], detail: {} };
    if (b.ip) {
      s.signals.push('ip-address');
      if (b.scheme === 'http') s.signals.push('unencrypted');
      if (b.path.indexOf('.apk') >= 0) s.signals.push('apk-file');
      return s;
    }
    const official = b.special ? null : findOfficial(b.host);
    if (official) {
      s.official = true;
      s.signals.push('official-domain');
      return s;
    }
    if (D.channelDomains.indexOf(b.host) >= 0) { s.channel = true; return s; }
    let strong = false;
    if (D.shorteners.indexOf(b.host) >= 0 || D.shorteners.indexOf(s.registeredDomain) >= 0) {
      s.signals.push('url-shortener');
      strong = true;
    } else {
      // A host with Turkish letters ("türkiye.gov.tr") is folded and compared with official domains.
      let folded = '';
      for (let i = 0; i < b.host.length; i++) {
        const ch = b.host[i];
        folded += has(FOLD, ch) && FOLD[ch].length === 1 ? FOLD[ch] : ch;
      }
      const foldedOfficial = b.special ? findOfficial(folded) : null;
      const lookalike = foldedOfficial && foldedOfficial.institution
        ? { domain: foldedOfficial.domain, institution: foldedOfficial.institution }
        : findLookalike(registeredDomain(folded));
      if (lookalike) {
        s.signals.push('lookalike-spelling');
        s.detail.official = lookalike.domain;
        s.detail.institution = lookalike.institution.name;
        strong = true;
      } else {
        const imp = findImpersonation(b.host);
        if (imp) {
          s.signals.push('impersonating-domain');
          s.detail.token = imp.token;
          s.detail.institution = imp.institution.name;
          strong = true;
        }
      }
      if (D.suspiciousTlds.indexOf(b.tld) >= 0) { s.signals.push('suspicious-tld'); s.detail.tld = b.tld; strong = true; }
      let hyphens = 0, digits = 0;
      for (let i = 0; i < b.host.length; i++) {
        const c = b.host.charCodeAt(i);
        if (c === 45) hyphens++;
        if (c >= 48 && c <= 57) digits++;
      }
      if (hyphens >= 2 || digits >= 4 || b.host.split('.').length >= 5) s.signals.push('complex-address');
    }
    if (b.special) { s.signals.push('special-characters'); strong = true; }
    if (b.path.indexOf('.apk') >= 0) { s.signals.push('apk-file'); strong = true; }
    if (b.scheme === 'http') s.signals.push('unencrypted');
    if (!strong) s.signals.push('unknown-domain');
    return s;
  }

  /** Extracts links from free text (same rules the analysis uses). */
  function extractLinks(text) {
    const out = [];
    for (const w of splitWords(text || '')) {
      const word = text.substring(w.start, w.end);
      const b = word.indexOf('.') > 0 ? parseLink(word) : null;
      if (b) out.push(b);
    }
    return out;
  }

  /**
   * Analyses a message.
   * @param {string} text message text
   * @param {string} [sender] unknown | header | mobile | foreign | whatsapp
   */
  function analyze(text, sender) {
    text = text || '';
    sender = sender || 'unknown';

    // 1. Extract links
    const links = [];
    const seen = {};
    let withoutLinks = '';
    let pos = 0;
    for (const w of splitWords(text)) {
      const word = text.substring(w.start, w.end);
      const b = word.indexOf('.') > 0 ? parseLink(word) : null;
      if (b) {
        withoutLinks += text.substring(pos, w.start) + ' ';
        pos = w.end;
        const key = b.host + b.path;
        if (!seen[key] && links.length < 10) {
          seen[key] = true;
          links.push(inspectLink(b));
        }
      }
    }
    withoutLinks += text.substring(pos);
    const plain = fold(withoutLinks);

    const signals = [];
    let score = 0;
    const add = (id, type, name, weight, reason) => {
      signals.push({ id, type, name, weight, reason });
      score += weight;
    };

    // 2. Language signals
    const lang = {};
    const langHits = [];
    for (const d of languageSignals) {
      if (d._rx.some((r) => r.test(plain))) { lang[d.id] = true; langHits.push(d); }
    }
    if (links.some((b) => b.channel) && !lang['channel-switch']) {
      const d = languageSignals.find((x) => x.id === 'channel-switch');
      if (d) { lang[d.id] = true; langHits.push(d); }
    }

    const riskyLinks = links.filter((b) => !b.official && !b.channel);

    // Intent classes (broad meaning classes); their combinations are evaluated in step 7b.
    const features = {};
    for (const n of intentClasses) if (n._rx.some((r) => r.test(plain))) features[n.id] = true;

    const asksAction = !!features['callback'] || riskyLinks.length > 0 || links.some((b) => b.channel) || !!lang['sensitive-info'] || !!lang['money-request']
      || phoneRx.test(plain) || PERSONAL_SENDERS.indexOf(sender) >= 0;

    // 3. Categories
    const matched = [];
    for (const k of categories) {
      let effective = 0, strong = false;
      if (k.senderCondition && k.senderCondition.indexOf(sender) < 0) continue;
      if (k._strong.some((r) => r.test(plain))) {
        // A message written in an institution's name that asks for nothing (no link, phone, payment or data) counts as a notice.
        if ((k.institutional || k.requiresAction) && !asksAction) effective = Math.floor((k.weight * 5) / 10);
        else { effective = k.weight; strong = true; }
      }
      else {
        let weak = 0;
        for (const r of k._weak) if (r.test(plain)) weak++;
        if (weak >= 2 && (riskyLinks.length > 0 || lang['sensitive-info'] || lang['money-request'])) {
          effective = Math.floor((k.weight * 6) / 10);
        }
      }
      if (effective > 0) matched.push({ k, effective, strong });
    }
    // Stable sort: effective weight descending, file order on ties
    const ranked = matched.map((e, i) => ({ e, i })).sort((x, y) => (y.e.effective - x.e.effective) || (x.i - y.i)).map((x) => x.e).slice(0, 3);
    ranked.forEach((e, i) => {
      add(e.k.id, 'category', e.k.name, i === 0 ? e.effective : 5, e.k.description);
    });

    for (const d of langHits) add(d.id, 'language', d.name, d.weight, d.description);

    // 4. Writing
    let cyrillic = false, accent = false, letters = 0, upper = 0;
    for (let i = 0; i < withoutLinks.length; i++) {
      const c = withoutLinks.charCodeAt(i);
      const ch = withoutLinks[i];
      if (c >= 0x370 && c <= 0x4ff) cyrillic = true;
      else if (c >= 0xc0 && c <= 0x24f && c !== 0xd7 && c !== 0xf7 && TURKISH_LETTERS.indexOf(ch) < 0) accent = true;
      if ((c >= 65 && c <= 90) || TURKISH_UPPER.indexOf(ch) >= 0) { letters++; upper++; }
      else if ((c >= 97 && c <= 122) || TURKISH_LOWER.indexOf(ch) >= 0) letters++;
    }
    if (digitLettersRx.test(plain)) add('digit-letters', 'writing', W.digitLetters.name, W.digitLetters.weight, W.digitLetters.description);
    if (cyrillic) add('mixed-alphabet', 'writing', W.mixedAlphabet.name, W.mixedAlphabet.weight, W.mixedAlphabet.description);
    if (accent) add('foreign-accents', 'writing', W.foreignAccents.name, W.foreignAccents.weight, W.foreignAccents.description);
    if ((letters >= 20 && upper * 100 >= letters * 70) || withoutLinks.indexOf('!!') >= 0) add('shouting', 'writing', W.shouting.name, W.shouting.weight, W.shouting.description);

    // 5. Claimed institution
    const textInstitutions = institutions.filter((k) => k._rx.some((r) => r.test(plain)));
    const claimed = textInstitutions.filter((k) => k.mismatch !== false);
    const strongInstitutional = ranked.filter((e) => e.strong && e.k.institutional);
    let claimedName = '';
    if (claimed.length > 0) claimedName = claimed[0].name;
    else if (strongInstitutional.length > 0) {
      const first = strongInstitutional[0].k;
      claimedName = first.institutions.length > 0 ? institutionById[first.institutions[0]].name : first.name;
    }
    const hasClaim = claimedName !== '';

    // 6. Link signals (each kind counts once)
    for (const id of Object.keys(D.signals)) {
      const t = D.signals[id];
      if (id === 'institution-link-mismatch') {
        if (hasClaim && riskyLinks.length > 0) {
          add(id, 'link', t.name, t.weight, fill(t.description, { institution: claimedName, host: riskyLinks[0].host }));
        }
        continue;
      }
      if (id === 'link-and-payment') {
        if (riskyLinks.length > 0 && (lang['payment-language'] || lang['money-request'] || lang['sensitive-info'] || lang['info-request'])) {
          add(id, 'link', t.name, t.weight, fill(t.description, { host: riskyLinks[0].host }));
        }
        continue;
      }
      if (id === 'link-and-reward') {
        if (riskyLinks.length > 0 && lang['reward']) {
          add(id, 'link', t.name, t.weight, fill(t.description, { host: riskyLinks[0].host }));
        }
        continue;
      }
      if (id === 'official-domain') {
        if (links.length > 0 && riskyLinks.length === 0 && links.some((b) => b.official) && !lang['sensitive-info']) {
          const b = links.find((x) => x.official);
          add(id, 'link', t.name, t.weight, fill(t.description, { host: b.host }));
        }
        continue;
      }
      const b = links.find((x) => x.signals.indexOf(id) >= 0);
      if (b) {
        add(id, 'link', t.name, t.weight, fill(t.description, {
          host: b.host, token: b.detail.token || '', institution: b.detail.institution || '', official: b.detail.official || '', tld: b.detail.tld || '',
        }));
      }
    }

    // 7. Sender
    const addSender = (id) => add(id, 'sender', S[id].name, S[id].weight, S[id].description);
    if (sender === 'mobile' && hasClaim) addSender('mobile-institution');
    if (sender === 'foreign') addSender(hasClaim ? 'foreign-institution' : 'foreign-number');
    if (sender === 'whatsapp' && hasClaim) addSender('whatsapp-institution');
    if (hasClaim && mobileRx.test(plain)) addSender('mobile-in-text');

    // 7b. Intent layer: broad meaning classes and their combinations
    for (const id of Object.keys(lang)) features['lang:' + id] = true;
    if (riskyLinks.length > 0) features['link-risky'] = true;
    if (links.some((b) => b.official)) features['link-official'] = true;
    if (links.length === 0) features['link-none'] = true;
    if (PERSONAL_SENDERS.indexOf(sender) >= 0) features['channel-personal'] = true;
    if (sender === 'header') features['channel-header'] = true;
    let intentTop = 0, intentCount = 0;
    for (const c of I.combinations) {
      if (!c.all.every((f) => features[f])) continue;
      if (c.any.length > 0 && !c.any.some((f) => features[f])) continue;
      if (c.none.some((f) => features[f])) continue;
      signals.push({ id: 'intent:' + c.id, type: 'intent', name: c.name, weight: c.score, reason: c.description });
      if (c.score > intentTop) intentTop = c.score;
      intentCount++;
    }
    let intentScore = 0;
    if (intentCount > 0) {
      const extra = (intentCount - 1) * 4;
      intentScore = intentTop + (extra > 12 ? 12 : extra);
    }
    if (intentScore > score) score = intentScore;

    if (score < 0) score = 0;
    if (score > 100) score = 100;
    const level = score >= rules.thresholds.high ? 'high' : score >= rules.thresholds.suspicious ? 'suspicious' : 'no-signs';

    // 8. What to do and the official verification path
    const whatToDo = [];
    const addStep = (a) => { if (whatToDo.indexOf(a) < 0) whatToDo.push(a); };
    if (ranked.length > 0) ranked[0].k.whatToDo.forEach(addStep);
    rules.general.basicRules.forEach(addStep);

    const verification = [];
    const addVerification = (title, vText, vLinks) => {
      if (verification.length < 3 && !verification.some((d) => d.text === vText)) verification.push({ title, text: vText, links: vLinks });
    };
    for (const e of ranked.slice(0, 2)) addVerification(e.k.name, e.k.verification.text, e.k.verification.links);
    const categoryInstitutions = {};
    for (const e of ranked) for (const id of e.k.institutions) categoryInstitutions[id] = true;
    for (const k of textInstitutions) {
      if (!categoryInstitutions[k.id] || ranked.length === 0) addVerification(k.name, k.verification, []);
    }

    return {
      score,
      level,
      categories: ranked.map((e) => ({ id: e.k.id, name: e.k.name })),
      institutions: textInstitutions.map((k) => ({ id: k.id, name: k.name })),
      signals,
      links: links.map((b) => ({ raw: b.raw, host: b.host, registeredDomain: b.registeredDomain, official: b.official, signals: b.signals })),
      whatToDo,
      verification,
      ifClicked: level === 'no-signs' ? [] : rules.general.ifClicked,
      note: level === 'no-signs' ? rules.general.noSignsWarning : rules.general.notConclusive,
      rulesVersion: rules.version,
    };
  }

  return { analyze, extractLinks, registeredDomain, version: rules.version };
}

export const LEVEL_NAMES = { 'high': 'Yüksek risk', 'suspicious': 'Şüpheli', 'no-signs': 'Belirti bulunamadı' };
