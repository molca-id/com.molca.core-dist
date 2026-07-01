# Assistant Text Tool Protocol

Sprint 69 adds an alternate tool-call transport for weak or local models that do not reliably use
structured function calling. It is controlled by **Tool Call Transport** on the Assistant Settings asset
and in Hub > Assistant.

## Modes

| Mode | Behavior |
|---|---|
| **Auto** | Uses **Text** for `Local` and **FunctionCalling** for cloud providers. |
| **FunctionCalling** | Sends provider-native structured `tool_calls` plus tool-role results. This is the cloud default. |
| **Text** | Sends no structured `Tools` array. Tool specs and grammar are written into the system prompt; tool results return as normal user text. |

## XML call grammar

In Text mode the model calls one tool by writing a single XML block in its assistant message:

```xml
<tool_name>
  <param_name>value</param_name>
</tool_name>
```

Rules:

- Use one tool per assistant message.
- Use exact tool and parameter names from the prompt-rendered tool list.
- For object or array parameters, put compact JSON inside the parameter tag.
- For no-argument tools, write `<tool_name></tool_name>`.
- The assistant harness parses the XML, strips it from the visible transcript, executes through the same
  `AssistantToolBridge.ExecuteAsync` path as structured calls, then sends a user-role result such as:

```text
[tool: molca_status] result:
{"ok":true}
```

## When to use it

Use **Auto** unless you are diagnosing transport behavior. Auto gives local models the text protocol and
keeps cloud providers on the proven structured path. Force **Text** when a local/OpenAI-compatible runtime
can write coherent XML calls but drops or ignores structured `tool_calls`. Force **FunctionCalling** when
you need to compare the old path against Text mode on the same local model.

Text mode still uses the existing guardrails: action allowlists and confirmation, plan mode, undo capture,
loop breaking, result-size caps, proactive retrieval, compaction, and streaming. Multi-tool batching is
intentionally out of scope; the parser executes only the first complete known XML call in a message.

## Tool exposure

Text mode renders the flat tool set into the system prompt. Read-only tools are always listed. Action tools
appear only when allowlisted, just like structured flat exposure. The structured `Tools` array is empty, so
providers do not see function schemas or emit provider-native tool-role messages.

The controller also adds a short request-local shortlist to the current user message. It ranks every
available tool by the user's wording, exact tool-name mentions, the tool name/description, and schema
parameter names, then includes the top matches with compact parameter specs and generated XML templates.
That shortlist is sent only to the model for the current request and is not persisted in chat history.

This is the scalable part of the local-model harness: new Core or fork MCP tools participate automatically
as long as their name, description, and schema are descriptive. The prompt still carries a small common
Unity-routes section, but the per-turn shortlist is what keeps the most relevant available tools close to
the user's latest words. Delete/remove requests also get a generic guardrail: if a delete/remove tool ranks,
selection/navigation tools are not treated as a substitute for deletion.
