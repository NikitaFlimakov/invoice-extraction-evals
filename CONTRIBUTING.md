# Contributing

Requires the .NET 10 SDK. `dotnet build` treats warnings as errors; `dotnet test` must stay green and offline.
Conventions are in [CLAUDE.md](CLAUDE.md). One logical change per commit, imperative subject line.

## Add a configuration

1. Add an entry to [`evals/configs.json`](evals/configs.json): `name`, `model`, `prompt` (a file in `prompts/`),
   `inputMode` (`text` or `ocr`), `temperature` (null for models that reject it), optionally `disableThinking`, and
   `extractor` (`direct`, the default, or `agent`).
2. If the model is new, add its price to [`evals/pricing.json`](evals/pricing.json).
3. Estimate first: `evals run --config <name> --dry-run`.
4. Run it with `EVALS_API_KEY` set: `evals run --config <name>`. Every model response lands in `evals/cache/`.
5. `evals report`, then commit `evals/cache/`, `evals/results/` and `docs/` together. CI replays the cache with
   `--offline`; a missing response fails the PR with the command to run.
6. Add the config to `ReportCommand.ConfigOrder` so tables list it in a stable place.

A changed prompt, model, judge prompt or tool definition is a cache miss by design: re-run the affected configs and
commit the refreshed cache in the same PR.

## Add an evaluator

1. Implement `IEvaluator` in [`src/InvoiceEvals.Evaluation`](src/InvoiceEvals.Evaluation/). Emit `NumericMetric`s
   (`evals report` aggregates only numeric metrics), use `null` for "not applicable", and put the reason in the metric.
2. Unit-test it in [`tests/InvoiceEvals.Tests`](tests/InvoiceEvals.Tests/) with hand-built responses; no network.
3. Register it in `RunCommand` (all configs, or agent configs only) and add a summary column in `ReportCommand`.
4. Document the definition in [docs/metrics.md](docs/metrics.md).
5. An LLM-based evaluator must take its chat client from the `ChatConfiguration` it is given, so its calls go through
   the response cache and CI can replay them.

Existing cached runs do not contain a new evaluator's scores until they are replayed: `evals run --offline` for each
config re-scores from the cache at no cost (an LLM evaluator's own calls need one online run).

## Move the baseline

`evals gate` compares against [`evals/results/baseline.csv`](evals/results/). Only
`evals gate --update-baseline` writes it, in a PR whose description explains why the numbers moved.
