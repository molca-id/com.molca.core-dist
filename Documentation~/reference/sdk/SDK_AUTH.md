---
title: Auth
category: Services
order: 110
---

# Auth

The SDK's auth feature is the UI and user-model layer on top of Core's `AuthManager` (see
[Networking](NETWORKING.md)). Core owns the login/token machinery; the SDK provides a ready login
screen and the concrete user types that shape your backend's auth payloads.

## AuthUI

`AuthUI` is a MonoBehaviour you place on your login screen. It wires the standard flows to Core's
`AuthManager` and exposes button entry points:

| Handler | Trigger |
|---|---|
| `OnLoginClicked()` | Username/password login. |
| `OnGuestClicked()` / `OnGuestPanelClicked()` / `OnGuestCancelClicked()` | Guest-session flow. |

Hook these to your uGUI buttons in the Inspector; subclass `AuthUI` to add project-specific fields or
validation.

## User model

The SDK supplies concrete implementations of Core's auth abstractions so login requests and responses
match your backend's JSON:

| Type | Base | Role |
|---|---|---|
| `SDKAuthUser` | `AuthUser` | The authenticated user: overrides `IsGuest`, `Clear()`, `DeserializeFromJson(json)`, `GetLoginJson(username, password)`, and `GetUserId()`. |
| `SDKUserData` | `IAuthUserData` | The user record (`UserId`, `Username`). |

To integrate a different backend, subclass `SDKAuthUser` and override `GetLoginJson` /
`DeserializeFromJson` to match its request/response shape — the rest of the auth flow (token storage,
refresh, `IHttpClient` wiring) stays in Core.

## See also

- [SDK App Flow](SDK_APP_FLOW.md)
- [Networking: HttpClient & Requests](NETWORKING.md)
- [SDK Overview](SDK_OVERVIEW.md)
