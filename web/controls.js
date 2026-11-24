// Theme and text-size buttons. prefs.js applies the saved choice before first paint; this keeps the buttons in sync.
import { $ } from './dom.js';

export function initControls() {
  const root = document.documentElement;
  const themeBtn = $('theme-toggle');
  const sizeBtn = $('size-toggle');
  const SIZES = ['', 'large', 'xlarge'];
  const SIZE_LABEL = { '': 'Yazıyı büyüt', large: 'Daha da büyüt', xlarge: 'Normal yazı' };

  function syncPrefs() {
    const dark = root.getAttribute('data-theme') === 'dark';
    themeBtn.setAttribute('aria-pressed', String(dark));
    themeBtn.textContent = dark ? 'Açık tema' : 'Koyu tema';
    document.querySelector('meta[name="theme-color"]').content = dark ? '#121315' : '#f6f3ea';
    sizeBtn.textContent = SIZE_LABEL[root.getAttribute('data-size') || ''];
  }
  function save(key, value) { try { localStorage.setItem(key, value); } catch { /* ignore */ } }

  themeBtn.addEventListener('click', () => {
    const dark = root.getAttribute('data-theme') !== 'dark';
    root.setAttribute('data-theme', dark ? 'dark' : 'light');
    save('aldanma.theme', dark ? 'dark' : 'light');
    syncPrefs();
  });
  sizeBtn.addEventListener('click', () => {
    const next = SIZES[(SIZES.indexOf(root.getAttribute('data-size') || '') + 1) % SIZES.length];
    if (next) root.setAttribute('data-size', next); else root.removeAttribute('data-size');
    save('aldanma.size', next);
    syncPrefs();
  });
  syncPrefs();
}
