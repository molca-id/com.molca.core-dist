# In-Window Model & Provider Switcher (Sprint 71)

The assistant chat window has a **provider + model picker** at the top of the composer, so you can switch
backends without leaving the chat or hand-editing the settings asset. It's built for the way the backend
actually gets changed in practice — cloud for tool-heavy authoring, local for chat, trying different
Ollama tags.

## The picker

A compact row: **provider dropdown · editable model field · `▾` model menu · Detect (Local only) · hint**.

- **Provider dropdown** — OpenAI / Compatible, Anthropic, or Local (Ollama). Switching defaults the model
  field to that provider's default; pick or type another immediately.
- **Model field** — free text. Type any model id (a pulled Ollama tag, a cloud model name, a fine-tune).
  Persists on Enter/blur.
- **`▾` menu** — lists discovered/known models; picking one fills the field.
- **Detect** (Local only) — pings the endpoint and lists its pulled models.
- **Hint** — reachability / status, or a Misconfigured warning.

## Provider-aware model discovery

- **Local / Ollama** — queries the running endpoint: `GET {root}/api/tags` first (the `/v1` suffix is
  stripped for Ollama's root API), degrading to the OpenAI-compatible `GET {baseUrl}/models`. When the
  endpoint is down or has no models pulled, the list is empty and the hint tells you what to do
  (`ollama serve`, `ollama pull …`) — never a silent failure. Results are briefly cached per (provider,
  base URL); **Detect** forces a refresh.
- **Cloud (OpenAI / DeepSeek / Anthropic)** — a curated, extensible known-model list
  (`AssistantModelCatalog.CuratedModelsFor`), plus the always-present free-text field for anything
  unlisted. No network call, no single hardcoded model.

Parsing is factored into pure static methods (`ParseOllamaTags`, `ParseOpenAiModels`) so it's unit-tested
without a live server.

## Apply, persist, and safety

- **One source of truth.** A selection writes `provider`/`model` onto `AssistantSettings` through the same
  `SerializedObject` path the Hub uses (`AssistantModelCatalog.ApplySelection`). Because it re-serializes
  the fields, `ToolCallTransport.Auto` and `ToolExposureMode.Auto` re-resolve (e.g. switching to Local
  turns on the text/XML tool protocol + flat exposure), and the change survives restarts. There's no
  second, session-only config source.
- **Applies to the live controller immediately.** The controller builds its provider from settings on each
  turn, so the next turn uses the new backend — no chat restart.
- **Safe mid-session switch.** The picker is disabled while a turn is in flight, so a switch can't corrupt
  in-flight history; it applies on the next turn.
- **No new secrets path.** API keys stay in `AssistantApiAuth` (EditorPrefs / env var). The picker never
  surfaces, enters, or stores a key — a keyless cloud provider simply shows **Misconfigured** with a
  pointer to the Hub key row. A local runtime is keyless and never flagged for a missing key.

## Extending the curated cloud list

Add entries to `AssistantModelCatalog.CuratedModelsFor`. The list is intentionally non-exhaustive — the
free-text field covers anything not listed, so this is only about surfacing convenient defaults.

## Non-goals

Pulling/deleting models from the UI, managing the Ollama process, and any change to how keys are stored.
