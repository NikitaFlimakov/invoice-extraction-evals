# Documentation

| Document | What it answers |
|---|---|
| [metrics.md](metrics.md) | How every score is computed: schema validity, field accuracy, judged names, line items, composite, agent metrics, bootstrap, gate |
| [annotation-guidelines.md](annotation-guidelines.md) | How FATURA labels become ground truth, normalization rules, known limitations of the dataset |
| [comparisons.md](../evals/results/comparisons.md) | Is the difference between two configurations real? Paired bootstrap per metric |
| [agent-vs-direct.md](agent-vs-direct.md) | Does an Agent Framework extractor with tools beat one structured-output call, and at what cost? |
| [results-by-layout.md](results-by-layout.md) | Composite per FATURA layout and synthetic family, per configuration (written by `evals report`) |
| [Judge calibration report](../evals/judge/) | Cohen's κ of the vendor-name judge against hand labels (`calibration_report_v<version>.md`) |
| [Live report](https://nikitaflimakov.github.io/invoice-extraction-evals/) | The aieval HTML report of the latest runs, published from `docs/report/` |
