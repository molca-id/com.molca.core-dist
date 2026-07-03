# Assistant Web & Documentation Lookup (Sprint 75)

Molca's assistant consumes its **own MCP registry** — a sealed tool ecosystem. Until this sprint it could
not check a current Unity API signature, a package version, a changelog, or any external doc: it answered
from the model's training cut-off or guessed. Knowledge-graph grounding (Sprint 47) covers the **project**;
this covers the **world outside it**.

This sprint adds a first-party, **read-only** web tool family — opt-in, host-allowlisted, and routed through
the hardened networking path — that closes the common need without a full third-party MCP-client stack.

## The tools

| Tool | Kind | What it does |
|---|---|---|
| `molca_web_fetch` | ReadOnly | Fetches a URL and returns its **readable text** (HTML stripped), size-capped and secret-redacted. Host must be allowlisted. |
| `molca_web_search` | ReadOnly | Queries the configured search provider and returns `title` / `url` / `snippet` rows. Degrades cleanly when no provider/key is configured. |

Both are **read-only** — they never mutate project state — but they are **egress-gated** (below). Neither
executes JavaScript, renders pages, or crawls: `molca_web_fetch` retrieves a single URL, `molca_web_search`
returns result rows you then fetch explicitly.

## Egress policy (off by default)

Editor network egress is a policy choice, so the whole family is **disabled by default**. It is controlled by
`AssistantSettings`, surfaced in **Hub → Assistant → Advanced → Web**:

- **Enable Web Tools** (`WebToolsEnabled`, default **off**) — the master switch. While off, both tools return
  an actionable policy result instead of touching the network.
- **Fetch Host Allowlist** (`WebHostAllowlist`) — `molca_web_fetch` may only request a host on this list. An
  entry matches its exact host **and any subdomain** (`unity3d.com` allows `docs.unity3d.com` but not
  `notunity3d.com`). An empty allowlist allows nothing even when web tools are enabled. Ships with a small
  default set (Unity/Microsoft docs, GitHub).
- **Search Provider** (`WebSearchProvider`) — `None` (default), `Brave`, or `Tavily`.
- **Search Max Results** (`WebSearchMaxResults`) — per-query result cap.

The search provider's **subscription key is a secret**: like the LLM key, it lives in `AssistantWebAuth`
(project-scoped `EditorPrefs`, per provider) or an environment variable (`BRAVE_SEARCH_API_KEY` /
`TAVILY_API_KEY`) — **never** on the settings asset. Set it in the same Hub section.

## Transport, size cap, redaction

- **Reuses the hardened path.** Requests go through `AssistantHttp` (Sprints 65/68): a background task pumped
  onto the editor loop (so it works while Play mode is paused), degrading a transport fault to a non-success
  result rather than throwing. `Awaitable`, honoring the async contract.
- **Size cap.** `molca_web_fetch` truncates its text to `AssistantSettings.MaxToolResultChars` (the same
  per-tool-result ceiling as every other tool) and reports `truncated`. Search snippets are individually
  capped and the row count honors `WebSearchMaxResults`.
- **Redaction.** Fetched/searched text is scrubbed of secret-looking tokens (API keys, bearer tokens, GitHub
  PATs, AWS/Slack keys) via `RedactSecrets`, and any URL is query-redacted (`LogRedaction`). Defense-in-depth
  so a page that leaks a credential can't surface it verbatim through a tool result.

## Grounding routing

The base prompt routes **current external facts** (a Unity/C# API signature, a package version, a changelog,
framework docs outside this project) to `molca_web_fetch` of the authoritative docs URL — or
`molca_web_search` to find it first — in preference to training memory, and **combines** it with the KG for
project-specific context (mirroring the Sprint-47 grounding contract). When the web tools are disabled, the
assistant says its answer may be stale and points to the Hub setting rather than guessing.

## Search provider setup

- **Brave** — get a key from the Brave Search API dashboard. Request is a `GET` with the
  `X-Subscription-Token` header.
- **Tavily** — get a key from the Tavily dashboard. Request is a `POST` with `api_key` in the JSON body.

Select the provider in the Hub, paste the key (or set the env var), and `molca_web_search` is live.

## Non-goals / follow-up

- **No JS rendering / headless browsing** and **no crawling** — a single fetch and a search, nothing more.
- **No third-party MCP-client stack.** Consuming external MCP **servers** as a client is a deliberate future
  seam, not this sprint; the two first-party tools cover the 80% need. This is the documented follow-up if
  broader external-tool access is later required.
