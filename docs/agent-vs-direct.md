# Does an agent help for invoice extraction?

> **Pending.** `agent-mini` and `agent-strong` have not been run yet. Every cell below is filled from
> `evals/results/summary.csv` and [`comparisons.md`](../evals/results/comparisons.md) after the runs. The verdict is
> written from those numbers, including "it does not help" if that is what they show.

## Setup

Same model, same prompt rules, same `InvoiceDto` schema, same 180 documents (150 FATURA, 30 synthetic). The only
differences are the agent loop (Microsoft Agent Framework `ChatClientAgent`, at most 6 tool-call rounds) and two
deterministic tools, `validate_totals` and `normalize_currency`. The agent's answer adds a `warnings` array, which the
scorers ignore. Definitions: [metrics.md](metrics.md#agent-metrics-agent-configs-only).

| Pair | Direct | Agent |
|---|---|---|
| mini | `mini-plain` (Haiku 4.5, temperature 0) | `agent-mini` |
| strong | `strong-plain` (Sonnet 5.5, thinking `between_tools`) | `agent-strong` |

## Results

| Metric (combined unless noted) | `mini-plain` | `agent-mini` | `strong-plain` | `agent-strong` |
|---|---|---|---|---|
| Composite | pending | pending | pending | pending |
| Invoice number | pending | pending | pending | pending |
| Total | pending | pending | pending | pending |
| Vendor name (judged) | pending | pending | pending | pending |
| Line-items F1 (synthetic, n=30) | pending | pending | pending | pending |
| `recomputed_total_rate` (FATURA) | pending | pending | pending | pending |
| `tool_override_rate` | n/a | pending | n/a | pending |
| Tool-call accuracy (LLM, secondary) | n/a | pending | n/a | pending |
| Task adherence 1–5 (LLM, secondary) | n/a | pending | n/a | pending |
| Tokens per document (in / out) | pending | pending | pending | pending |
| USD per 1k documents | pending | pending | pending | pending |
| Latency p50 / p95 ms | pending | pending | pending | pending |

Paired bootstrap, Δ composite (agent − direct): see `mini-plain → agent-mini` and `strong-plain → agent-strong` in
[comparisons.md](../evals/results/comparisons.md). Pending.

## Findings

Pending.

## Verdict

Pending.
