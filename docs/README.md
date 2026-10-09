# Documentation

| Document | What it answers |
|---|---|
| [metrics.md](metrics.md) | How every score is computed: schema validity, field accuracy, judged names, line items, composite, agent metrics, bootstrap, gate |
| [annotation-guidelines.md](annotation-guidelines.md) | How FATURA labels become ground truth, normalization rules, known limitations of the dataset |
| [design-decisions.md](design-decisions.md) | Why the harness is built this way: sampling, held-out layouts, "as printed", nulls, judge, cache, tools, gate |
| [agent-vs-direct.md](agent-vs-direct.md) | The protocol for "does an Agent Framework extractor with tools beat one structured-output call?" |
| [Judge prompt](../evals/judge/judge_prompt.md) | The vendor/customer-name judge's instructions, versioned in the header |

Generated once results are produced (see the README, "Producing results"); none of them exist yet:

| File | Written by |
|---|---|
| `evals/results/summary.csv`, `summary.md`, `<execution>.csv` | `evals report` |
| `docs/results-by-layout.md`, `docs/report/index.html` (published to GitHub Pages) | `evals report` |
| `evals/results/comparisons.md`, `comparisons-strong.md` | `evals compare --markdown` |
| `evals/judge/calibration_pairs.jsonl`, `calibration_report_v<version>.md` | `evals judge pairs`, `evals judge calibrate` |
| `evals/results/baseline.csv` | `evals gate --update-baseline` |
