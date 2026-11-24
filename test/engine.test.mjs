import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createEngine, fold, levenshtein } from '../web/engine.js';

const root = new URL('../', import.meta.url);
const rules = JSON.parse(readFileSync(new URL('rules/rules.json', root), 'utf8'));
const corpus = JSON.parse(readFileSync(new URL('corpus/corpus.json', root), 'utf8')).messages;
const engine = createEngine(rules);

test('fold simplifies Turkish letters', () => {
  assert.equal(fold('İSTANBUL ŞİŞLİ ığdır'), 'istanbul sisli igdir');
  assert.equal(fold('T.C. Kimlik No: 1.500,50 TL\'yi "öde"!'), 't.c kimlik no 1.500 50 tl yi ode');
});

test('levenshtein', () => {
  assert.equal(levenshtein('kitten', 'sitting'), 3);
  assert.equal(levenshtein('', 'abc'), 3);
});

test('every pattern is a portable regular expression and ids are unique', () => {
  const patterns = [
    ...rules.institutions.flatMap((k) => k.keywords),
    ...rules.categories.flatMap((k) => [...k.strong, ...k.weak]),
    ...rules.languageSignals.flatMap((k) => k.patterns),
    ...rules.intent.classes.flatMap((k) => k.patterns),
  ];
  for (const p of patterns) {
    assert.doesNotThrow(() => new RegExp(p), p);
    // Shared subset of both engines: no lookaround, named groups or Unicode classes; ASCII only.
    assert.ok(!/\(\?[<=!]|\\p\{|\\k</.test(p), 'non-portable construct: ' + p);
    assert.ok(/^[\x20-\x7e]+$/.test(p), 'pattern must be ASCII (it runs on folded text): ' + p);
  }
  const ids = rules.categories.map((k) => k.id);
  assert.equal(new Set(ids).size, ids.length);
  for (const k of rules.categories) for (const i of k.institutions) assert.ok(rules.institutions.some((x) => x.id === i), k.id + ' > ' + i);
});

test('official domains are lower-case, scheme-less and unique', () => {
  for (const k of rules.institutions) for (const d of k.domains) assert.match(d, /^[a-z0-9-]+(\.[a-z0-9-]+)+$/, d);
});

test('fake enforcement message is high risk and shows the official verification path', () => {
  const r = engine.analyze('Hakkınızda icra takibi başlatılmıştır. Dosyanızı kapatmak için: uyap-dosyasorgu.com/icra', 'mobile');
  assert.equal(r.level, 'high');
  assert.equal(r.categories[0].id, 'fake-enforcement');
  assert.ok(r.verification.some((v) => v.links.some((l) => l.url === 'https://www.turkiye.gov.tr')));
});

test('link signals', () => {
  const signals = (link) => engine.analyze('Detay: ' + link).links[0].signals;
  assert.ok(signals('isbank.com').includes('lookalike-spelling'));
  assert.ok(signals('ziraatbank.com.tr.guvenli.net').includes('impersonating-domain'));
  assert.ok(signals('bit.ly/abc').includes('url-shortener'));
  assert.ok(signals('http://185.10.10.10/giris').includes('ip-address'));
  assert.ok(signals('türkiye.gov.tr').includes('special-characters'));
  assert.ok(signals('gonderitakip.ptt.gov.tr').includes('official-domain'));
  assert.equal(engine.analyze('Fiyat 12.50 TL, A.Ş. ve T.C. bilgi@firma.com').links.length, 0);
});

test('the engine never calls anything "safe"; every result has a note and basic steps', () => {
  for (const m of corpus) {
    const r = engine.analyze(m.text, m.sender);
    assert.ok(['high', 'suspicious', 'no-signs'].includes(r.level));
    assert.ok(r.score >= 0 && r.score <= 100);
    assert.ok(r.note.length > 0 && r.whatToDo.length > 0);
  }
  assert.ok(!JSON.stringify(rules.general).toLocaleLowerCase('tr').includes('güvenlidir'));
});

test('accuracy does not regress on train and holdout', () => {
  for (const [split, min] of [['train', 0.97], ['holdout', 0.95]]) {
    const part = corpus.filter((m) => m.split === split);
    const correct = part.filter((m) => (engine.analyze(m.text, m.sender).level !== 'no-signs') === (m.label === 'scam')).length;
    assert.ok(correct / part.length >= min, `${split}: ${correct}/${part.length}`);
  }
});

test('the corpus is realistic but fake: no example domains and no full IBANs', () => {
  for (const m of corpus) {
    assert.ok(!/example\.(com|org|net)/.test(m.text), m.id);
    assert.ok(!/TR\d{2}(\s?\d{4}){5}\s?\d{2}/.test(m.text), m.id + ' contains a full IBAN');
  }
});

test('intent layer: catches an unfamiliar message with a familiar structure, not genuine notices', () => {
  const r = engine.analyze('Sayin abonemiz, faturaniz odenmedigi icin hizmetiniz durduruldu. Bilgilerinizi guncelleyin: abone-islem.co', 'unknown');
  assert.notEqual(r.level, 'no-signs');
  assert.ok(r.signals.some((s) => s.type === 'intent'));
  const otp = engine.analyze('Dogrulama kodunuz: 482913. Bu kodu banka personeli dahil kimseyle paylasmayiniz.', 'header');
  assert.equal(otp.level, 'no-signs');
  assert.ok(!otp.signals.some((s) => s.type === 'intent'));
  const login = engine.analyze("e-Devlet Kapisi: Hesabiniza 11.11.2026 08:37'de e-Devlet sifresi ile giris yapilmistir. Bilginiz disindaysa sifrenizi degistiriniz. turkiye.gov.tr", 'header');
  assert.equal(login.level, 'no-signs');
  const fakeLogin = engine.analyze('e-Devlet: hesabiniz askiya alinmistir. e-Devlet sifreniz ile giris yapin: turkiye-gov.tr.xyz/giris', 'mobile');
  assert.equal(fakeLogin.level, 'high');
  const code = engine.analyze('Sana 6 rakam gelecek, onu bana yazar misin', 'whatsapp');
  assert.ok(code.signals.some((s) => s.id === 'intent:sensitive-request-alone'));
});

test('intent combinations only refer to defined classes and derived features', () => {
  const known = new Set([...rules.intent.classes.map((x) => x.id), ...rules.languageSignals.map((x) => 'lang:' + x.id),
    'link-risky', 'link-official', 'link-none', 'channel-personal', 'channel-header']);
  for (const c of rules.intent.combinations) for (const f of [...c.all, ...c.any, ...c.none]) assert.ok(known.has(f), c.id + ' > ' + f);
});

test('identifier texts exist for every signal the server uses', () => {
  const ids = Object.keys(rules.identifiers.signals);
  for (const id of ['phone-foreign', 'iban-bank', 'domain-new', 'blocklist-usom', 'reported', 'co-reported', 'disputed', 'official']) assert.ok(ids.includes(id), id);
});
