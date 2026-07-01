# molca-mcp

An [MCP](https://modelcontextprotocol.io) server that bridges MCP clients (Claude Code, Cursor, …)
to a running **Molca Unity editor**. It is a thin stdio proxy: it forwards `tools/list` and
`tools/call` to the editor's authenticated loopback bridge and returns the results. Tools are
discovered dynamically, so any provider you add in Unity — including SDK-fork providers — shows up
automatically with no change to this server.

```
MCP client (Claude Code / Cursor)
        │  stdio (MCP)
   molca-mcp (this server)
        │  HTTP + X-Molca-Token  →  127.0.0.1:<port>
   Molca editor bridge (HttpListener, main-thread dispatch)
        │
   Molca tool registry  →  McpToolProvider assets
```

## Prerequisites

- Node.js ≥ 18 (uses the built-in global `fetch`).
- Unity open with the Molca framework, the **MCP bridge enabled**, and at least one tool provider
  configured.

## Enable the bridge in Unity

1. **Project Settings → Molca → MCP**.
2. Click **Create MCP Settings** if no asset is assigned.
3. Add the **Core MCP Provider** asset (Create → Molca → Editor → MCP → Core Provider) to the
   provider list. Its `molca_status` tool is the walking-skeleton tool.
4. Tick **Enable Bridge**, set a **Port** (default `7777`), and **Copy** the **Auth Token**.

The token is a per-project secret stored in your local `EditorPrefs` — it is never written to an
asset and never committed.

## Build

This proxy ships **inside** the Core package at `Packages/com.molca.core/Tools~/molca-mcp` (a `~`
folder Unity doesn't import). Because an installed package is read-only, build it via Unity:

**Project Settings → Molca → MCP → Set Up Proxy (npm install + build).**

That copies the source to a writable, project-local folder — `<project>/molca-mcp/` — runs
`npm install && npm run build` there, and streams the log into the settings panel. The built entry
point is therefore `<project>/molca-mcp/dist/index.js`, which is what `.mcp.json` points at.

Requires Node.js ≥ 18 on your PATH. (To build by hand, copy `Tools~/molca-mcp` somewhere writable
first — don't run `npm` inside the read-only package cache.)

## Configure your MCP client

> The repo ships a template at the root: copy `.mcp.json.example` to `.mcp.json` and paste your
> token. `.mcp.json` is git-ignored because it holds the secret token — never commit it.

Pass the port and token via environment variables. **Claude Code** (`.mcp.json` or
`claude mcp add`):

```json
{
  "mcpServers": {
    "molca": {
      "command": "node",
      "args": ["molca-mcp/dist/index.js"],
      "env": {
        "MOLCA_MCP_PORT": "7777",
        "MOLCA_MCP_TOKEN": "<paste the token from the Molca MCP tab>"
      }
    }
  }
}
```

**Cursor** (`~/.cursor/mcp.json` or project `.cursor/mcp.json`) uses the same shape under
`mcpServers`.

## Environment variables

| Variable          | Default     | Description                                              |
| ----------------- | ----------- | -------------------------------------------------------- |
| `MOLCA_MCP_TOKEN` | _(none)_    | **Required.** Auth token from the Molca → MCP tab.       |
| `MOLCA_MCP_PORT`  | `7777`      | Loopback port the editor bridge listens on.              |
| `MOLCA_MCP_HOST`  | `127.0.0.1` | Bridge host. Loopback only — the bridge rejects others.  |

## Verify

With Unity open and the bridge running, your MCP client should list `molca_status`. Calling it
returns the editor/runtime status:

```json
{ "editorOpen": true, "isPlaying": false, "isReady": false, "coreVersion": "x.y.z" }
```

## Troubleshooting

- **`Unauthorized (401)`** — the token doesn't match. Re-copy it from the Molca → MCP tab (it may
  have been regenerated).
- **`Could not reach the Molca editor bridge`** — Unity isn't open, the bridge isn't enabled, or the
  port differs from `MOLCA_MCP_PORT`.
- **Tools list is empty** — no provider is configured, or every provider is disabled/misconfigured.
  Check the status dots in the Molca → MCP tab.
