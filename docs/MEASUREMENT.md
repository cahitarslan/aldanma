# Measurement

Rules version 0.5.0. 0.5.0 renamed the rule set to English keys and added the identifier layer; message scoring is unchanged from 0.4.1
(the JS output was compared field by field against 0.4.1 for all 304 inputs before the rename was accepted).

Reproduce: `node tools/evaluate.mjs corpus/corpus.json --split <split>`

## Limits first

- The corpus is **synthetic**: all 279 messages were written by the rule author, based on scam patterns reported in the press.
- When rules are tuned while looking at a split, results on that split are **optimistic**. Only splits the rules had never seen are honest.
- Splits are small (30 to 126 messages); percentages move several points with one message.
- Positive class is scam. "Warned" means the engine said suspicious or high. Recall = share of scams warned about. Precision = share of warnings that were scams.

## Blind results: each version on data it had never seen

| Version | Unseen split | Accuracy | Precision | Recall | TP / FN / FP / TN |
|---|---|---:|---:|---:|---|
| 0.1.0 | holdout (53) | 98.1% | 96.3% | 100% | 26 / 0 / 1 / 26 |
| 0.1.0 | hard (40) | 45.0% | 40.0% | 20.0% | 4 / 16 / 6 / 14 |
| 0.2.0 | blind1 (30) | 66.7% | 100% | 33.3% | 5 / 10 / 0 / 15 |
| 0.3.0 | blind2 (30) | 60.0% | 80.0% | 26.7% | 4 / 11 / 1 / 14 |
| 0.4.0 | blind3 (80, independent author) | | | 95% (38/40) | 4 of 40 genuine messages warned |

The first three versions memorised phrases and did not generalise (recall 20-33% on new wording). 0.4.0 added the intent layer
(broad meaning classes and their combinations) and was then measured on 80 messages written independently, without seeing the rules.
blind3 is not part of this repository.

## Current rules on the repository corpus (not blind)

| Split | Messages | Accuracy | Precision | Recall | TP / FN / FP / TN | Genuine rated "high" |
|---|---:|---:|---:|---:|---|---:|
| train | 126 | 100% | 100% | 100% | 62 / 0 / 0 / 64 | 0 |
| holdout | 53 | 100% | 100% | 100% | 26 / 0 / 0 / 27 | 0 |
| hard | 40 | 85.0% | 79.2% | 95.0% | 19 / 1 / 5 / 15 | 2 |
| blind1 | 30 | 100% | 100% | 100% | 15 / 0 / 0 / 15 | 0 |
| blind2 | 30 | 96.7% | 93.8% | 100% | 15 / 0 / 1 / 14 | 0 |

These look good because the rules were written while looking at these splits. They guard against regressions; they do not prove real-world accuracy.

## What is not measured yet

- The identifier layer (reports, USOM, domain age). Its accuracy depends on the data that accumulates after launch.
- Real user messages. A consented, anonymised sample after launch is the next honest measurement.
