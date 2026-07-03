# Assistant Reasoning / Extended Thinking (Sprint 76)

Before this sprint the assistant's reasoning was **explicitly disabled**: `AnthropicLlmProvider` never sent a
`thinking` block, and there was no OpenAI `reasoning_effort`. For hard multi-step authoring and plan-mode
tasks — exactly where answer quality is won or lost — that headroom went unused. This sprint enables a
per-turn reasoning budget, gated per model, without leaking hidden reasoning into the visible transcript and
without breaking the tool-use round loop or streaming.

## The setting

One provider-neutral knob, `ReasoningEffort` on `AssistantSettings`, threaded onto every turn's
`LlmRequest.Reasoning`:

| Level | Meaning |
|---|---|
| `Off` | No extended reasoning. **The shipped default** — lowest cost and latency. |
| `Low` | A small reasoning budget for lightly harder turns. |
| `Medium` | A moderate reasoning budget. |
| `High` | A large reasoning budget for the hardest multi-step / plan-mode turns. |

It is selectable from the in-window model picker (Sprint 71) — a "Reasoning: …" dropdown — and applied through
the same `SerializedObject` path as the provider/model selection, so it persists and re-resolves. Off by
default because reasoning bills as output tokens and adds latency.

## Per-vendor mapping

The neutral level maps to each vendor's native mechanism; a **non-reasoning model and the Local backend ignore
it entirely** (the field is never sent, so nothing 400s):

- **Anthropic** (Claude 3.7+ / 4.x) → `thinking { type: "enabled", budget_tokens: N }`, with the
  `anthropic-beta: interleaved-thinking-2025-05-14` header so thinking can interleave across tool-use turns.
  The budget is scaled by level (Low ≈ 2k, Medium ≈ 8k, High ≈ 16k) and **clamped below `max_tokens`** —
  Anthropic requires `max_tokens > budget_tokens`, and answer headroom is reserved. If `max_tokens` is too
  small to fit a valid budget plus an answer, thinking is skipped for that turn.
- **OpenAI reasoning models** (o-series, GPT-5, `deepseek-reasoner`) → `reasoning_effort`
  (`low`/`medium`/`high`). Plain `gpt-4o`/`gpt-4.1` are **not** reasoning models and never receive the field.
- **Local** → ignored.

Capability is decided by `AssistantModelCatalog.IsReasoningModel(provider, model)` — conservative (unknown →
not reasoning), so a model never receives a field it would reject.

## Reasoning is never the visible answer

Thinking / `redacted_thinking` blocks are consumed for quality but **never rendered as the reply**. The visible
answer stays the model's text block. Reasoning surfaces only as a collapsed **"Thought for ~N tokens"**
disclosure beneath the answer (a redacted-only turn notes that it reasoned but shows no readable text). This is
consistent with the base prompt's standing "do not narrate hidden reasoning" rule.

## Streaming + tool-use stay correct

Reasoning coexists with tool calls. Anthropic requires **thinking blocks to be preserved across tool-use
turns** — echoed back verbatim, including their opaque signature, or the tool-use turn is rejected. So:

- The provider parses thinking blocks (non-streaming and streaming) into `LlmResponse.ThinkingBlocks`.
- The controller stores them on the assistant `LlmMessage`, and they are **persisted** with the session so a
  reloaded conversation can still replay them.
- `AnthropicLlmProvider` re-emits them **first** in the assistant content block (before text and `tool_use`).
- Streamed thinking deltas are accumulated for the block and the "thought" affordance, but **not** forwarded to
  the visible streaming text.

## Cost visibility

Reasoning tokens bill as output. They are counted distinctly in `SessionReasoningTokens` and shown in the
composer's token readout (`… · N reasoning`) so the latency/cost trade-off of a higher level is visible, not
hidden inside the output total. Anthropic doesn't report a separate reasoning count, so it is estimated from
the returned thinking text; OpenAI reports it as `completion_tokens_details.reasoning_tokens`.

## Non-goals

- **Exposing raw chain-of-thought as the answer** — reasoning is only ever a collapsed affordance.
- **Reasoning on models that don't offer it** — the setting is silently ignored there.
