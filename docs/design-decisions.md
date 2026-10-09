# Design decisions

The decisions that shape what the numbers mean, in the order they were made. Each one says what was decided, why, and
what it costs. Definitions are in [metrics.md](metrics.md) and [annotation-guidelines.md](annotation-guidelines.md).

## 1. Sample by hash, not by random number generator

**Decision.** The golden set, the dev pool and the held-out layouts are chosen by ranking items on
`SHA-256("<seed>:<key>")` ([`StratifiedSampler`](../src/InvoiceEvals.Core/StratifiedSampler.cs)). The bootstrap uses
SplitMix64. `System.Random` is never used.

**Why.** `System.Random`'s sequence is not guaranteed across .NET versions, and a sample that depends on input order
changes when the archive is listed differently. A hash rank depends only on the seed and the item's name, so
`evals download --seed 42` rebuilds a byte-identical `evals/annotations.jsonl` on any machine.

**Cost.** None in practice. The seed is part of the dataset's identity: a different seed is a different benchmark.

## 2. Hold out whole layouts for prompt development

**Decision.** 5 of FATURA's 50 layouts (Template2, 25, 28, 29, 48 with seed 42) form the dev pool (`evals/dev/`, 20
documents). The golden set (150 documents) comes from the other 45. Prompt writing and the two few-shot examples use
the dev pool only.

**Why.** FATURA's vendor, wording and positions are fixed per layout. A few-shot example from a golden layout would
show the model the answer for every document of that layout, and a prompt tuned while reading golden documents
overfits to them. Holding out documents is not enough; whole layouts must be held out.

**Cost.** Template25, the only layout with five GST lines, is dev-only. The synthetic `many-tax-lines` family covers
that case in the scored set.

## 3. Ground truth is what is printed

**Decision.** Every golden value is the value printed on the document, even when it is arithmetically wrong. FATURA
amounts reconcile (`subtotal − discount + tax = total`) in 8 of 5,597 documents, and due dates precede invoice dates in
about half of them; ground truth keeps all of that as printed.

**Why.** An extractor that "corrects" a total is wrong about the document, and downstream systems (matching, audit)
need the printed value. `recomputed_total_rate` measures exactly this failure.

**Cost.** Scores on FATURA reward faithful copying over arithmetic sense. That is the intended behaviour, but a reader
must know it before comparing with benchmarks that reconcile totals.

## 4. Null means "not printed"

**Decision.** A golden `null` field means the document does not print it. Returning a value there is a
`false_positive`; returning `null` where a value is printed is a `miss`. `lineItems: null` is different: it means
**not annotated** (FATURA has no line-item labels), and line-item metrics skip the document.

**Why.** Hallucinated fields are the most expensive extraction error. Scoring "correct null" as a hit and reporting
false-positive and miss rates per field separates "invents values" from "misses values", which a single accuracy
number hides.

**Cost.** Field accuracy includes easy `correct_null` hits on fields a layout never prints, so accuracies are higher
than "accuracy on printed fields". The per-field miss rate is the number to read for that.

## 5. The LLM judge decides only the gray zone

**Decision.** Vendor and customer names are first compared strictly after normalization (case, punctuation, `&`,
trailing legal forms). Only when that fails **and** both names are present does a judge
([`NameJudge`](../src/InvoiceEvals.Evaluation/NameJudge.cs)) decide equivalence. The judged score is a separate column;
the composite uses the strict one.

**Why.** Most comparisons are settled deterministically, so the judge makes few calls and cannot flip clear cases. The
composite stays deterministic and comparable across judge-prompt versions. The judge's prompt is versioned, accepted
only at Cohen's κ ≥ 0.75 against hand labels, and revised on the calibration set at most once, so it is not tuned until
it agrees.

**Cost.** Judged-name scores depend on a model and are only as trustworthy as the calibration report for the prompt
version in use.

## 6. The response cache is committed

**Decision.** Every model call (extraction, name judge, agent quality judges) goes through the
`Microsoft.Extensions.AI.Evaluation.Reporting` response cache in `evals/cache/`, which is committed. `evals run
--offline` replays from it and fails on a miss. The cache key covers messages and chat options; because it ignores
`ChatOptions.Tools`, a fingerprint of the agent's tool definitions is added as a caching key.

**Why.** Anyone can reproduce every number without an API key, CI costs nothing and needs no secrets, and a changed
prompt, model, judge prompt or tool definition is a cache miss rather than a silent mix of old and new responses.
Latency is stamped on the response before caching, so replays report the original network latency.

**Cost.** Repository size grows with every refreshed run, and a PR that changes a prompt must include the re-run
cache, which needs a key.

## 7. Agent tools report, they never correct

**Decision.** The agent's two tools are pure functions. `validate_totals` reports whether printed amounts reconcile and
what the reconciled total would be; `normalize_currency` maps text to an ISO code or returns null when it is ambiguous.
The prompt says to extract totals as printed and to put an inconsistency in `warnings`, never to change a value.

**Why.** A tool that returns a "fixed" total invites the agent to overwrite the printed value, which decision 3 counts
as an error. Keeping tools descriptive makes `tool_override_rate` a clean measurement: how often the agent ignores the
instruction once its own tool tells it the totals do not add up.

**Cost.** The agent cannot "help" by correcting data, so any accuracy gain must come from better reading, not from
arithmetic.

## 8. Compare configurations on paired documents

**Decision.** `evals compare` bootstraps per-document deltas (10,000 resamples, seed 42, percentile 95% interval) over
documents paired by id, and calls a difference significant only when the interval excludes 0. Rows with n < 30 are
flagged.

**Why.** Both configurations see the same documents, so pairing removes the document-difficulty variance that an
unpaired comparison of two means would carry. The flag stops conclusions from the 30-document synthetic subset, where a
0/1 metric moves in steps of 0.033.

**Cost.** The interval covers sampling of documents, not run-to-run model variance; a cached run is one draw.

## 9. Gate on several metrics, each with its own threshold

**Decision.** `evals gate` checks seven (metric, subset) cells per config from
[`evals/thresholds.json`](../evals/thresholds.json): schema validity, composite, invoice number, total, judged vendor
name, line-items F1 (synthetic) and, for agents, `validate_totals_called_rate`. A cell fails on a drop larger than its
absolute `maxDrop`, on becoming n/a, or when a config with a baseline was not run.

**Why.** A composite can stay flat while one field collapses and another improves. Gating the fields that matter most
(totals, invoice numbers) separately catches that. Thresholds are absolute and small because cached replays are
deterministic: one document on the 180-document set is 0.0056 of a 0/1 metric, so any drop is a real change.

**Cost.** More cells means more ways to fail; a legitimate trade-off needs a baseline update in a PR that explains it.

## 10. Cover FATURA's gaps with generated edge cases

**Decision.** 30 synthetic invoices (`evals synthesize`) cover what FATURA cannot: line items, credit notes,
discounts, several currencies, multi-page documents, many tax lines, legal-suffix vendor names and due dates before
invoice dates. One record drives the image, the text layer and the golden DTO.

**Why.** FATURA has no line-item labels, three currencies, 34 vendors and only US addresses. Generating the edge cases
from one record means image, text and ground truth cannot disagree.

**Cost.** 30 documents is a small sample: synthetic-only metrics are flagged n < 30 and are indicators, not results.
