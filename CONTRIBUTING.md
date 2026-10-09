# Contributing

Requires the .NET 10 SDK. `dotnet build` treats warnings as errors; `dotnet test` must stay green and offline.
Conventions are in [CLAUDE.md](CLAUDE.md). One logical change per commit, imperative subject line.

## Add a configuration

1. Add an entry to [`evals/configs.json`](evals/configs.json): `name`, `model`, `prompt` (a file in `prompts/`),
   `inputMode` (`text` or `ocr`), `temperature` (null for models that reject it), optionally `disableThinking`, and
   `extractor` (`direct`, the default, or `agent`).
2. If the model is new, add its price to [`evals/pricing.json`](evals/pricing.json).
3. Add the config to `ReportCommand.ConfigOrder` so tables list it in a stable place.
4. Estimate first: `evals run --config <name> --dry-run`.
5. Run it with `EVALS_API_KEY` set: `evals run --config <name>`. Every model response lands in `evals/cache/`.
6. Re-commit the cache as below. Until then CI skips the config ("evals/cache has no entries for it"), so a config
   without a committed cache is not gated.

## Re-commit the cache

A changed prompt, model, judge prompt, tool definition, config or input text is a cache miss by design, and CI fails
the PR with the command to run. Re-run every affected config online, then:

```sh
evals run --config <name> --offline   # must report 0 network calls and 0 cache misses
evals report                          # rewrites evals/results/ and docs/
git add evals/cache evals/results docs
git commit -m "Refresh response cache for <name>"
```

Commit the cache, the results and the docs in the same PR as the change that caused the miss. `evals/results/store/`
is gitignored; CI rebuilds it from the cache. If the numbers moved, move the baseline in the same PR (below).

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

`evals gate` compares against `evals/results/baseline.csv`. Only `evals gate --update-baseline` writes it, in a PR
whose description explains why the numbers moved. No baseline is committed yet; the CI gate is inactive until the
first one is. v1.0.0 is tagged only after that.
