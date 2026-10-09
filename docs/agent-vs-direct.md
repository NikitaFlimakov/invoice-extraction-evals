# Does an agent help for invoice extraction?

This is the protocol for the question. Neither agent configuration has been run yet, so there are no numbers here.
The answer is written from the files listed under "Producing the answer", including "it does not help" if that is
what they show.

## Setup

Same model, same prompt rules, same `InvoiceDto` schema, same 180 documents (150 FATURA, 30 synthetic). The only
differences are the agent loop (Microsoft Agent Framework `ChatClientAgent`, at most 6 tool-call rounds) and two
deterministic tools, `validate_totals` and `normalize_currency`. The agent's answer adds a `warnings` array, which the
scorers ignore. Definitions: [metrics.md](metrics.md#agent-metrics-agent-configs-only).

| Pair | Direct | Agent |
|---|---|---|
| mini | `mini-plain` (`claude-haiku-4-5`, temperature 0) | `agent-mini` |
| strong | `strong-plain` (`claude-sonnet-5-5`, thinking `between_tools`) | `agent-strong` |

## What decides the answer

1. **Accuracy.** Δ composite (agent − direct) from the paired bootstrap, and the same for invoice number, total,
   judged vendor name and line-items F1 (synthetic, n = 30, flagged). An agent "helps" on accuracy only where the 95%
   interval excludes 0.
2. **Faithfulness.** `recomputed_total_rate` on FATURA for both extractors, and `tool_override_rate` for the agent:
   once `validate_totals` reports that the printed amounts do not add up, how often does the agent replace the
   printed total anyway? A gain in total accuracy that comes with overrides is not a gain.
3. **Tool use.** `validate_totals_called_rate` (expected 100%), `normalize_currency` call rates on documents with a
   currency sign vs an ISO code, and `warning_precision`. Tool-call accuracy and task adherence (LLM-judged) are
   secondary and never decide the verdict.
4. **Cost and latency.** Tokens per document, USD per 1k documents and p50 / p95 latency. The dry-run estimates the
   agent at about 4.5× the direct extractor's cost on both models; the measured ratio replaces that estimate.

The verdict weighs 1 and 2 first: an agent that is more accurate only by overriding printed totals fails the
"as printed" rule ([design decision 3](design-decisions.md#3-ground-truth-is-what-is-printed)).

## Producing the answer

```sh
evals run --config mini-plain && evals run --config agent-mini
evals run --config strong-plain && evals run --config agent-strong
evals report        # evals/results/summary.csv: accuracy, agent columns, tokens, cost, latency per config
evals compare --a mini-plain --b agent-mini      # also part of evals/results/comparisons.md
evals compare --a strong-plain --b agent-strong  # evals/results/comparisons-strong.md
```

The `--markdown` files come from the full sequence in the README ("Producing results"). Read the agent columns of
`summary.csv` (each rate has an `_n` column with the documents it applies to) and the two comparisons, then replace
this section with the table, the findings and the verdict.
