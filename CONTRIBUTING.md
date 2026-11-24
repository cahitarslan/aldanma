# Contributing

## Report a missed or wrongly flagged message

Open an issue with the "Missed or wrongly flagged message" template. Issues are public: remove names, phone numbers, IBANs,
ID and card numbers first. Messages are never learned automatically: someone reads each one, turns it into a rule, and the
rule ships only after tests and measurement pass. Automatic learning would let scammers train the tool too.

## Work through the review queue

Messages users send for review are in `GET /api/admin/submissions` (header `X-Admin-Key`). Read each one and label it:
`POST /api/admin/submissions/{id}` with `{ "accept": true, "label": "scam" }` (or `"legit"`), or `{ "accept": false }` to discard.
Then:

```bash
curl -H "X-Admin-Key: $KEY" "$API/api/admin/submissions?status=accepted" > reviewed.json
npm run suggest-rules -- reviewed.json > suggestions.md
```

`suggestions.md` lists candidate patterns with their false-positive risk. It changes nothing; apply what you agree with as below.

## Change the rules

1. Edit `rules/rules.json`. Patterns run on folded text (lower-case ASCII, Turkish letters simplified) and must use the subset
   both engines support: no lookaround, named groups or Unicode classes.
2. Add the message that motivated the change to `corpus/corpus.json` (see [corpus/SCHEMA.md](corpus/SCHEMA.md)).
3. Run:
   ```bash
   npm test
   node tools/evaluate.mjs corpus/corpus.json --split hard
   npm run parity        # regenerate the expected JS output
   dotnet test           # C# must match JS on every input
   ```
4. Bump `version` in `rules/rules.json` and report the measurement in the pull request.

## Code

- Code, comments, commit messages and docs are in English. User-facing text is Turkish and lives in `rules/rules.json` or `web/`.
- `web/engine.js` and `src/Aldanma.Core/Messages/MessageEngine.cs` are line-by-line twins. Change both or neither.
- Never log or store message text. The one exception is the review queue, which needs explicit consent and masks numbers and IBANs first. Phone numbers and IBANs are otherwise stored only as keyed hashes.
