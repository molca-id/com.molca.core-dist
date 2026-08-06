---
title: ClickUp Integration
category: Tooling
order: 905
---

# ClickUp integration

The ClickUp integration is editor-only. It does two independent jobs:

- **Inbound** — **Hub → Tasks** lists the tasks in one ClickUp folder, and lets you search, group, pin, focus,
  create, and re-status them without leaving Unity.
- **Outbound** — build and release activity is reported to ClickUp, either as a new task in a list or as a
  comment on the task you are currently working on.

Neither runs at play time. All network I/O goes through `EditorHttpClient`, and the API token never leaves the
machine it was entered on.

## Why a personal API token, not OAuth

GitHub authenticates via device flow and Figma via loopback + PKCE, both entirely inside the editor. ClickUp
cannot work that way: its OAuth2 is a plain authorization-code flow that requires a confidential
`client_secret` and supports neither PKCE nor a device flow. A distributable editor tool cannot embed a secret
or host a callback endpoint, so the personal API token is the supported path here.

An OAuth path behind *user-supplied* app credentials — a studio registering its own ClickUp app — remains
possible but is not implemented.

> A stored token lives in `EditorUserSettings` (`ProjectSettings/EditorUserSettings.asset`), which is
> per-machine and git-ignored by Unity's default `.gitignore`. It is obfuscated, **not** encrypted. Treat it
> like any other local dev credential.

## Setup

1. Create a personal token in ClickUp (**Settings → Apps**). The inspector's **Get a token** button opens that
   page. Tokens start with `pk_`; the field warns before spending a request if what you pasted does not.
2. Open the ClickUp provider asset (**Hub → Integrations → ClickUp → Configure**), paste the token, and press
   **Save & Connect**.
3. Pick the target **Workspace → Folder → List**. These cascade: choosing a workspace loads its folders
   (flattened across spaces and shown as `Space / Folder`), and choosing a folder loads its lists. There is no
   manual id entry — the dropdowns author the ids for you.

After connecting, the inspector names the account by **email** and lists the workspaces the token can reach.
Check that readout first when a task list comes back empty: a token that cannot see the workspace you configured
is the most common cause.

### What the two targets are for

| Field | Used by | Purpose |
| --- | --- | --- |
| **Folder** | Hub → Tasks | The set of tasks shown. One project maps to one folder. |
| **List** | Outbound push | Where a new activity task is created. |

They are independent. A project can list tasks without ever pushing, and vice versa.

## Hub → Tasks

Rows show the task name (click to open it in ClickUp), a status-colored dropdown, assignee initials, and badges
for priority, due date, list, and tags. Due dates read relatively — `Today`, `Tomorrow`, `in 3d`, `2d overdue` —
compared by calendar day, so a task due at 23:00 today reads "Today" rather than overdue.

- **Only mine / Include closed** are server-side filters: changing them refetches.
- **Search** and **Group** are client-side, applied to the fetched snapshot, so they cost no requests. Search
  matches name, list, status, tag, and assignee.
- **Status changes** write straight to ClickUp. A failure reverts the dropdown and reports ClickUp's own reason.
  A refresh cannot interrupt a status write — fetches and writes run on separate cancellation scopes.
- Folders with more than 100 tasks are paged through to the end. If a folder is large enough to hit the
  20-page ceiling, that is logged rather than silently truncated.

### Focus vs. pinning

These look similar and are deliberately different:

| | Focus | Pinning |
| --- | --- | --- |
| Count | Exactly one | Any number |
| Meaning | Semantic — *this is what I am working on* | Presentational — *keep this visible* |
| Effect | Build/release activity can comment on it | Row sorts to the top |
| Control | ☆ / ★ on a row | 📌 on a row |
| Shown when filtered out | Yes, in a banner | No |

Keeping them separate is what makes "which task does a build comment on?" have exactly one answer. Both are
stored per-machine in `ClickUpTaskFocus` (via `EditorUserSettings`) and are **never** serialized onto the
provider asset — focus is personal, and a committed field would churn and conflict across a team.

## Outbound: push targets

`Push on Build` and `Push on Release` opt in; **Push Target** decides where the report lands.

| Push target | Behavior |
| --- | --- |
| `NewTaskInList` | Creates a task in the target list for every build. The original behavior, and the serialized zero value — assets authored before this field existed keep it. Complete, but noisy: none of those tasks are work anybody planned. |
| `CommentOnFocusedTask` | Comments on the focused task. Reports **nothing** when no task is focused, and never silently falls back to creating a task. The quietest option. |
| `CommentOnFocusedTaskOrNewTask` | Comments on the focused task when there is one, otherwise creates a task in the list. Use when activity must never be lost. |

Because focus lives outside the asset, a comment mode can be configured but currently inert. The inspector says
so explicitly rather than leaving it ambiguous.

Automated pushes intentionally do **not** require a session-verified connection — the API call validates the
token itself, so a push works in a fresh editor session without anyone pressing Connect.

## Behavior worth knowing

- **Session cache.** The authorized user and the accessible workspace list are stable for a token's lifetime and
  are cached for the editor session. Call `InvalidateSessionCache()` (or use the inspector's ↻) after changing
  something server-side that the cache would hide. The cache is dropped automatically when the token changes.
- **Rate limits.** A personal token is capped near 100 requests/minute. `429` and `503` are retried a bounded
  number of times, honoring `Retry-After` when present and backing off exponentially when it is not.
- **Failure messages** prefer ClickUp's own `err` text and `ECODE` over the HTTP status, so a rejected status
  change says *"Status not found (CAT_014)"* rather than *"400"*.
- **Cancellation is cooperative.** `EditorHttpClient` takes no cancellation token, so an in-flight request always
  runs to completion; a cancelled operation discards its result rather than stopping the call. Tokens are honored
  between retry attempts and between pagination pages.

## MCP tools

| Tool | Kind | Notes |
| --- | --- | --- |
| `molca_clickup_status` | Read-only | Connection, targets, push target and whether it has a destination, focused task. Never returns the token. |
| `molca_clickup_focus` | Read-only | The focused task and the pinned task ids. |
| `molca_clickup_list_tasks` | Read-only | The folder's tasks, paginated, with priority, ISO-8601 due date, tags, assignees, and pinned/focused flags. |
| `molca_clickup_list_workspaces` | Read-only | Workspaces the token can reach. |
| `molca_clickup_set_focus` | Action | Sets or clears the focused task. Local editor state only, but not on Unity's undo stack. |
| `molca_clickup_set_task_status` | Action | Writes to ClickUp; not undoable from Unity. |
| `molca_clickup_create_task` | Action | Writes to ClickUp; not undoable from Unity. |

Every tool goes through the single registered `ClickUpIntegrationProvider`, so the token never crosses MCP.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| "Not configured" | No token stored. |
| "Token saved — not verified" | A token is stored but `ConnectAsync` has not run this session. Automation still works. |
| Empty task list | Check the **Reaches** readout — the token may not see the configured workspace. Or every task is assigned to someone else (turn off **Only mine**) or closed (turn on **Include closed**). |
| "Workspace id … isn't accessible with this token" | The configured workspace is not among the token's workspaces. Re-pick it. |
| A comment push does nothing | `CommentOnFocusedTask` with no focused task reports nothing by design. Focus a task, or switch to the fallback mode. |

## See also

- [Hub](HUB.md) — the shell the Tasks and Integrations sections live in.
- [Core MCP tools](CORE_MCP_TOOLS.md) — the full `molca_*` tool surface.
