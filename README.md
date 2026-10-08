# invoice-extraction-evals

A reproducible benchmark and evaluation harness for LLM-based structured extraction of invoices, in .NET 10.

## Problem

"The extraction works" usually means someone looked at a few outputs. This repo answers **how do you know?** with
numbers: a versioned golden set, deterministic field-level metrics, a calibrated LLM judge for the one fuzzy field,
confidence intervals when comparing configurations, traces for every run, and a CI gate that fails a PR on regression.

## What it measures

| Metric | Scope | Type |
|---|---|---|
| Schema validity | whole response parses into `InvoiceDto` | deterministic |
| Field accuracy | exact match after normalization: number, dates, currency, amounts, addresses | deterministic |
| Line-items F1 | synthetic edge cases (FATURA has no line-item ground truth) | deterministic |
| Vendor name | semantic match via LLM judge, calibrated against 50 hand labels (Cohen's κ ≥ 0.75) | LLM judge |
| Cost & latency | $/1k documents, p50/p95 | measured |

Configurations are compared with bootstrap 95% confidence intervals; a difference whose CI includes zero is reported as no difference.

## Results

> **Pending.** Extractors and evaluators land in Phase 2. This table will hold real numbers from `evals/results/`.

| Configuration | Schema valid | Field accuracy | Vendor name | $/1k docs | p50 / p95 |
|---|---|---|---|---|---|
| plain prompt | pending | pending | pending | pending | pending |
| few-shot prompt | pending | pending | pending | pending | pending |
| agent + tools | pending | pending | pending | pending | pending |

## Golden set

150 invoices from [FATURA](https://zenodo.org/records/10371464) (CC BY 4.0), 3 per layout across all 50 layouts,
sampled with a fixed seed. Images are in [`evals/golden/`](evals/golden/); ground truth is one JSON line per document
in [`evals/annotations.jsonl`](evals/annotations.jsonl). FATURA labels are converted and normalized per
[docs/annotation-guidelines.md](docs/annotation-guidelines.md).

Fields printed per document (of 150): invoice date 147, currency 138, invoice number 132, total 126, customer 114,
subtotal 102, vendor name 102, due date 87, tax 72, discount 36.

Caveats worth knowing before reading any score: FATURA's amounts do not reconcile, dates are random, it has no
line-item labels, and it only has 34 distinct vendors. See [known limitations](docs/annotation-guidelines.md#known-limitations-of-fatura-as-ground-truth).

## Quickstart

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet test                                            # build + unit tests
dotnet run --project src/InvoiceEvals.Cli -- download  # rebuild the golden set (downloads ~690 MB once)
```

`download` caches the archive in `.cache/fatura/`, verifies its SHA-256, and rewrites `evals/golden/fatura/` and
`evals/annotations.jsonl`. Output is byte-identical across runs for the same `--count` and `--seed`.

## Architecture

```
src/InvoiceEvals.Core         InvoiceDto schema, golden set I/O, FATURA converter, stratified sampler
src/InvoiceEvals.Extraction   IChatClient-based extractors (Phase 2)
src/InvoiceEvals.Evaluation   custom IEvaluators (Phase 2)
src/InvoiceEvals.Cli          `evals download` (later: run, report, compare)
tests/InvoiceEvals.Tests      unit tests
evals/                        golden set and annotations, versioned in git
```

## Roadmap

1. **Skeleton, schema, golden set** ✅
2. Extractors (plain, few-shot), field-level evaluators, cached Reporting, first results
3. Calibrated vendor-name judge, bootstrap CIs, Langfuse experiments, CI eval gate
4. Agent Framework extractor with tools, head-to-head comparison

## License

Code: [MIT](LICENSE). FATURA data: CC BY 4.0, see [evals/golden/README.md](evals/golden/README.md) for attribution.
