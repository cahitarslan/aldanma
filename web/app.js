// Aldanma interface: the check form.
// Messages are analysed in the browser with the open rule set. Only the phone numbers, IBANs and links found
// in them are sent to the server, which answers with community reports, block-list hits and domain age.
import { createEngine } from './engine.js';
import { findCandidates, isSingleItem, callApi } from './lookup.js';
import { $, el } from './dom.js';
import { initControls } from './controls.js';
import { renderResult } from './result.js';
import { initReporting } from './reporting.js';
import { createFeedback } from './feedback.js';

const API = (document.querySelector('meta[name="aldanma-api"]')?.content || '').trim();
const TURNSTILE = (document.querySelector('meta[name="aldanma-turnstile"]')?.content || '').trim();
const SITE = 'https://aldanma.cahitarslan.com';
const MAX_TEXT = 5000; // same limit as the API and the textarea

const rules = await (await fetch('rules.json')).json();
const engine = createEngine(rules);
$('rules-version').textContent = rules.version;
initControls();

const EXAMPLES = [
  { name: 'Sahte kargo mesajı', sender: 'foreign', text: 'PTT: Gonderiniz depoda beklemektedir. 24,90 TL yeniden gonderim ucretini odeyiniz, aksi halde iade edilecektir. http://ptt-gonderitakip.com' },
  { name: 'Kapora isteyen ilan', sender: 'whatsapp', text: 'Merhaba, ilandaki araba hâlâ satılık. Yurt dışındayım, aracı noter güvenceli gönderiyorum. Önce 5.000 TL kapora için IBAN atıyorum, bugün yatırırsanız size ayırırım.' },
  { name: 'Yabancı numara', text: '+234 803 123 4567' },
  { name: 'Benzetilmiş adres', text: 'yap1kredi.com.tr' },
];

/* ---------------------------------------------------------- the form --- */

const input = $('input');
const senderSet = $('sender-set');
const resultBox = $('result');
const formError = $('form-error');
const status = $('status');

// Reports refresh the result so the new count shows at once (single items are re-checked by the user).
const openReport = API
  ? initReporting({ api: API, turnstileKey: TURNSTILE, identifierRules: rules.identifiers, onReported: () => { if (lastResult && !lastResult.single) run(); } })
  : null;
if (!API) $('dispute').hidden = true;
const feedbackCard = API ? createFeedback({ api: API, turnstileKey: TURNSTILE, rulesVersion: rules.version }) : null;

for (const [i, o] of rules.sender.options.entries()) {
  $('senders').append(el('label', { class: 'choice' },
    el('input', { type: 'radio', name: 'sender', value: o.id, checked: i === 0 }), el('span', { text: o.name })));
}
const selectedSender = () => document.querySelector('input[name=sender]:checked')?.value || 'unknown';
const setSender = (id) => { for (const r of document.querySelectorAll('input[name=sender]')) r.checked = r.value === (id || 'unknown'); };

function updateSenderVisibility() {
  const t = input.value.trim();
  senderSet.hidden = t.length === 0 || isSingleItem(t);
}
input.addEventListener('input', () => { updateSenderVisibility(); formError.hidden = true; });

for (const ex of EXAMPLES) {
  $('examples').append(el('button', {
    type: 'button', class: 'chip', text: ex.name,
    onclick: () => { input.value = ex.text; updateSenderVisibility(); setSender(ex.sender); run(); },
  }));
}

$('clear').addEventListener('click', () => {
  input.value = '';
  resultBox.hidden = true;
  resultBox.replaceChildren();
  $('report').hidden = true;
  updateSenderVisibility();
  status.textContent = '';
  input.focus();
});

const pasteBtn = $('paste');
if (!navigator.clipboard?.readText) pasteBtn.hidden = true;
pasteBtn.addEventListener('click', async () => {
  try {
    input.value = (await navigator.clipboard.readText()).slice(0, MAX_TEXT);
    updateSenderVisibility();
    input.focus();
  } catch {
    showError('Panoya erişilemedi. Kutuya uzun basıp "Yapıştır"ı seçebilirsin.');
  }
});

function showError(text) {
  formError.textContent = text;
  formError.hidden = false;
}

$('check').addEventListener('submit', (e) => { e.preventDefault(); run(); });

let lastResult = null;

function render(r) {
  lastResult = r;
  renderResult(resultBox, r, { rules, site: SITE, onReport: openReport, feedbackCard });
}

async function run() {
  const text = input.value.trim();
  formError.hidden = true;
  if (!text) { showError('Önce kontrol edilecek numarayı, IBAN\'ı, linki ya da mesajı yaz.'); input.focus(); return; }

  const single = isSingleItem(text);
  const sender = selectedSender();
  const message = single ? null : engine.analyze(text, sender);
  const candidates = single ? [text] : findCandidates(text, engine);

  let identifiers = [];
  let lookupFailed = false;
  if (API && candidates.length > 0) {
    $('submit').disabled = true;
    status.textContent = 'İhbarlara ve listelere bakılıyor…';
    const res = await callApi(API, '/api/lookup', { items: candidates });
    $('submit').disabled = false;
    status.textContent = '';
    if (res.ok) identifiers = res.data.identifiers;
    else if (res.status === 429) { showError(res.data?.error || 'Çok sık sorgu yaptın. Biraz bekle.'); return; }
    else lookupFailed = true;
  } else if (!API && candidates.length > 0) {
    lookupFailed = true;
  }

  if (single && identifiers.length === 0) {
    if (lookupFailed) {
      // No server: a single link can still be inspected locally.
      const local = engine.analyze(text.includes('.') ? 'https://' + text.replace(/^https?:\/\//i, '') : text);
      if (local.links.length > 0) { render({ text, sender, message: local, identifiers: [], lookupFailed, single }); return; }
      showError('İhbar veritabanına şu an ulaşılamıyor. Biraz sonra tekrar dene.');
      return;
    }
    showError('Bunu bir telefon numarası, IBAN ya da internet adresi olarak tanıyamadık. Mesajın tamamını yapıştırmayı dene.');
    return;
  }
  render({ text, sender, message, identifiers, lookupFailed, single });
}

/* -------------------------------------------------------------- stats --- */

if (API) {
  callApi(API, '/api/meta').then((res) => {
    if (!res.ok) return;
    if (res.data.reportCount > 0) $('stat-reports').textContent = res.data.reportCount.toLocaleString('tr-TR') + ' ihbar';
    if (res.data.blocklistCount > 0) $('stat-blocklist').textContent = res.data.blocklistCount.toLocaleString('tr-TR') + ' adres';
  });
}

// Support links like /?q=05321234567 (used by the WhatsApp share and by bookmarks).
const q = new URLSearchParams(location.search).get('q');
if (q) { input.value = q.slice(0, MAX_TEXT); updateSenderVisibility(); run(); }

