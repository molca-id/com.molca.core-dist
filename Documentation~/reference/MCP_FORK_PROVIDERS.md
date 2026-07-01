# Extending Molca MCP from an SDK Fork

The MCP tool surface is built on the same layer model as the rest of the framework
(`architecture.md`): **Core defines the foundation; SDK forks extend by adding providers, never by
modifying Core.** A fork contributes tools by shipping its own `McpToolProvider` asset under its own
namespace. No Core file changes, no asmdef edits to Core, no registry edits.

This is the same extension contract as `SettingModule` (Settings) and `NotificationProvider`
(notifications): subclass an abstract `ScriptableObject`, author an asset, register it in a list.

## The contract

A provider is an editor-only `ScriptableObject` subclass of `Molca.Editor.Mcp.McpToolProvider`:

| Member | Required | Purpose |
|---|---|---|
| `Namespace` | yes | Globally-unique namespace, e.g. `molca.vr`. Two providers may not share one — the registry rejects collisions at load. |
| `Create<Tool>Tool()` factories | yes | One zero-arg method per tool returning an `McpToolDefinition` (unique name, JSON schema, mode, kind, main-thread execute delegate). Discovered automatically — see [How tools are discovered](#how-tools-are-discovered-sprint-34). |
| `GetTools()` | no | Only override to take manual control (conditional tools / custom ordering); otherwise the convention default discovers your factories. |
| `GetStatus()` / `GetStatusMessage()` | optional | Drive the settings-UI status dot (Configured / Disabled / Misconfigured). |

### Rules

- **Namespace your tool names.** Prefix every tool name with your namespace token (e.g.
  `molca_vr_handtracking`) so names never collide with Core or another fork.
- **Secrets never on the asset.** Config (endpoints, model names, enable flags) may be
  `SerializeField`s; API keys/tokens must come from project-scoped `EditorPrefs` or environment
  variables — never a serialized field (the same rule as Core credentials, Sprints 4.5 / 16.2).
- **Execute runs on the main thread.** The bridge marshals every call onto the Unity main thread, so
  your delegate may touch editor and runtime APIs directly. Return a JSON string.
- **Action tools are gated.** Mark mutating tools `McpToolKind.Action`. They run only when added to
  the action allowlist (Project Settings → Molca → MCP) *and* confirmed per invocation (MCP
  elicitation in IDE clients; a modal dialog in the chat). Every action run is recorded in the audit
  log. Read-only tools are exposed immediately and need none of this.
- **Declare reversibility.** Set the `reversibility` argument on an Action tool so the confirmation
  prompt is honest and a revert path exists:
  - `McpToolReversibility.Irreversible` (default) — e.g. a build; cannot be undone.
  - `McpToolReversibility.FileSnapshot` — before editing a file, call
    `McpUndoStack.Snapshot(path, toolName, description)`; the user can then revert via
    `molca_undo_last_action` or the "Revert last MCP action" button.
  - `McpToolReversibility.UnityUndo` — wrap in-memory object mutations in Unity's `Undo` so plain
    Ctrl+Z reverts them.

### Reserved namespaces

To prevent collisions between the shared SDK package and the fork layers that depend on it, the
following namespaces (and their `molca_<token>_…` tool-name prefixes) are reserved. A provider must
not claim one unless it ships in the layer that owns it:

| Namespace | Token prefix | Owner |
|---|---|---|
| `molca` | `molca_…` | Core (`com.molca.core`) — do not extend; add your own. |
| `molca.sdk` | `molca_sdk_…` | Shared SDK (`com.molca.sdk`). **Reserved** — no provider exists yet (no agent-facing authoring surface as of Sprint 67). Claim it only when the shared SDK layer gains a tool. |
| `molca.vr` | `molca_vr_…` | VR fork (`molca-sdk-vr`). |
| `molca.dt` | `molca_dt_…` | Digital-twin fork (`molca-sdk-dt`). |

Project (non-fork) tools should use a project-specific token, not any of the above.

## Where it goes

Place the provider under your SDK layer's **editor** assembly, e.g.
`Assets/_MolcaSDK/<Layer>/Editor/`. That assembly must reference `Molca.Editor` (the
`MolcaSDK.Editor` assembly already does). Then add the authored asset to the **MCP Settings**
provider list (Project Settings → Molca → MCP). The registry discovers it on the next domain reload;
its status dot and tools appear with no Core changes.

> Project (non-SDK) code follows the same pattern but lives under `Assets/YourProject/`.

## How tools are discovered (Sprint 34)

You **do not** write a `GetTools()` method. The base `McpToolProvider.GetTools()` discovers tools by
convention: every **zero-parameter method that returns an `McpToolDefinition`** on your provider type
(public or non-public, static or instance, across all of its partial files) is invoked and its result
registered. By convention each such factory is named `Create<Tool>Tool()`.

Consequences:

- **Add a tool = add a `Create<Tool>Tool()` factory.** No central list to edit, so two people adding
  tools in separate partial files never conflict.
- Results are **sorted by tool name** (deterministic across reloads/machines) and cached per type.
- A **parameterized** helper that returns `McpToolDefinition` (e.g. a shared
  `BuildSomething(string name)`) is *not* a factory — it takes arguments, so discovery skips it. Call
  it from a zero-arg factory.
- You *may* still override `GetTools()` when you need conditional tools or custom ordering — an
  override wins and discovery is not used for that type.

## How the in-editor assistant exposes tools (Sprint 67)

The registry can hold ~180+ tools. The **IDE MCP bridge** still serves the full registry verbatim. The
**in-editor assistant**, however, does **not** send every tool's schema on every request — that was a large
per-request token cost and a too-large decision space. Instead (`AssistantToolBridge` + `AssistantChatController`):

- The system prompt carries a **compact catalog**: one line per family — `[family] (N): name1, name2, …` —
  listing tool **names** grouped by family, *without* per-tool summaries (a trailing `*` marks an action).
- Only two meta-tools are callable up front: **`molca_tool_schema(names[])`** (fetch a tool's full input
  schema on demand) and **`molca_list_tools(family)`** (expand a family to names + summaries). A tool's full
  schema enters the request **only after** the model fetches it — the per-turn "activated" set.
- So the model's flow is: read the catalog → (optionally `molca_list_tools` a family) → `molca_tool_schema`
  the tool(s) → call them. Per-request tool payload drops from all schemas to the meta-tools + what's in use.
- Independent **read-only** calls in a round execute **in parallel**; action tools stay sequential
  (confirmation + undo grouping order preserved).

**Implications for fork tools:** nothing to do — your discovered tools appear in the catalog under their
family (derived from the `molca_<family>_…` / `molca_unity_<family>_…` name) automatically, and the assistant
fetches their schemas on demand. Give tools **self-descriptive names** (the catalog shows names, not
summaries) and a clear first sentence in the description (shown by `molca_list_tools` and `molca_tool_schema`).
A read tool that often reads several items at once can accept an **array** input (batch) to save round-trips —
see `molca_read_source`'s `paths`.

### Text tool transport for local models (Sprint 69)

When Assistant Settings uses **Tool Call Transport = Auto**, the `Local` backend uses a text/XML transport
instead of structured function-calling. Fork providers do not need extra registration for this path: the
assistant renders the same flat tool set into the system prompt, including fork tools from the MCP Settings
provider list. Keep tool names and parameter names XML-tag friendly (letters, digits, underscores) and keep
the first sentence of each description clear, because local models choose tools from that prompt-rendered
list. Action tools still appear only when allowlisted and still run through confirmation/undo guardrails.

## Worked example — a VR fork provider

```csharp
using Molca.Editor.Mcp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MolcaSDK.VR.Editor.Mcp
{
    /// <summary>
    /// Example SDK-fork MCP provider owning the <c>molca.vr</c> namespace. Proves the extension
    /// contract: a fork ships tools with zero Core changes. No GetTools() — the Create*Tool()
    /// factory below is discovered by convention.
    /// </summary>
    [CreateAssetMenu(fileName = "VR MCP Provider", menuName = "MolcaSDK/VR/MCP Provider")]
    public partial class VRMcpToolProvider : McpToolProvider
    {
        public override string Namespace => "molca.vr";

        private static McpToolDefinition CreateVrStatusTool() => new McpToolDefinition(
            name: "molca_vr_status",
            description: "Reports VR-layer status (XR active, rig present).",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteVrStatus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteVrStatus(string argumentsJson)
        {
            // Runs on the main thread — safe to read editor/runtime state here.
            var result = new JObject
            {
                ["xrActive"] = UnityEngine.XR.XRSettings.isDeviceActive,
                ["isPlaying"] = EditorApplication.isPlaying
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
```

Add the `VR MCP Provider` asset to the MCP Settings provider list. Calling `tools/list` from any MCP
client now returns `molca_vr_status` alongside Core's `molca_*` tools — and if two providers ever
claimed `molca.vr`, the registry would report a duplicate-namespace error at load rather than
silently shadowing one.

### Adding a second tool — just drop a partial file

Because the class is `partial` and tools are discovered, a new tool needs **no edit** to the file
above. Add `VRMcpToolProvider.Teleport.cs` beside it:

```csharp
using Molca.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace MolcaSDK.VR.Editor.Mcp
{
    public partial class VRMcpToolProvider
    {
        private static McpToolDefinition CreateVrTeleportTool() => new McpToolDefinition(
            name: "molca_vr_teleport",
            description: "Teleports the rig to a named anchor.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{}}",
            execute: ExecuteVrTeleport,
            mode: McpToolMode.Play,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteVrTeleport(string argumentsJson) =>
            new JObject { ["ok"] = true }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
```

`molca_vr_teleport` appears after the next domain reload — no settings change, no `GetTools()` edit.

### Generating either of these with `molca_create_mcp_tool`

The Core `molca_create_mcp_tool` action tool scaffolds both shapes:

- **New provider** — pass a `providerClassName` that doesn't exist yet (must end in `McpToolProvider`),
  plus `toolNamespace` and `toolName`. It writes the provider stub into your working area; you then
  create the asset and add it to MCP Settings.
- **Extend existing** — pass the name of a provider that already exists *in a writable location*. It
  drops a tool-only partial beside it (the second-tool shape above) — no settings change needed.

Providers that live in a **read-only package** (Core under `Packages/`, an SDK layer under
`Assets/_MolcaSDK/`) cannot be extended in place — the tool refuses and tells you to subclass instead.
That's the layer model: extend Core/SDK by adding your *own* provider, never by editing theirs.

---

## Contributing to the Framework Graph (Sprint 22.8)

The **Framework Graph** (`Molca ▸ Utilities ▸ Framework Graph`, and the read-only
`molca_framework_graph` MCP tool) maps how the loaded project is wired. A fork can add its own
**read-only** nodes and edges without modifying Core by implementing `IFrameworkGraphContributor`.

- **Base/contract:** `Molca.Editor.FrameworkGraph.IFrameworkGraphContributor` — one method,
  `void Contribute(FrameworkGraphSnapshot snapshot)`.
- **Registration:** none. `FrameworkGraphBuilder` discovers every concrete, parameterless implementor
  via `TypeCache` and calls it after the Core layers are built. (Types in `*.Tests` assemblies are
  skipped.)
- **Folder placement:** `Assets/_MolcaSDK/[Layer]/Editor/` (or the fork's editor assembly).

```csharp
using Molca.Editor.FrameworkGraph;

public sealed class VrFrameworkGraphContributor : IFrameworkGraphContributor
{
    public void Contribute(FrameworkGraphSnapshot snapshot)
    {
        // Namespace your ids so they never collide with Core's; use the Fork category.
        var rig = snapshot.AddNode(new FrameworkGraphNode("vr:rig", "XR Rig", FrameworkNodeCategory.Fork)
            .With("tracking", "active"));

        // Edges are only kept when both endpoints exist as nodes (no dangling edges).
        snapshot.AddNode(new FrameworkGraphNode("vr:hand-left", "Left Hand", FrameworkNodeCategory.Fork));
        snapshot.AddEdge(new FrameworkGraphEdge("vr:rig", "vr:hand-left", FrameworkEdgeKind.Contains));
    }
}
```

Rules: read-only only (describe state; never mutate serialized data — that stays on the guarded
action tools); honour the SOs-out boundary (a ScriptableObject may be a config node, never a
runtime-resolvable scene reference target); guard your own reads. The builder also wraps each
contributor in try/catch, so a faulting contributor is reported in the snapshot's `unavailable`
list rather than breaking the graph.

## See also

- [`SEQUENCE_VALIDATION.md`](./SEQUENCE_VALIDATION.md) — the `ISequenceValidator` registry: adding a sequence validator in a fork follows the same TypeCache-discovery, no-registration-line pattern as MCP providers.
