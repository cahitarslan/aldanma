// Suggests rule changes from reviewed submissions. It never edits rules/rules.json: it writes a Markdown report
// that a person reads, and every change still goes through the normal path (corpus, evaluate, parity, tests).
//
// Usage:
//   curl -H "X-Admin-Key: $KEY" "$API/api/admin/submissions?status=accepted" > reviewed.json
//   node tools/suggest-rules.mjs reviewed.json > suggestions.md
//
// Needs Anthropic credentials (ANTHROPIC_API_KEY or an `ant auth login` profile).
import { readFileSync } from 'node:fs';
import Anthropic from '@anthropic-ai/sdk';

const MODEL = 'claude-opus-5-5';

const file = process.argv[2];
if (!file) {
  console.error('Usage: node tools/suggest-rules.mjs <reviewed-submissions.json>');
  process.exit(2);
}

const submissions = JSON.parse(readFileSync(file, 'utf8')).filter((s) => s.status === 'accepted' && s.label);
if (submissions.length === 0) {
  console.error('No accepted, labelled submissions in the file. Label them in the review queue first.');
  process.exit(1);
}

const rules = JSON.parse(readFileSync(new URL('../rules/rules.json', import.meta.url), 'utf8'));
// The model needs the categories and their patterns to propose changes in the same style; the rest is noise.
const ruleContext = {
  version: rules.version,
  categories: rules.categories.map(({ id, name, weight, strong, weak }) => ({ id, name, weight, strong, weak })),
  intentClasses: rules.intent?.classes,
};

const schema = {
  type: 'object',
  additionalProperties: false,
  required: ['suggestions'],
  properties: {
    suggestions: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['submissionIds', 'change', 'categoryId', 'patterns', 'rationale', 'falsePositiveRisk'],
        properties: {
          submissionIds: { type: 'array', items: { type: 'string' } },
          change: { type: 'string', enum: ['add-strong-pattern', 'add-weak-pattern', 'new-category', 'narrow-pattern', 'no-change'] },
          categoryId: { type: 'string' },
          patterns: { type: 'array', items: { type: 'string' } },
          rationale: { type: 'string' },
          falsePositiveRisk: { type: 'string' },
        },
      },
    },
  },
};

const instructions = `You help maintain the open rule set of Aldanma, a Turkish scam-message checker.
Each submission below was reviewed by a person and labelled "scam" or "legit". "missed" means the engine did not flag a scam;
"false-alarm" means it flagged a genuine message; "confirmed" means the user said the result was right (use those as
evidence of what already works and must keep working, not as cases to fix). Propose the smallest rule changes that would
fix the wrong results.

Pattern rules: patterns run on folded text (lower-case ASCII; ç→c, ğ→g, ı→i, ö→o, ş→s, ü→u) and must work in both
JavaScript and .NET regular expressions: no lookaround, no named groups, no Unicode classes. Prefer specific phrases over
single words. For a false alarm, prefer narrowing an existing pattern. Use "no-change" when a case cannot be fixed safely.
For every suggestion, say concretely which genuine messages it could wrongly flag. Write rationale and risk in English.`;

const client = new Anthropic();
const response = await client.messages.create({
  model: MODEL,
  max_tokens: 16000,
  output_config: { effort: 'high', format: { type: 'json_schema', schema } },
  messages: [{
    role: 'user',
    content: `${instructions}\n\n<rules>\n${JSON.stringify(ruleContext)}\n</rules>\n\n<submissions>\n${JSON.stringify(
      submissions.map(({ id, text, sender, claim, level, label, rulesVersion }) => ({ id, text, sender, claim, level, label, rulesVersion })),
    )}\n</submissions>`,
  }],
});

if (response.stop_reason === 'refusal' || response.stop_reason === 'max_tokens') {
  console.error(`The model stopped early (${response.stop_reason}); no suggestions written.`);
  process.exit(1);
}
const text = response.content.find((b) => b.type === 'text')?.text ?? '{"suggestions":[]}';
const { suggestions } = JSON.parse(text);

const lines = [
  `# Rule suggestions (rules ${rules.version}, ${submissions.length} reviewed submissions)`,
  '',
  'Suggestions only. Nothing here is applied automatically. For each one you accept: edit `rules/rules.json`, add the',
  'submission to `corpus/corpus.json`, then run `npm test`, `node tools/evaluate.mjs corpus/corpus.json`, `npm run parity`',
  'and `dotnet test` (see CONTRIBUTING.md).',
  '',
];
for (const [i, s] of suggestions.entries()) {
  lines.push(`## ${i + 1}. ${s.change} → \`${s.categoryId}\``, '');
  if (s.patterns.length) lines.push(...s.patterns.map((p) => `- \`${p}\``), '');
  lines.push(`**Why:** ${s.rationale}`, '', `**False-positive risk:** ${s.falsePositiveRisk}`, '', `Submissions: ${s.submissionIds.join(', ')}`, '');
}
process.stdout.write(lines.join('\n'));
