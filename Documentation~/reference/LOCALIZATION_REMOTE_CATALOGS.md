---
title: Remote Localization Catalogs
category: Localization & Audio
order: 701
---

# Remote localization catalogs

`LocalizationRemoteCatalogSettings` enables signed post-ship translation updates while the built-in
Unity StringTables remain the always-available fallback.

Create or assign settings from **Molca Hub > Localization > Remote Catalog**. Configure the project id,
channel, and server public RSA verification key. Leave the manifest URL blank to use the licensed
server endpoint. HTTPS is required except for loopback development.

Use **Sync Allowlist** whenever stable catalog identities or source-locale placeholder contracts change,
then run **Production Preflight**. The allowlist deliberately prevents a server payload from inventing
UI identities or changing Smart String arguments. Exporting a publication bundle does not publish it;
an authorized project owner or manager submits the file to the server, where it becomes an immutable,
signed, audited version.

At runtime `LocalizationManager`:

- loads and reverifies an optional project/channel-scoped last-known-good snapshot;
- checks the signed manifest before downloading;
- bounds bytes, entries, and locales;
- validates project, channel, app range, hash, identities, and placeholders;
- activates the candidate atomically and refreshes bindings;
- retains the prior in-memory snapshot for rollback.

Use `LocalizationManager.OverlayStatus`, `ActiveOverlay`,
`RefreshRemoteCatalogAsync(cancellationToken)`, and `RollbackRemoteCatalog()` for diagnostics and
explicit controls. A rejected or interrupted update never clears the active text.

MCP exposes `molca_localization_remote_status` and the guarded
`molca_localization_sync_remote_allowlist` repair action.
