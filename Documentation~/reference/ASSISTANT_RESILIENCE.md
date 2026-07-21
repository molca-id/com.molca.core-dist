---
title: Assistant Resilience
category: Assistant
order: 1000
---

# Assistant Resilience

The in-editor assistant keeps working when the network hiccups or the model misbehaves. This page
describes what it does on its own and the three settings you can tune (Hub → Assistant, or on the
**Assistant Settings** asset).

## What it handles for you

**Transient network failures are retried.** A rate-limit (HTTP 429), a temporary server error (5xx), or
a dropped connection/timeout is retried automatically with increasing backoff before the turn gives up.
A `Retry-After` hint from the server is respected. Errors that retrying can't fix — a bad API key or an
invalid request (4xx) — fail immediately, and cancelling a turn never triggers a retry. This applies to
every model call a turn makes, including streaming, context compaction, and chat auto-naming.

**Runaway tool loops are stopped.** If the model keeps making the exact same tool call without making
progress, the turn stops with a *Continue* affordance instead of silently burning through its tool
budget. The work done so far is kept — click **Continue** to resume, or rephrase your request.

**One huge tool result can't derail the turn.** A tool result larger than the configured ceiling is
trimmed (with a `…[truncated N chars]` marker) so it doesn't crowd out the rest of the conversation in
the same turn. This is in addition to the automatic context compaction that runs between turns.

**A failing read doesn't take down the others.** When the model reads several things at once, one read
failing surfaces as an error for just that item; the rest still return normally.

**Token usage is counted on streaming turns.** Streaming answers still report accurate input/output
token counts in the session usage/cost readout, falling back to an estimate only when the provider
reports none.

## Settings

| Setting | Default | What it does |
|---|---|---|
| **Tool Call Transport** | Auto | Auto uses text/XML tools for `Local` models and structured function-calling for cloud providers. |
| **Retry Max Attempts** | 3 | Maximum tries per model call, including the first. Set to **1** to disable retry. |
| **Loop-Break Threshold** | 4 | How many identical tool calls are allowed in a turn before the loop is stopped. |
| **Max Tool-Result Chars** | 100,000 | Per-result size ceiling; longer results are truncated with a marker. |

Numeric settings are clamped to safe ranges, so an out-of-range value is corrected rather than rejected.

## See also

- [`ASSISTANT_TEXT_TOOL_PROTOCOL.md`](./ASSISTANT_TEXT_TOOL_PROTOCOL.md) - the local-model text/XML tool transport.

- [`CORE_MCP_TOOLS.md`](./CORE_MCP_TOOLS.md) — the tools the assistant can call.
- [`MCP_FORK_PROVIDERS.md`](./MCP_FORK_PROVIDERS.md) — adding your own tools for the assistant to use.
