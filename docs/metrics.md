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
| vendor / customer name | equal after normalization: lower-case, `&` → `and`, punctuation removed, trailing legal-form suffixes removed (`Inc`, `LLC`, `Ltd`, `GmbH & Co. KG`, `S.à r.l.`, `K.K.`, ...). Abbreviations (`Intl.` vs `International`) are **not** equated; that is the Phase 3 LLM judge's job (extension point: the `vendorNameMatch` constructor argument). Diacritics are kept. |

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

## Pass/fail interpretation

0/1 metrics pass at 1. `composite` and the line-item metrics pass at ≥ 0.9. `recomputed_total_rate` fails at 1.
Ratings: 1 Exceptional, ≥ 0.9 Good, ≥ 0.7 Average, ≥ 0.5 Poor, below Unacceptable; not applicable is Inconclusive.

No evaluator checks date ordering: FATURA dates are random and the `due-before-invoice` family is printed that way.
