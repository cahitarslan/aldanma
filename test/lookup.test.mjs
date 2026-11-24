import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createEngine } from '../web/engine.js';
import { findCandidates, isSingleItem } from '../web/lookup.js';

const rules = JSON.parse(readFileSync(new URL('../rules/rules.json', import.meta.url), 'utf8'));
const engine = createEngine(rules);

test('single items are told apart from messages', () => {
  assert.ok(isSingleItem('0532 123 45 67'));
  assert.ok(isSingleItem('+90 (532) 123-45-67'));
  assert.ok(isSingleItem('TR85 0001 0000 0000 0012 3456 78'));
  assert.ok(isSingleItem('kargo-takip.xyz'));
  assert.ok(!isSingleItem('Kargonuz bekliyor: kargo-takip.xyz'));
});

test('candidates are found in a listing, in order, without duplicates', () => {
  const text = `Sahibinden temiz iPhone 15. Kaporayı TR85 0001 0000 0000 0012 3456 78 hesabına yatırın,
    detay için 0532 123 45 67 ya da +44 7911 123456 WhatsApp. Takip: https://kargo-takip-ptt.xyz/takip
    Tekrar: 05321234567`;
  assert.deepEqual(findCandidates(text, engine), [
    'TR85 0001 0000 0000 0012 3456 78', '0532 123 45 67', '+44 7911 123456', 'https://kargo-takip-ptt.xyz/takip',
  ]);
});

test('prices and dates are not phone numbers', () => {
  assert.deepEqual(findCandidates('Fiyat 18.000 TL, 12.11.2026 tarihinde 3 adet.', engine), []);
});
