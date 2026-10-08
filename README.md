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
| Vendor / customer name | strict normalized match, plus a judged variant where an LLM judge decides only the gray zone, calibrated against 60 hand labels (target Cohen's κ ≥ 0.75) | deterministic + LLM judge |
| Cost & latency | $/1k documents, p50/p95 | measured |

Configurations are compared with bootstrap 95% confidence intervals; a difference whose CI includes zero is reported as no difference.

## Results

> **Pending.** The four configurations in [`evals/configs.json`](evals/configs.json) have not yet been run and cached
> in this repository. `evals report` writes the full table to `evals/results/summary.md`. The live HTML report is at
> **[nikitaflimakov.github.io/invoice-extraction-evals](https://nikitaflimakov.github.io/invoice-extraction-evals/)**.

| Configuration | Schema valid | Invoice no. | Total | Vendor (strict) | Vendor (judged) | Composite | $/1k docs | p50 / p95 |
|---|---|---|---|---|---|---|---|---|
| `mini-plain` | pending | pending | pending | pending | pending | pending | pending | pending |
| `mini-fewshot` | pending | pending | pending | pending | pending | pending | pending | pending |
| `mini-plain-ocr` | pending | pending | pending | pending | pending | pending | pending | pending |
| `strong-plain` | pending | pending | pending | pending | pending | pending | pending | pending |

Vendor (judged) differs from strict only where the LLM judge accepted a gray-zone mismatch. In `mini-plain-ocr` the
FATURA OCR layer never contains the vendor name, so its vendor scores measure the dataset, and the judge is not asked.

## Is the difference real?

Paired bootstrap with 10,000 resamples gives a 95% CI on the mean per-document delta. The method is in
[docs/metrics.md](docs/metrics.md#comparing-configurations-evals-compare), and every metric is in
[`evals/results/comparisons.md`](evals/results/comparisons.md).

| Comparison (Δ composite) | n | Mean Δ | 95% CI | Significant |
|---|---|---|---|---|
| `mini-plain` → `mini-fewshot` | pending | pending | pending | pending |
| `mini-plain` → `strong-plain` | pending | pending | pending | pending |
| `mini-plain` → `mini-plain-ocr` | pending | pending | pending | pending |

Interpretation is pending until the first cached runs. Synthetic-only metrics (line-items F1) rest on 30 documents.
Those rows are flagged n < 30 and are not used to draw conclusions.

## How the gate works

Every pull request replays all four configurations from the committed response cache with `evals run --offline`, so
CI needs no API key and costs nothing. A changed prompt, model or judge prompt misses the cache, and the run fails with
a message telling the author to run it locally and commit `evals/cache/`. `evals gate` then compares the fresh summary
with [`evals/results/baseline.csv`](evals/results/), fails the PR if any metric below drops by more than its threshold,
and posts the before/after table as one sticky PR comment. The baseline only moves through `evals gate --update-baseline`,
committed deliberately in a PR that explains why the numbers changed.

| Metric | Subset | Max drop (absolute) |
|---|---|---|
| Schema validity | combined | 0.01 |
| Composite | combined | 0.01 |
| Invoice number accuracy | combined | 0.02 |
| Total accuracy | combined | 0.02 |
| Vendor name (judged) accuracy | combined | 0.03 |
| Line-items F1 | synthetic | 0.03 |

Thresholds live in [`evals/thresholds.json`](evals/thresholds.json) and apply to every configuration.

## Traces and experiments (Langfuse)

Optional. With these variables set, every `evals run` is a Langfuse experiment, including a cached replay, which
costs nothing. Without them it runs unchanged.

```sh
export LANGFUSE_PUBLIC_KEY=pk-lf-...
export LANGFUSE_SECRET_KEY=sk-lf-...
export LANGFUSE_BASE_URL=https://cloud.langfuse.com
dotnet run --project src/InvoiceEvals.Cli -- langfuse sync-dataset   # once: dataset items → evals/langfuse-items.json
dotnet run --project src/InvoiceEvals.Cli -- run --config mini-plain
```

Traces go over OpenTelemetry (OTLP/HTTP) and follow Langfuse's experiments-via-OpenTelemetry spec:

- one trace per document, linked to its dataset item
- model calls (extraction and judge) as GenAI child spans, with token usage and the original latency
- every metric posted as a score on the document's root observation

> Screenshots pending: `docs/images/langfuse-experiment.png`, `docs/images/langfuse-trace.png`.

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

Running and comparing configurations needs `EVALS_API_KEY` unless every response is already cached:

```sh
evals() { dotnet run --project src/InvoiceEvals.Cli -- "$@"; }
evals run --config mini-plain --dry-run     # documents, tokens, cost estimate (extraction + judge upper bound)
evals run --config mini-plain               # extract, judge the gray zone, score; responses cached in evals/cache/
evals run --config mini-plain --offline     # replay from cache only; a miss fails (what CI does)
evals report                                # evals/results/summary.csv, summary.md, docs/report/index.html
evals compare --a mini-plain --b mini-fewshot --b strong-plain --b mini-plain-ocr --markdown evals/results/comparisons.md
evals gate                                  # regression check against evals/results/baseline.csv
evals judge pairs                           # calibration pairs for hand labelling
evals judge calibrate --dry-run             # cost of the calibration run; drop --dry-run to write the κ report
```

`download` caches the archive in `.cache/fatura/`, verifies its SHA-256, and rewrites `evals/golden/fatura/` and
`evals/annotations.jsonl`. Output is byte-identical across runs for the same `--count` and `--seed`.

## Architecture

```
src/InvoiceEvals.Core         InvoiceDto schema, golden set I/O, FATURA converter, stratified sampler
src/InvoiceEvals.Extraction   IChatClient extractor, latency stamping, GenAI tracing, offline client
src/InvoiceEvals.Evaluation   IEvaluators, name judge, calibration pairs, bootstrap and Cohen's κ
src/InvoiceEvals.Cli          evals download | synthesize | run | report | compare | judge | gate | langfuse
tests/InvoiceEvals.Tests      unit tests (offline: fake chat clients and HTTP handlers)
evals/                        golden set, configs, response cache, judge prompt and calibration, thresholds, results
```

```
 eval set ─► extractor ─► TracingChatClient ─► Reporting response cache ─► Anthropic API
 (evals/)       │                              (evals/cache/, committed)    (OfflineChatClient in CI)
                ▼
           evaluators ─► gray-zone names ─► NameJudge ─► same cache
                │
                ├─► result store ─► evals report ─► summary.csv ─► evals gate ─► sticky PR comment
                │                               └─► docs/report/ ─► GitHub Pages
                └─► OpenTelemetry (OTLP/HTTP) + Scores API ─► Langfuse experiment (optional)
```

## Roadmap

1. **Skeleton, schema, golden set** ✅
2. Extractors (plain, few-shot), field-level evaluators, cached Reporting, first results (code ✅, results pending)
3. Calibrated vendor-name judge, bootstrap CIs, Langfuse experiments, CI eval gate (code ✅, numbers pending)
4. Agent Framework extractor with tools, head-to-head comparison

## License

Code: [MIT](LICENSE). FATURA data: CC BY 4.0, see [evals/golden/README.md](evals/golden/README.md) for attribution.
