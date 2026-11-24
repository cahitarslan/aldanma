// Report and dispute forms. Both need a server; without one the site runs in message-only mode and hides them.
import { $, el } from './dom.js';
import { callApi } from './lookup.js';
import { humanCheck, humanToken, humanPending, resetHumanCheck, HUMAN_PENDING } from './human-check.js';

const MIN_REASON = 20;
// Loose on purpose: the server does the real check. This only catches an empty or obviously wrong address.
const looksLikeEmail = (s) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s);

function radios(containerId, name, options, checkedFirst) {
  const box = $(containerId);
  box.replaceChildren(...options.map((o, i) => el('label', { class: 'choice' },
    el('input', { type: 'radio', name, value: o.id, checked: checkedFirst && i === 0 }), el('span', { text: o.name }))));
}


function openReport(identifiers, turnstileKey) {
  const list = $('report-items');
  list.replaceChildren(...identifiers.map((i) => el('label', { class: 'choice' },
    el('input', { type: 'checkbox', name: 'item', value: i.display, checked: true }), el('span', { class: 'mono', text: i.display }))));
  if (identifiers.length === 0) list.append(el('p', { class: 'hint', text: 'İhbar edilecek numarayı, IBAN\'ı ya da adresi aşağıya yaz.' }));
  $('report-done').hidden = true;
  $('report-error').hidden = true;
  $('report-form').hidden = false;
  $('report').hidden = false;
  humanCheck($('report-turnstile'), turnstileKey);
  $('report').scrollIntoView({ behavior: 'smooth', block: 'start' });
  $('report-title').setAttribute('tabindex', '-1');
  $('report-title').focus();
}

/**
 * Wires the report and dispute forms and returns the function that opens the report form
 * pre-filled with the given identifiers.
 */
export function initReporting({ api, turnstileKey, identifierRules, onReported }) {
  radios('report-categories', 'category', identifierRules.reportCategories, false);
  radios('report-channels', 'channel', identifierRules.reportChannels, false);
  radios('report-losses', 'loss', identifierRules.lossBands, true);
  $('report-month').max = new Date().toISOString().slice(0, 7);

  $('report-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const err = $('report-error');
    err.hidden = true;
    const inputs = [...document.querySelectorAll('input[name=item]:checked')].map((x) => x.value);
    const extra = $('report-extra').value.trim();
    if (extra) inputs.push(extra);
    const category = document.querySelector('input[name=category]:checked')?.value;
    const channel = document.querySelector('input[name=channel]:checked')?.value;
    const loss = document.querySelector('input[name=loss]:checked')?.value || 'none';
    const box = $('report-turnstile');
    const problem = inputs.length === 0 ? 'En az bir numara, IBAN ya da adres seç.' : !category ? '"Ne oldu?" sorusunu cevapla.' : !channel ? 'Nereden iletişime geçtiklerini seç.'
      : humanPending(box, turnstileKey) ? HUMAN_PENDING : null;
    if (problem) { err.textContent = problem; err.hidden = false; return; }

    const btn = $('report-submit');
    btn.disabled = true;
    const res = await callApi(api, '/api/reports', { inputs, category, channel, loss, occurredOn: $('report-month').value || null, humanToken: humanToken(box) });
    btn.disabled = false;
    resetHumanCheck(box);
    if (res.ok) {
      const done = $('report-done');
      done.textContent = `Teşekkürler. İhbarın kaydedildi (${res.data.accepted.join(', ')}). Bir sonraki kişi bu kaydı sorduğunda uyarı görecek.`;
      done.hidden = false;
      onReported();
    } else {
      err.textContent = res.data?.error || 'İhbar gönderilemedi. Bağlantını kontrol edip tekrar dene.';
      err.hidden = false;
    }
  });

  $('dispute').querySelector('details').addEventListener('toggle', (e) => { if (e.target.open) humanCheck($('dispute-turnstile'), turnstileKey); });
  $('dispute-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const err = $('dispute-error');
    err.hidden = true;
    $('dispute-done').hidden = true;
    const box = $('dispute-turnstile');
    const input = $('dispute-input').value.trim();
    const contact = $('dispute-contact').value.trim();
    const reason = $('dispute-reason').value.trim();
    const problem = !input ? "İtiraz ettiğin numarayı, IBAN'ı ya da adresi yaz."
      : !looksLikeEmail(contact) ? 'Sana dönebilmemiz için geçerli bir e-posta adresi yaz.'
      : reason.length < MIN_REASON ? `Açıklama en az ${MIN_REASON} karakter olmalı (şu an ${reason.length}).`
      : humanPending(box, turnstileKey) ? HUMAN_PENDING : null;
    if (problem) { err.textContent = problem; err.hidden = false; return; }

    const btn = $('dispute-submit');
    btn.disabled = true;
    const res = await callApi(api, '/api/disputes', { input, contact, reason, humanToken: humanToken(box) });
    btn.disabled = false;
    resetHumanCheck(box);
    if (res.ok) {
      $('dispute-done').textContent = 'İtirazın alındı. İnceleyip e-postayla döneceğiz.';
      $('dispute-done').hidden = false;
      e.target.reset();
    } else {
      err.textContent = res.data?.error || 'İtiraz gönderilemedi. Tekrar dene.';
      err.hidden = false;
    }
  });

  return (identifiers) => openReport(identifiers, turnstileKey);
}
