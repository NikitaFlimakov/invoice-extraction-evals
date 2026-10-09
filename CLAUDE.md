# CLAUDE.md

## Commands

- Build: `dotnet build` (warnings are errors)
- Test: `dotnet test` (xUnit v3 on Microsoft.Testing.Platform, configured in `global.json`)
- Rebuild golden set: `dotnet run --project src/InvoiceEvals.Cli -- download [--count 150] [--seed 42]`
  - Archive is cached in `.cache/fatura/` (gitignored, ~690 MB). Output must be byte-identical across runs.

## Layout

- `src/InvoiceEvals.Core`: `InvoiceDto`, `GoldenDocument`, `GoldenSet` (JSONL I/O), `Fatura/FaturaConverter`, `StratifiedSampler`
- `src/InvoiceEvals.Cli`: System.CommandLine entry point, assembly name `evals`
- `src/InvoiceEvals.Extraction`: `ChatInvoiceExtractor`, `TracingChatClient` (GenAI spans, above the cache), `OfflineChatClient` (below the cache, `--offline`)
- `src/InvoiceEvals.Agent`: `AgentInvoiceExtractor` (Agent Framework `ChatClientAgent`, ≤ 6 tool rounds), `InvoiceTools` (deterministic; their fingerprint is a caching key because the cache key ignores `ChatOptions.Tools`)
- `src/InvoiceEvals.Evaluation`: evaluators (incl. `AgentBehaviorEvaluator`, `AgentQualityEvaluator`), `NameJudge` (gray-zone names), `CalibrationPairs`, `PairedBootstrap`/`CohenKappa`/`SplitMix64`
- `evals/judge/`: judge prompt (versioned header), calibration pairs (hand-labelled), `calibration_report_v<version>.md`. Revise the judge prompt on the calibration set at most once.
- `evals/thresholds.json` + `evals/results/baseline.csv`: `evals gate`. Only `evals gate --update-baseline` writes the baseline, in a PR that explains it.
- Reporting's `ScenarioRun` swallows evaluator exceptions into value-less metrics; check `CompositeScoreEvaluator.FailureOf` after `EvaluateAsync`.
- `evals/annotations.jsonl`: ground truth, one `GoldenDocument` per line, sorted by id. Generated; never hand-edit FATURA rows.
- `docs/annotation-guidelines.md`: normalization rules. **Change it together with `FaturaConverter`.**

## Conventions

- .NET 10 / C# 14. Primary constructors, records for immutable data, file-scoped namespaces.
- `internal sealed` by default; `public` only for types used across assemblies.
- `DateOnly` for calendar dates, `DateTimeOffset` for instants, `decimal` for money.
- Async APIs take a `CancellationToken`.
- Package versions live in `Directory.Packages.props` only. Package source is pinned to nuget.org in `nuget.config`.
- No speculative abstractions: one extractor interface, no plugin system.
- Determinism: order by `StringComparer.Ordinal`, write `\n` line endings, never use `System.Random` for sampling.
- Ground truth is what is printed on the document, not what is arithmetically correct (FATURA totals don't reconcile).
- Test names use `Subject_Condition_Result` (CA1707 is suppressed in the test project).
- Commit messages: imperative, short, one logical step per commit.
- Never write a number this code did not produce. Until a run is committed, the README describes how to produce
  results ("Producing results"); it has no placeholder tables.
