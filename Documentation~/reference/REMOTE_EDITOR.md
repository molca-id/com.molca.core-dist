---
title: Molca Remote Editor
category: Tooling
order: 85
---

# Molca Remote Editor

Molca Remote connects a running Unity Editor to the Molca dashboard using an outbound encrypted connection.
It does not expose Unity, the local MCP listener, or the local MCP token to the network.

## Enable or revoke access

1. Sign in to Molca Hub and connect the repository to a Molca project.
2. Open **Hub → Project → Remote Editor**.
3. Enable **Remote access**. Enable **Allow remote Assistant** separately if needed.
4. To permit mutations, enable **Allow remote actions** and acknowledge its warning.
5. Open the Remote dashboard from the same card.

Turning Remote access off closes the connection. The dashboard can also disconnect a process session or
revoke the Editor installation. Access is private to the signed-in user and is rechecked against current
license, project membership, project, and binding state.

## Shared information

The presence snapshot is limited to Editor/Core versions, edit or play mode, compilation health, Molca
project identity, active-scene summary, selected-object display metadata, bounded console counts, and
Assistant activity. Detailed state is requested through allowlisted read-only tools.

Remote Assistant observation contains a bounded transcript and pending ordinary questions. Provider keys,
the local MCP token, source files, asset contents, environment variables, absolute home paths, and raw tool
arguments/results are not included.

## Execution policy

Remote read-only tools use the same registry and Unity main-thread dispatcher as local MCP tools. Remote
Assistant turns use the same controller as the Hub, so switching Hub tabs or closing the dashboard does not
cancel accepted work. Use **Stop** in either surface to cancel the shared turn.

Remote actions require all of the following:

- the server Remote Actions feature is enabled;
- the dashboard actor currently owns the session and has project action access;
- **Allow remote actions** is enabled in this Editor;
- the tool is on the local MCP action allowlist;
- the Editor mode and the confirmed scene/selection context still match;
- no Assistant turn is concurrently using the Editor action lane.

A direct dashboard action is created in a waiting state and is queued only after confirming its immutable
command ID, tool, arguments, target Editor, and context. Reversible results expose their file-snapshot ID or
Unity Undo group. Irreversible actions are supported and clearly labelled.

Remote Assistant never changes the configured action mode. In **Ask**, **Auto**, or **Plan**, existing local
confirmation rules continue to apply, and action confirmations cannot be answered from the web. In
**AutoAll**, locally allowlisted actions—including irreversible actions—can run without another prompt once
Remote Actions is enabled. Removing authorization stops a remote-origin turn; a transient browser or network
disconnect does not.
