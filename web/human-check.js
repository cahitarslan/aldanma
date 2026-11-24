// Cloudflare Turnstile, loaded only when a form that needs it is opened. Without a site key the check is skipped.
// A token is single-use: after any answer from the server (success or error) the widget must be reset to get a new one.

let loaded = null;

export function humanCheck(box, siteKey) {
  if (!siteKey) return;
  loaded ??= new Promise((resolve) => {
    const s = document.createElement('script');
    s.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
    s.async = true;
    s.onload = resolve;
    document.head.append(s);
  });
  loaded.then(() => {
    if (box.dataset.rendered) return;
    const theme = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
    box.dataset.widgetId = window.turnstile.render(box, { sitekey: siteKey, language: 'tr', theme });
    box.dataset.rendered = '1';
  });
}

export const humanToken = (box) => box.querySelector('input[name="cf-turnstile-response"]')?.value || null;

/** True when the check is on but the person has not finished it yet. */
export const humanPending = (box, siteKey) => Boolean(siteKey) && !humanToken(box);

export const HUMAN_PENDING = 'Göndermeden önce "insan doğrulaması" kutusunun tamamlanmasını bekle.';

/** Gets a fresh token after the old one was sent. */
export function resetHumanCheck(box) {
  if (window.turnstile && box.dataset.widgetId) window.turnstile.reset(box.dataset.widgetId);
}
