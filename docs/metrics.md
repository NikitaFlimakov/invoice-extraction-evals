# Metrics

All metrics are deterministic and computed per document by the evaluators in
[`src/InvoiceEvals.Evaluation`](../src/InvoiceEvals.Evaluation/), always against the golden `InvoiceDto`
(see [annotation-guidelines.md](annotation-guidelines.md) for how ground truth is produced). `evals report`
aggregates them per run, configuration and subset (FATURA, synthetic, combined).

## Schema validity (`schema_validity`)

1 if **all** of the following hold, else 0. Each violation is recorded as an error diagnostic on the metric.

1. The response parses into `InvoiceDto` (JSON, camelCase, dates as `yyyy-MM-dd` so they parse as `DateOnly`, amounts as numbers).
2. At least one of `invoiceNumber`, `invoiceDate`, `total` is non-null.
3. `currency`, when present, is a 3-letter upper-case code (`^[A-Z]{3}$`).
4. Every line item has a non-empty `description`.

## Field accuracy (`field.<name>`)

One 0/1 metric per field: `invoice_number`, `invoice_date`, `due_date`, `currency`, `subtotal`, `discount`, `tax`,
`total`, `vendor_name`, `customer_name`. Addresses are not scored.

| Field | Correct when |
|---|---|
| invoice number | equal after trimming and upper-casing |
| dates | `DateOnly` equality |
| currency | equal ISO 4217 code after trimming and upper-casing |
| amounts | within **0.01 inclusive** |
| vendor / customer name | equal after normalization: lower-case, `&` → `and`, punctuation removed, trailing legal-form suffixes removed (`Inc`, `LLC`, `Ltd`, `GmbH & Co. KG`, `S.à r.l.`, `K.K.`, ...). Abbreviations (`Intl.` vs `International`) are **not** equated here; the judged variant below handles them. Diacritics are kept. |

Golden `null` means "not printed". Each metric carries an outcome in `Metadata["outcome"]`:

| Golden | Predicted | Outcome | Score |
|---|---|---|---|
| null | null | `correct_null` | 1 |
| null | value | `false_positive` | 0 |
| value | null | `miss` | 0 |
| value | equal | `correct_value` | 1 |
| value | different | `mismatch` | 0 |
| any | response did not parse | `unparsed` | 0 |

Reported per field: **accuracy** (mean score), **false-positive rate** (`false_positive` / documents with golden null)
and **miss rate** (`miss` / documents with golden value). "Dates" in the README is the mean of `invoice_date` and
`due_date` accuracy.

## Judged names (`field.vendor_name_judged`, `field.customer_name_judged`)

The strict name metrics above stay as they are. Next to them, `evals run` emits a judged variant that differs only in
the **gray zone**: strict comparison says `mismatch` and both names are non-null. Only there an LLM judge decides
whether the extracted name denotes the same party. Every other outcome (correct, miss, false positive, unparsed) is
copied from the strict metric without a model call. Summary columns: `vendor_name_judged_accuracy`,
`customer_name_judged_accuracy`.

- Judge: [`NameJudge`](../src/InvoiceEvals.Evaluation/NameJudge.cs), `claude-haiku-4-5`, temperature 0, one
  structured-output call returning `{reason, equivalent}`. Prompt: [`evals/judge/judge_prompt.md`](../evals/judge/judge_prompt.md),
  versioned in its header.
- Input: the field, the annotated name, the extracted name, and up to 7 lines (≤ 1,500 characters) of the document's
  **text layer** around the annotated name, so the judge sees the printed form and the other parties on the page.
- Judge calls go through the same Reporting response cache as extractions, so replays (and CI) never call the API.
- An unparseable judge response scores as a mismatch, with a warning diagnostic.
- **OCR mode on FATURA:** the OCR layer never contains the vendor name (see
  [annotation-guidelines.md](annotation-guidelines.md#model-input-text-layers)). The judge is not asked about the vendor
  there; the judged vendor score stays a mismatch (`judge = not_in_model_input`). The caveat is the dataset's, not
  the judge's.
- Metadata `judge` on each judged metric: `not_needed`, `equivalent`, `not_equivalent`, `unparsed`, `not_in_model_input`.
- The composite uses the **strict** name metrics, so it stays deterministic and comparable with earlier runs.

### Calibration

`evals judge pairs` writes [`evals/judge/calibration_pairs.jsonl`](../evals/judge/): every distinct real gray-zone
mismatch from the latest run of each config, topped up to 60 with seeded synthetic perturbations of golden names (legal
suffix added, abbreviation, 1–3 OCR-style character errors, reordered words, a different company containing the
golden name, a different party). Every pair is verified to be in the gray zone. `human_label` is filled in by hand;
regenerating keeps labels by pair id. `evals judge calibrate` runs the judge on every labelled pair and writes
`calibration_report_v<version>.md` with Cohen's κ, the confusion matrix, agreement per origin and every disagreement
with the judge's reason. Target κ ≥ 0.75. The prompt is revised on this set **at most once**; a revision bumps the
version and gets its own report, and the earlier report is kept.

## Recomputed total (`recomputed_total_rate`)

1 when the predicted total differs from the printed total but equals printed `subtotal − discount + tax` (each
within 0.01), i.e. the model "fixed" the arithmetic instead of extracting. 0 otherwise. Not applicable when the golden
total or subtotal is not printed, when the printed amounts already reconcile (the two behaviours are then
indistinguishable), or when the response does not parse. Synthetic amounts always reconcile, so this metric only
fires on FATURA.

## Line items (`line_items_precision`, `line_items_recall`, `line_items_f1`)

Only for documents whose golden `lineItems` is not null (synthetic). FATURA rows report "n/a" and the summary reports
coverage (documents scored). Golden lines are visited in order; each is matched to the unused predicted line with
amount within 0.01 and the highest description overlap, provided overlap ≥ 0.5 (Jaccard of lower-case alphanumeric
tokens). Precision = matched / predicted, recall = matched / golden. Empty vs empty scores 1; an empty side scores 0
on the ratio that divides by it.

## Composite (`composite`)

Weighted mean, weights in `CompositeScoreEvaluator.Weights`:

| Component | Weight |
|---|---|
| schema validity | 0.1 |
| mean of the 10 field accuracies | 0.6 |
| line-items F1 | 0.2 |
| 1 − recomputed_total_rate | 0.1 |

A component that is not applicable to a document is dropped and the remaining weights are rescaled to sum to 1. So a
FATURA document with reconciling amounts scores `(0.1·schema + 0.6·fields) / 0.7`.

## Agent metrics (agent configs only)

`agent-mini` and `agent-strong` run [`AgentInvoiceExtractor`](../src/InvoiceEvals.Agent/AgentInvoiceExtractor.cs): a
Microsoft Agent Framework `ChatClientAgent` with two deterministic tools, at most 6 tool-call rounds, then the same
`InvoiceDto` schema with one extra top-level `warnings` array. Every metric above is computed on the agent's final
answer exactly as for the direct extractor. The answer is the last assistant text that parses as `InvoiceDto`; tool
turns are not scored. When the round limit is hit, the agent is asked for a final answer with tools disabled; if nothing
parses, schema validity is 0 like any unparseable response.

Tools ([`InvoiceTools`](../src/InvoiceEvals.Agent/InvoiceTools.cs)), both pure functions:

| Tool | Returns |
|---|---|
| `validate_totals(subtotal, discount, tax, total)` | `consistent` (subtotal − discount + tax = total within 0.01 inclusive; null if subtotal or total is not printed), `difference`, `reconciledTotal`, `reason`. Reports only. |
| `normalize_currency(text)` | ISO 4217 `code`, or null with a `reason` when the text is ambiguous (`$`, `¥`, `kr`, `Rs`, "dollar") or unknown. |

The prompt ([`prompts/agent.md`](../prompts/agent.md)) is `plain` verbatim plus the tool rules, including: *"Totals are
extracted as printed. Use validate_totals to report an inconsistency in `warnings`, never to change a value."*

### Deterministic ([`AgentBehaviorEvaluator`](../src/InvoiceEvals.Evaluation/AgentBehaviorEvaluator.cs))

Read from the function calls and results in the transcript and the answer's `warnings`.

| Summary column | Per document | Applies to |
|---|---|---|
| `tool_call_count_mean` | number of tool calls | every document |
| `validate_totals_called_rate` | 1 if `validate_totals` was called (expected 100%) | every document |
| `normalize_currency_called_rate_symbol` | 1 if `normalize_currency` was called | text layer shows a currency sign (`$ € £ ¥ ₹`) but no ISO code |
| `normalize_currency_called_rate_code` | 1 if `normalize_currency` was called | text layer shows an ISO code |
| `tool_override_rate` | 1 if the final total differs from the printed total **and** equals a `reconciledTotal` that `validate_totals` reported with `consistent: false` | golden total printed, response parsed, and `validate_totals` reported an inconsistency at least once |
| `warning_precision` | 1 if the golden amounts really do not reconcile | answer has a warning starting with `totals_inconsistent:` |

`tool_override_rate` is the agent counterpart of `recomputed_total_rate`: the conditional rate at which the agent,
told by its own tool that the totals do not add up, replaces the printed total. It is conditioned on the tool's
report, not on the golden amounts, because a misread subtotal can make the tool report an inconsistency the document
does not have. Every rate column has an `_n` column with the number of documents it applies to.

### LLM-judged ([`AgentQualityEvaluator`](../src/InvoiceEvals.Evaluation/AgentQualityEvaluator.cs)), secondary

`ToolCallAccuracyEvaluator` (`tool_call_accuracy`, 0/1) and `TaskAdherenceEvaluator` (`task_adherence`, 1–5) from
`Microsoft.Extensions.AI.Evaluation.Quality`, given the two tool definitions, on `claude-haiku-4-5` through the response
cache. Both are marked experimental in the package. They are reported as separate columns and never enter the
composite or the gate: the deterministic metrics decide.

## Pass/fail interpretation

0/1 metrics pass at 1. `composite` and the line-item metrics pass at ≥ 0.9. `recomputed_total_rate` fails at 1.
Ratings: 1 Exceptional, ≥ 0.9 Good, ≥ 0.7 Average, ≥ 0.5 Poor, below Unacceptable; not applicable is Inconclusive.

No evaluator checks date ordering: FATURA dates are random and the `due-before-invoice` family is printed that way.

## Comparing configurations (`evals compare`)

Paired bootstrap on per-document deltas (candidate − baseline), with documents paired by id across the latest
execution of each config. 10,000 resamples of n documents with replacement, seed 42, SplitMix64 generator (not
`System.Random`, whose sequence is not guaranteed across runtimes). The 95% interval is the percentile interval
(nearest rank of 2.5% and 97.5% of the sorted resample means). **Significant** means the interval excludes 0. It is
computed for the composite, schema validity, every field accuracy, both judged names and line-items F1. A metric is
split into FATURA and synthetic rows when the two subsets' mean deltas have opposite signs. Rows with n < 30 are
flagged: with 30 synthetic documents a 0/1 metric moves in steps of 0.033, and the interval is too coarse to conclude
from. Implementation: [`PairedBootstrap`](../src/InvoiceEvals.Evaluation/Statistics.cs).

## Regression gate (`evals gate`)

Compares the latest execution of every config in `evals/results/summary.csv` with `evals/results/baseline.csv`, cell
by cell for the (metric, subset) pairs in [`evals/thresholds.json`](../evals/thresholds.json). A cell fails when it
dropped by more than its absolute `maxDrop`, became n/a, or its config was not run. A config without a baseline is
reported, not failed. Exit 1 on any failure, 2 on a configuration error (unknown column, missing file).
