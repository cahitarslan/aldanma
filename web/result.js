// Renders a check result: verdict sign, next steps, what is known about each identifier, reasons and help.
import { el, svg } from './dom.js';

const LEVEL = {
  high: { word: 'TEHLİKE', title: 'Büyük olasılıkla dolandırıcılık.', summary: 'Linke tıklama, numarayı arama, para ya da bilgi gönderme.' },
  suspicious: { word: 'DİKKAT', title: 'Dikkat: şüpheli işaretler var.', summary: 'Bir şey yapmadan önce resmî yoldan doğrula.' },
  'no-signs': { word: 'BİLGİ', title: 'Bilinen bir dolandırıcılık işareti bulamadık.', summary: 'Bu, güvenli olduğu anlamına gelmez. Yeni dolandırıcılıkları henüz tanımıyor olabiliriz.' },
};
const LEVEL_RANK = { 'no-signs': 0, suspicious: 1, high: 2 };
const LEVEL_TAG = { high: 'Yüksek risk', suspicious: 'Şüpheli', 'no-signs': 'Belirti yok' };

const KIND_NAME = { phone: 'Telefon numarası', iban: 'IBAN', domain: 'İnternet adresi', url: 'İnternet adresi' };
const KIND_LOWER = { phone: 'numara', iban: 'IBAN', domain: 'adres', url: 'adres' };
const PHONE_TYPE = {
  Mobile: 'Cep telefonu', Landline: 'Sabit hat', NonGeographic: '0850 hattı', TollFree: 'Ücretsiz hat (0800)',
  Premium: 'Ücretli hat (0900)', BusinessShort: 'Kurumsal hat (444)', ShortCode: 'Kısa numara', Foreign: 'Yurt dışı numarası',
};

const STEPS = {
  phone: ['Bu numarayı arama, mesajlarına cevap verme.', 'Kendini banka, polis, savcı ya da kargo diye tanıtıyorsa kapat; kurumu kendi bildiğin resmî numaradan ara.', 'Telefona gelen kodu, şifreni ya da kart bilgini kimseye söyleme.'],
  iban: ['Bu IBAN\'a para gönderme.', 'Satıcıyı ya da kişiyi başka bir yoldan doğrula: yüz yüze, görüntülü arama ya da tanıdığın biri aracılığıyla.', 'Para gönderdiysen hemen bankanı ara, işlemi bildir ve savcılığa başvur.'],
  link: ['Bu linki açma.', 'Kurumun adresini tarayıcıya kendin yaz ya da resmî uygulamasını aç.', 'Açtıysan hiçbir bilgi girme; girdiysen şifreni değiştir ve bankanı ara.'],
};

const SELF_CHECK = [
  'Tıklaman istenen bir bağlantı var mı?',
  'Şifre, kod, kart ya da kimlik bilgisi isteniyor mu?',
  'Para göndermen ya da ödeme yapman isteniyor mu?',
  'Acele ettiriliyor ya da korkutuluyor musun?',
];

/** Road-sign shaped verdict badge: red triangle, yellow diamond, blue circle. The word keeps the meaning without colour. */
function sign(level) {
  const s = svg('svg', { viewBox: '0 0 100 100', class: 'sign', role: 'img', 'aria-label': LEVEL_TAG[level] });
  if (level === 'high') {
    s.append(svg('path', { d: 'M50 6 96 90H4Z', fill: '#fff', stroke: '#b3141f', 'stroke-width': '9', 'stroke-linejoin': 'round' }));
    s.append(svg('text', { x: '50', y: '80', 'text-anchor': 'middle', 'font-size': '46', fill: '#17181a' }, '!'));
  } else if (level === 'suspicious') {
    s.append(svg('path', { d: 'M50 4 96 50 50 96 4 50Z', fill: '#f2b705', stroke: '#17181a', 'stroke-width': '4', 'stroke-linejoin': 'round' }));
    s.append(svg('text', { x: '50', y: '61', 'text-anchor': 'middle', 'font-size': '32', fill: '#17181a' }, '?'));
  } else {
    s.append(svg('circle', { cx: '50', cy: '50', r: '45', fill: '#0a4a9e', stroke: '#fff', 'stroke-width': '4' }));
    s.append(svg('text', { x: '50', y: '64', 'text-anchor': 'middle', 'font-size': '40', fill: '#fff' }, 'i'));
  }
  return s;
}

const fmtDate = (iso) => new Date(iso + (iso.length === 10 ? 'T12:00:00Z' : '')).toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' });
const plural = (n) => n.toLocaleString('tr-TR');

function maxLevel(levels) {
  return levels.reduce((a, b) => (LEVEL_RANK[b] > LEVEL_RANK[a] ? b : a), 'no-signs');
}

export function renderResult(box, r, { rules, site, onReport, feedbackCard }) {
  const levels = [r.message?.level, ...r.identifiers.map((i) => i.level)].filter(Boolean);
  const level = maxLevel(levels);
  const score = Math.max(r.message?.score ?? 0, ...r.identifiers.map((i) => i.score));
  const L = LEVEL[level];

  const parts = [];

  // 1. Verdict
  const lead = leadSentence(r, level);
  parts.push(el('section', { class: `verdict ${level}`, 'aria-labelledby': 'verdict-title' },
    sign(level),
    el('div', {},
      el('h2', { id: 'verdict-title', text: L.title }),
      lead ? el('p', { text: lead }) : null,
      el('p', { text: L.summary }),
      el('p', { class: 'meter', text: `Risk puanı: 100 üzerinden ${score}` }))));

  // 2. What to do now
  const steps = stepsFor(r, level, rules);
  parts.push(el('section', { class: 'card', 'aria-labelledby': 'steps-title' },
    el('h3', { id: 'steps-title', text: 'Şimdi ne yapmalısın?' }),
    el('ol', { class: 'steps' }, ...steps.map((s) => el('li', { text: s })))));

  // 3. What we know about each number, IBAN and address
  if (r.identifiers.length > 0) {
    parts.push(el('section', { class: 'card', 'aria-labelledby': 'ids-title' },
      el('h3', { id: 'ids-title', text: r.identifiers.length === 1 ? 'Bu kayıt hakkında bildiklerimiz' : 'Mesajdaki numara, IBAN ve adresler' },
      ),
      el('div', { class: 'stack' }, ...r.identifiers.map(identityCard))));
  }
  if (r.lookupFailed) {
    parts.push(el('p', { class: 'note', text: 'İhbar veritabanına şu an ulaşılamadı; sonuç yalnızca mesajın kendisine göre. Biraz sonra tekrar deneyebilirsin.' }));
  }

  // 4. Why (message signals)
  if (r.message && r.message.signals.length > 0) {
    parts.push(reasonsCard('Mesajda neler dikkat çekti?', r.message.signals));
  }

  // 5. Self-check when the engine is unsure
  if (r.message && level !== 'high') parts.push(selfCheck());

  // 6. Official verification
  if (r.message && r.message.verification.length > 0) {
    parts.push(el('section', { class: 'card', 'aria-labelledby': 'verify-title' },
      el('h3', { id: 'verify-title', text: 'Resmî yoldan nasıl doğrularsın?' }),
      ...r.message.verification.map((v) => el('div', { class: 'verify-item' },
        el('strong', { text: v.title }), el('p', { class: 'tight', text: v.text }),
        v.links.length ? el('div', { class: 'verify-links' }, ...v.links.map((l) => el('a', { href: l.url, rel: 'noopener', target: '_blank', text: l.name }))) : null))));
  }

  // 7. If you already clicked
  if (level !== 'no-signs') {
    const clicked = r.message?.ifClicked?.length ? r.message.ifClicked : rules.general.ifClicked;
    parts.push(el('section', { class: 'card clicked', 'aria-labelledby': 'clicked-title' },
      el('h3', { id: 'clicked-title', text: 'Tıkladıysan ya da para gönderdiysen' }),
      el('ul', { class: 'plain-list' }, ...clicked.map((c) => el('li', { text: c })))));
  }

  // 8. Actions
  const reportable = r.identifiers.filter((i) => i.reportable);
  parts.push(el('div', { class: 'row' },
    el('a', { class: 'btn btn-secondary', href: whatsappLink(r, level, site), target: '_blank', rel: 'noopener', text: 'Ailene WhatsApp\'tan gönder' }),
    onReport ? el('button', { class: 'btn btn-danger', type: 'button', text: 'Dolandırıcılığı ihbar et', onclick: () => onReport(reportable) }) : null));

  if (feedbackCard) parts.push(feedbackCard(r, level, score));

  parts.push(el('p', { class: 'note', text: r.message ? r.message.note : 'Bu sonuç kesin hüküm değildir; bildiklerimize dayanan bir ön kontroldür.' }));

  box.replaceChildren(...parts);
  box.hidden = false;
  box.focus();
  box.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'start' });
}

function leadSentence(r, level) {
  const reported = r.identifiers.filter((i) => i.reports.reporterCount > 0).sort((a, b) => b.reports.reporterCount - a.reports.reporterCount)[0];
  if (reported) return `${plural(reported.reports.reporterCount)} kişi bu ${KIND_LOWER[reported.kind]} için dolandırıcılık ihbarında bulundu.`;
  const listed = r.identifiers.find((i) => i.signals.some((s) => s.id === 'blocklist-usom'));
  if (listed) return 'Bu adres USOM\'un zararlı bağlantılar listesinde.';
  if (r.message && r.message.categories.length > 0 && level !== 'no-signs') return `Mesaj, "${r.message.categories[0].name}" türüne benziyor.`;
  const top = r.identifiers.flatMap((i) => i.signals).filter((s) => s.weight > 0).sort((a, b) => b.weight - a.weight)[0];
  if (top) return top.reason;
  return '';
}

function stepsFor(r, level, rules) {
  if (r.message && level !== 'no-signs' && r.message.categories.length > 0) return r.message.whatToDo.slice(0, 5);
  if (level === 'no-signs') return rules.general.basicRules;
  const kinds = r.identifiers.map((i) => (i.kind === 'url' || i.kind === 'domain' ? 'link' : i.kind));
  const steps = [];
  for (const k of ['iban', 'phone', 'link']) if (kinds.includes(k)) for (const s of STEPS[k]) if (!steps.includes(s)) steps.push(s);
  return (steps.length ? steps : r.message?.whatToDo ?? rules.general.basicRules).slice(0, 5);
}

function identityCard(i) {
  const kindLine = [KIND_NAME[i.kind]];
  if (i.details.phoneType) kindLine.push(PHONE_TYPE[i.details.phoneType] || '');
  if (i.details.bank) kindLine.push(i.details.bank);
  if (i.details.country) kindLine.push(i.details.country);
  if (i.details.registeredOn) kindLine.push('Kayıt: ' + fmtDate(i.details.registeredOn));

  const rep = i.reports;
  let reportsLine;
  if (i.official) reportsLine = el('p', { class: 'reports-none', text: 'Tanınan resmî adres. Bu adres için ihbar alınmaz.' });
  else if (rep.reporterCount > 0) {
    const cats = rep.categories.slice(0, 2).map((c) => c.name.toLocaleLowerCase('tr')).join(', ');
    reportsLine = el('p', { class: 'reports-line', text: `${plural(rep.reporterCount)} kişi ihbar etti · son ihbar ${fmtDate(rep.lastReportedOn)}${cats ? ' · en sık: ' + cats : ''}` });
  } else reportsLine = el('p', { class: 'reports-none', text: 'Henüz ihbar yok. Bu, güvenli olduğu anlamına gelmez.' });

  const reasons = i.signals.filter((s) => s.id !== 'reported');
  return el('div', { class: `identity ${i.level}` },
    el('div', { class: 'identity-head' },
      el('span', { class: 'value mono', text: i.display }),
      el('span', { class: `tag ${i.level}`, text: LEVEL_TAG[i.level] })),
    el('span', { class: 'kind', text: kindLine.filter(Boolean).join(' · ') }),
    reportsLine,
    rep.disputed ? el('p', { class: 'note', text: 'Bu kayıt için itiraz var; inceleniyor.' }) : null,
    reasons.length ? el('details', {}, el('summary', { text: 'Ayrıntılar' }), reasonList(reasons)) : null);
}

function reasonList(signals) {
  return el('ul', { class: 'reasons' }, ...signals.map((s) => el('li', { class: s.weight >= 25 ? 'strong' : s.weight > 0 ? 'mild' : 'good' },
    el('strong', { text: s.name }), el('span', { text: s.reason }))));
}

function reasonsCard(title, signals) {
  const sorted = [...signals].sort((a, b) => b.weight - a.weight);
  const first = sorted.slice(0, 4);
  const rest = sorted.slice(4);
  return el('section', { class: 'card', 'aria-label': title },
    el('h3', { text: title }),
    reasonList(first),
    rest.length ? el('details', {}, el('summary', { text: `${rest.length} işaret daha` }), reasonList(rest)) : null);
}

function selfCheck() {
  const answers = SELF_CHECK.map(() => null);
  const line = el('p', { class: 'verdict-line', role: 'status' });
  const update = () => {
    if (answers.some((a) => a === true)) line.textContent = 'Bağlantıya tıklama, bilgi ya da para gönderme. Kurumu kendi bildiğin numaradan ya da resmî uygulamasından kontrol et.';
    else if (answers.every((a) => a === false)) line.textContent = 'İyi. Yine de emin değilsen gönderene başka bir yoldan ulaşıp sor.';
    else line.textContent = '';
  };
  return el('section', { class: 'card selfcheck', 'aria-labelledby': 'self-title' },
    el('h3', { id: 'self-title', text: 'Kendin de kontrol et' }),
    ...SELF_CHECK.map((q, i) => {
      const group = el('div', { class: 'yesno', role: 'group', 'aria-label': q });
      for (const [label, value] of [['Evet', true], ['Hayır', false]]) {
        const b = el('button', { type: 'button', 'aria-pressed': 'false', text: label });
        b.addEventListener('click', () => {
          answers[i] = value;
          for (const x of group.children) x.setAttribute('aria-pressed', String(x === b));
          update();
        });
        group.append(b);
      }
      return el('div', { class: 'q' }, el('span', { text: q }), group);
    }),
    line);
}

function whatsappLink(r, level, site) {
  const head = { high: '⚠️ Dikkat, bu büyük olasılıkla dolandırıcılık.', suspicious: '⚠️ Bu şüpheli görünüyor.', 'no-signs': 'Şunu kontrol ettim.' }[level];
  const what = r.identifiers.length > 0
    ? r.identifiers.map((i) => `${KIND_NAME[i.kind]}: ${i.reports.reporterCount > 0 ? plural(i.reports.reporterCount) + ' ihbar' : LEVEL_TAG[i.level]}`).join('\n')
    : (r.message?.categories[0]?.name ?? '');
  const text = `${head}\n${what}\nLinke tıklamadan, para göndermeden önce sen de bak: ${site}`;
  return 'https://wa.me/?text=' + encodeURIComponent(text);
}
