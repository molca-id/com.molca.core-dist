# Assistant Prompt Caching (Sprint 74)

Prompt caching reuses the **stable prefix** of a request — the system prompt plus the tool specs — across
the many model rounds of a single turn (and across turns in a session), so it is re-sent as a discounted
**cache read** instead of full-price input. On a multi-round tool-use turn, where that prefix repeats on every
round, this is the largest available input-cost reduction.

The feature changes **cost, not answer content**. It never alters what the model sees semantically, only how
the vendor bills the repeated prefix.

## What is cached, and what is not

- **Cached (stable prefix):** the system prompt and the tool specifications. These do not change within a turn
  (and rarely between turns), so they are the profitable thing to cache.
- **Not cached (volatile tail):** the growing conversation — user turns, assistant turns, and tool results.
  These change every round, so caching them would only churn the cache.

## Per-vendor behavior

| Provider | Mechanism | Markers | Cache write billed? |
|---|---|---|---|
| **Anthropic** | Explicit `cache_control: { type: ephemeral }` breakpoints on the system block and the last tool | Yes, emitted by `AnthropicLlmProvider` | Yes (~1.25× input for the 5-minute cache) |
| **OpenAI / DeepSeek** | Automatic prefix caching past the vendor threshold | None — the job is prefix **stability** | No (cache reads only, ~0.5× input) |
| **Local (Ollama)** | — | None (no-op) | — |

Anthropic's cacheable order is `tools → system → messages`, and a breakpoint caches everything up to and
including its block. Marking the **last tool** caches the whole tool block; marking **system** caches
tools + system — two breakpoints, well within the 4-breakpoint cap. The message tail is never marked.

## Prefix stability with tiered tool exposure

Tiered exposure (Sprint 67) grows the offered tool set as the model activates tools via `molca_tool_schema`.
Naïvely this would bust the cache every round. It doesn't here because:

- `GetTieredToolSpecs` orders tools in **stable registry order** and only **appends** a newly-activated tool's
  schema — it never reorders.
- A round with **no new activation** re-sends a byte-identical prefix → a full cache hit.
- A **growth** round still matches the unchanged **leading** tools → an incremental (partial) hit, writing
  only the newly-added tail of the tool block.

So once the model has activated the tools it needs (usually in the first round or two), every subsequent round
of the turn is a cache hit. No forced switch to flat exposure is required.

## The setting

`AssistantSettings.PromptCaching` (`Auto` / `On` / `Off`), surfaced in the Hub → Assistant → Advanced → Tool
Use as **Prompt Caching**.

- **Auto** (default): on for cloud providers (Anthropic, OpenAI), off for the keyless Local backend, which
  gains nothing from it.
- **On** / **Off**: force the behavior for any provider.

`AssistantSettings.EnablePromptCaching` resolves the mode against the configured provider; the controller sets
`LlmRequest.CacheStablePrefix` from it on every round's request.

## Cost & telemetry

Cache usage is billed distinctly:

- The non-cached input is billed at the model's normal input rate.
- Cache **reads** are billed at the input rate × `AssistantCostTable.CacheReadMultiplier` (0.1× for Claude,
  0.5× for OpenAI-compatible).
- Cache **writes** (Anthropic only) are billed at the input rate × `AssistantCostTable.CacheWriteMultiplier`
  (1.25× for Claude, 1.0× — i.e. none — elsewhere).

`PromptTokens` on the response is always the **full** prompt size: for Anthropic the provider folds
`cache_read_input_tokens` and `cache_creation_input_tokens` into it (the API reports them separately from
`input_tokens`); for OpenAI `prompt_tokens` already includes the cached subset.

The controller accumulates `SessionCacheReadTokens` / `SessionCacheWriteTokens` (subsets of
`SessionInputTokens`) and exposes `SessionCacheHitRate`. The composer readout shows the cache-hit share next to
the session cost (e.g. `… · 62% cached`). The cache breakdown is **not persisted** across session reloads or
process restarts (a non-goal); a reloaded session bills its restored input at full price until new cached turns
run.

## Non-goals

- Caching across sessions or process restarts.
- Any change to model behavior or answer content.
