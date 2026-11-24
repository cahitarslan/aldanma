// Writes the JS engine's full result for every corpus message to corpus/expected-js.json.
// The C# ParityTests produce their own result for the same inputs and compare field by field.
// With --check it does not write; it fails if the existing file is out of date (for CI).
import { readFileSync, writeFileSync } from 'node:fs';
import { createEngine } from '../web/engine.js';
const root = new URL('../', import.meta.url);
const read = (p) => JSON.parse(readFileSync(new URL(p, root), 'utf8'));
const rules = read('rules/rules.json');
const engine = createEngine(rules);
const inputs = [...read('corpus/corpus.json').messages, ...read('corpus/parity-extra.json').messages];
const results = {};
for (const m of inputs) results[m.id] = engine.analyze(m.text, m.sender);
const output = JSON.stringify({ rulesVersion: rules.version, count: inputs.length, results }, null, 1) + '\n';
const target = new URL('corpus/expected-js.json', root);
if (process.argv.includes('--check')) {
  if (readFileSync(target, 'utf8') !== output) { console.error('expected-js.json is out of date: run "npm run parity".'); process.exit(1); }
  console.log('expected-js.json is up to date (' + inputs.length + ' inputs).');
} else {
  writeFileSync(target, output);
  console.log('written: corpus/expected-js.json (' + inputs.length + ' inputs)');
}
