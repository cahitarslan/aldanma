#!/usr/bin/env node
// Evaluates any labelled corpus file with the current rule set.
// Usage: node tools/evaluate.mjs <corpus.json> [--split <name>] [--json] [--all]
//   --split <name>  only messages whose "split" field equals <name>
//   --json          print the result as JSON
//   --all           list every message, not only the misclassified ones
// Corpus format: corpus/SCHEMA.md
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createEngine, SENDERS } from '../web/engine.js';

const args = process.argv.slice(2);
const file = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--split');
if (!file) { console.error('Usage: node tools/evaluate.mjs <corpus.json> [--split <name>] [--json] [--all]'); process.exit(2); }
const split = args.includes('--split') ? args[args.indexOf('--split') + 1] : null;

const rules = JSON.parse(readFileSync(new URL('../rules/rules.json', import.meta.url), 'utf8'));
const engine = createEngine(rules);
const raw = JSON.parse(readFileSync(resolve(file), 'utf8'));
let messages = Array.isArray(raw) ? raw : raw.messages;
if (!Array.isArray(messages)) { console.error('Error: the file must be an array or { "messages": [...] }.'); process.exit(2); }
if (split) messages = messages.filter((m) => m.split === split);

const errors = [];
messages.forEach((m, i) => {
  const name = m.id || '#' + (i + 1);
  if (typeof m.text !== 'string') errors.push(`${name}: "text" missing`);
  if (m.label !== 'scam' && m.label !== 'legit') errors.push(`${name}: "label" must be scam or legit (got ${JSON.stringify(m.label)})`);
  if (m.sender !== undefined && !SENDERS.includes(m.sender)) errors.push(`${name}: "sender" must be one of ${SENDERS.join(', ')}`);
});
if (errors.length) { console.error('Invalid corpus:\n ' + errors.slice(0, 20).join('\n ')); process.exit(2); }

let tp = 0, fn = 0, fp = 0, tn = 0, scamHigh = 0, legitHigh = 0, catTotal = 0, catHit = 0;
const rows = [];
messages.forEach((m, i) => {
  const r = engine.analyze(m.text, m.sender || 'unknown');
  const warned = r.level !== 'no-signs';
  const scam = m.label === 'scam';
  if (scam && warned) tp++; else if (scam) fn++; else if (warned) fp++; else tn++;
  if (scam && r.level === 'high') scamHigh++;
  if (!scam && r.level === 'high') legitHigh++;
  if (scam && m.category) { catTotal++; if (r.categories.some((k) => k.id === m.category)) catHit++; }
  rows.push({ id: m.id || '#' + (i + 1), label: m.label, sender: m.sender || 'unknown', score: r.score, level: r.level, correct: scam === warned,
    categories: r.categories.map((k) => k.id), signals: r.signals.map((x) => x.id), text: m.text });
});
const n = messages.length;
const pct = (a, b) => (b === 0 ? null : Math.round((a * 1000) / b) / 10);
const summary = {
  file, split, rulesVersion: rules.version, messageCount: n,
  confusion: { truePositive: tp, falseNegative: fn, falsePositive: fp, trueNegative: tn },
  accuracy: pct(tp + tn, n), precision: pct(tp, tp + fp), recall: pct(tp, tp + fn), specificity: pct(tn, tn + fp),
  scamHighRate: pct(scamHigh, tp + fn), legitHighCount: legitHigh, categoryHitRate: pct(catHit, catTotal),
  mistakes: rows.filter((x) => !x.correct),
};
if (args.includes('--json')) {
  if (args.includes('--all')) summary.all = rows;
  console.log(JSON.stringify(summary, null, 1));
} else {
  const f = (v) => (v === null ? 'n/a' : v + '%');
  console.log(`File: ${file}${split ? ' (split: ' + split + ')' : ''} · rules ${rules.version} · ${n} messages`);
  console.log('Positive class: scam. "Warned" = the engine said suspicious or high.\n');
  console.log('Confusion matrix        warned       no signs');
  console.log(`actual scam    ${String(tp).padStart(15)} ${String(fn).padStart(14)}`);
  console.log(`actual legit   ${String(fp).padStart(15)} ${String(tn).padStart(14)}\n`);
  console.log(`Accuracy ${f(summary.accuracy)} · Precision ${f(summary.precision)} · Recall ${f(summary.recall)} · Specificity ${f(summary.specificity)}`);
  console.log(`Scam rated "high" ${f(summary.scamHighRate)} · legit rated "high": ${legitHigh}` + (catTotal ? ` · category hit rate ${f(summary.categoryHitRate)}` : ''));
  const list = args.includes('--all') ? rows : summary.mistakes;
  console.log(`\n${args.includes('--all') ? 'All messages' : 'Misclassified'} (${list.length}):`);
  for (const x of list) {
    console.log(`${x.correct ? ' ' : 'X'} ${x.id} [${x.label}, ${x.sender}] score ${x.score} ${x.level}`);
    console.log(`    ${x.text.replace(/\s+/g, ' ').slice(0, 160)}`);
    console.log(`    signals: ${x.signals.join(', ') || 'none'}`);
  }
}
