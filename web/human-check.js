// Cloudflare Turnstile, loaded only when a form that needs it is opened. Without a site key the check is skipped.

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
    window.turnstile.render(box, { sitekey: siteKey, language: 'tr', theme });
    box.dataset.rendered = '1';
  });
}

export const humanToken = (box) => box.querySelector('input[name="cf-turnstile-response"]')?.value || null;
