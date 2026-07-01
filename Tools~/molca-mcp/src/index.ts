#!/usr/bin/env node
/**
 * Molca MCP server.
 *
 * A thin stdio MCP server that proxies tool listing and invocation to the Molca Unity editor
 * bridge (an authenticated loopback HTTP endpoint exposed by the editor). Tools are listed
 * dynamically from the bridge's registry, so any provider added in Unity — including SDK-fork
 * providers — appears here with zero changes to this server.
 *
 * Configuration (environment variables):
 *   MOLCA_MCP_PORT   Loopback port the editor bridge listens on (default: 7777).
 *   MOLCA_MCP_TOKEN  Per-session auth token, copied from the Molca > MCP settings tab (required).
 *   MOLCA_MCP_HOST   Bridge host (default: 127.0.0.1). Loopback only.
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  ListToolsRequestSchema,
  CallToolRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

const HOST = process.env.MOLCA_MCP_HOST ?? "127.0.0.1";
const PORT = process.env.MOLCA_MCP_PORT ?? "7777";
const TOKEN = process.env.MOLCA_MCP_TOKEN ?? "";
const BASE = `http://${HOST}:${PORT}`;

if (!TOKEN) {
  console.error(
    "[molca-mcp] MOLCA_MCP_TOKEN is not set. Copy the token from Unity > Project Settings > " +
      "Molca > MCP and set it in your MCP client config."
  );
}

interface BridgeTool {
  name: string;
  description: string;
  inputSchema: unknown;
  mode: string;
  kind: string;
}

/** Calls the editor bridge with the auth token, returning the parsed JSON body. */
async function bridgeFetch(path: string, init?: RequestInit): Promise<any> {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      "X-Molca-Token": TOKEN,
      ...(init?.headers ?? {}),
    },
  });

  if (res.status === 401) {
    throw new Error(
      "Unauthorized (401): the MCP token is missing or does not match the editor. " +
        "Re-copy it from the Molca > MCP settings tab."
    );
  }
  if (!res.ok) {
    throw new Error(`Bridge returned HTTP ${res.status} for ${path}.`);
  }
  return res.json();
}

const server = new Server(
  { name: "molca-mcp", version: "0.1.0" },
  { capabilities: { tools: {} } }
);

/**
 * Asks the user to confirm an action via MCP elicitation.
 * Returns true (approved), false (declined/cancelled), or null if the client does not support
 * elicitation — in which case the action cannot be auto-confirmed and must be run from a client/UI
 * that supports it (e.g. the in-editor Molca chat).
 */
async function elicitConfirmation(message: string): Promise<boolean | null> {
  try {
    // Cast to any so we don't hard-depend on a specific @modelcontextprotocol/sdk version's typings;
    // if the method or capability is absent the call throws and we fall through to null.
    const result = await (server as any).elicitInput({
      message,
      requestedSchema: {
        type: "object",
        properties: {
          confirm: { type: "boolean", description: "Run this action?" },
        },
        required: ["confirm"],
      },
    });
    return result?.action === "accept" && result?.content?.confirm === true;
  } catch {
    // Client likely lacks the elicitation capability.
    return null;
  }
}

server.setRequestHandler(ListToolsRequestSchema, async () => {
  try {
    const body = await bridgeFetch("/tools");
    const tools = (body.tools ?? []) as BridgeTool[];
    return {
      tools: tools.map((t) => ({
        name: t.name,
        // Surface the editor-mode requirement in the description so clients know the precondition.
        description: `${t.description} (mode: ${t.mode}, ${t.kind})`,
        inputSchema: t.inputSchema ?? { type: "object", properties: {} },
      })),
    };
  } catch (err) {
    console.error(`[molca-mcp] Failed to list tools: ${(err as Error).message}`);
    return { tools: [] };
  }
});

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;
  try {
    let body = await bridgeFetch("/invoke", {
      method: "POST",
      body: JSON.stringify({ tool: name, arguments: args ?? {} }),
    });

    // Action-tool guardrail: the bridge asks for human confirmation before running a mutating tool.
    // Use MCP elicitation to ask the user, then re-invoke with the one-time confirmation token.
    if (body.requiresConfirmation) {
      const approved = await elicitConfirmation(String(body.summary ?? `Run action '${name}'?`));
      if (approved === null) {
        return {
          isError: true,
          content: [{ type: "text", text:
            "This action needs confirmation, but this client does not support MCP elicitation. " +
            "Run it from a client that supports elicitation, or use the in-editor Molca assistant chat." }],
        };
      }
      if (!approved) {
        return { content: [{ type: "text", text: "Action cancelled — not confirmed by the user." }] };
      }
      body = await bridgeFetch("/invoke", {
        method: "POST",
        body: JSON.stringify({ tool: name, arguments: args ?? {}, confirmationToken: body.confirmationToken }),
      });
    }

    if (body.ok === false) {
      return {
        isError: true,
        content: [{ type: "text", text: String(body.error ?? "Unknown bridge error.") }],
      };
    }

    return {
      content: [{ type: "text", text: JSON.stringify(body.result, null, 2) }],
    };
  } catch (err) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text:
            `Could not reach the Molca editor bridge at ${BASE}. Is Unity open with the MCP ` +
            `bridge enabled? Details: ${(err as Error).message}`,
        },
      ],
    };
  }
});

async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error(`[molca-mcp] Connected. Proxying to ${BASE}.`);
}

main().catch((err) => {
  console.error("[molca-mcp] Fatal:", err);
  process.exit(1);
});
