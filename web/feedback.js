// "Was this right?" under every result. The answer sends only the level, score and rule ids the engine already
// produced, never the text. After either answer the user may also share the message itself, only by ticking a consent
// box: after "no" to get a wrong result fixed ("missed" / "false-alarm"), after "yes" as an optional, secondary offer
// that gives the corpus a confirmed example ("confirmed"). Phone numbers and IBANs are masked by the API before storage.
import { el } from './dom.js';
import { callApi } from './lookup.js';
import { humanCheck, humanToken } from './human-check.js';

export function createFeedback({ api, turnstileKey, rulesVersion }) {
  return (r, level, score) => {
    const status = el('p', { class: 'note', role: 'status' });
    const card = el('section', { class: 'card feedback', 'aria-labelledby': 'feedback-title' },
      el('h3', { id: 'feedback-title', text: 'Bu sonuç doğru muydu?' }));

    const send = async (correct) => {
      for (const b of card.querySelectorAll('.yesno button')) b.disabled = true;
      const res = await callApi(api, '/api/feedback', {
        correct, level, score, rulesVersion,
        kinds: [...new Set(r.identifiers.map((i) => i.kind))],
        categories: (r.message?.categories ?? []).map((c) => c.id),
        signals: [...new Set([...(r.message?.signals ?? []), ...r.identifiers.flatMap((i) => i.signals)].map((s) => s.id))],
      });
      status.textContent = res.ok ? 'Teşekkürler, cevabın kaydedildi.' : 'Cevap gönderilemedi.';
      if (r.message) card.append(reviewForm(r, level, correct));
    };

    card.append(
      el('div', { class: 'yesno', role: 'group', 'aria-label': 'Bu sonuç doğru muydu?' },
        el('button', { type: 'button', text: 'Evet', onclick: () => send(true) }),
        el('button', { type: 'button', text: 'Hayır', onclick: () => send(false) })),
      status);
    return card;
  };

  function reviewForm(r, level, correct) {
    const claim = correct ? 'confirmed' : level === 'no-signs' ? 'missed' : 'false-alarm';
    const consent = el('input', { type: 'checkbox', id: 'review-consent' });
    const turnstile = el('div');
    const result = el('p', { class: 'note', role: 'status' });
    const button = el('button', { class: 'btn btn-secondary', type: 'button', text: correct ? 'Mesajı paylaş' : 'Mesajı incelemeye gönder' });
    button.addEventListener('click', async () => {
      if (!consent.checked) { result.textContent = 'Göndermek için onay kutusunu işaretle.'; return; }
      button.disabled = true;
      const res = await callApi(api, '/api/submissions', {
        text: r.text, sender: r.sender, claim, level, rulesVersion, consent: true, humanToken: humanToken(turnstile),
      });
      button.disabled = res.ok;
      const done = correct ? 'Paylaştığın için teşekkürler. Bir kişi inceleyip örneklere ekleyecek.' : 'Gönderildi. Bir kişi inceleyecek; gerekiyorsa kurallar güncellenecek.';
      result.textContent = res.ok ? done : res.data?.error || 'Gönderilemedi. Tekrar dene.';
    });
    humanCheck(turnstile, turnstileKey);
    const intro = {
      confirmed: "Bu mesajı, numaralar ve IBAN'lar gizlenerek, aracı geliştirmek için paylaşmak ister misin? Paylaşmazsan hiçbir şey gönderilmez.",
      missed: 'Bu bir dolandırıcılıksa mesajı bize gönderebilirsin; kuralları ona göre güncelleriz.',
      'false-alarm': 'Bu mesaj gerçekse bize gönderebilirsin; yanlış alarmı düzeltiriz.',
    }[claim];
    return el('div', { class: correct ? 'review optional' : 'review' },
      el('p', { text: intro }),
      el('label', { class: 'choice', for: 'review-consent' }, consent,
        el('span', { text: 'Mesajın metninin incelenmek üzere saklanmasına izin veriyorum. İçindeki telefon numaraları ve IBAN\'lar kaydedilmeden önce gizlenir.' })),
      turnstile,
      button,
      result);
  }
}
