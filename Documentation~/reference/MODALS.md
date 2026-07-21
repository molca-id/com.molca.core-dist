---
title: Modals
category: UI & Presentation
order: 500
---

# Modals

The modal system is a single `RuntimeSubsystem` — `ModalManager` — that owns every transient
overlay in the app: toast messages, loading indicators, confirmation dialogs, and any custom modal
you author. You never instantiate an overlay yourself; you ask the manager to show one, and it
handles parenting, pooling, tracking, and dismissal.

`ModalManager` lives in the `Molca.Modals` assembly (`Runtime/Modals/`). Reach it the usual way —
`RuntimeManager.GetSubsystem<ModalManager>()` or an `[Inject] private ModalManager _modalMgr;` field
— after awaiting `RuntimeManager.WaitForInitialization()`.

## Overview of what it shows

| Surface | Method(s) | Backed by |
|---|---|---|
| Toast message | `AddMessage(...)` | pooled `ModalMessage`, auto-fades |
| Inline loading | `AddLoading` / `RemoveLoading` | pooled `ModalLoading` |
| Full-screen loading | `ShowFullScreenLoading` / `HideFullScreenLoading` | a persistent panel |
| Confirmation dialog | `ShowRegularConfirmation` / `ShowAdvancedConfirmation` | `ModalConfirmation` |
| Custom / SDK modal | `ShowModal(...)` | any `BaseModal` subclass |

## Toast messages

`AddMessage` queues a self-dismissing toast. Work is deferred one frame internally so you can safely
call it from anywhere, including inside a UI rebuild.

```csharp
_modalMgr.AddMessage("Saved.");
_modalMgr.AddMessage("Low battery.", ModalManager.MessageType.Warning);
_modalMgr.AddMessage("Upload failed.", ModalManager.MessageType.Error, duration: 15f);
```

`MessageType` is `Default`, `Warning`, or `Error`; each maps to a color configured on the manager.
`duration` (default `10f`) is the on-screen lifetime in seconds — the toast fades out over the last
fraction of it and returns itself to the pool.

If the manager's `hookLogger` flag is enabled, it subscribes to `LogManager` and mirrors log
info/warning/error lines as toasts automatically — no per-call code needed.

## Loading indicators

Inline loading indicators are keyed by their title, so you add and remove them by the same string:

```csharp
var loading = _modalMgr.AddLoading("Downloading pack");
loading.Refresh("Downloading pack (42%)", 0.42f);   // message + 0..1 progress
// ...later
_modalMgr.RemoveLoading("Downloading pack");
```

`AddLoading` returns the pooled `ModalLoading` so you can drive its progress bar via `Refresh(msg,
progress)`; the bar lerps smoothly toward the target fill. Adding a title that is already active logs
a warning and returns the existing indicator.

For a blocking full-screen overlay (e.g. during bootstrap or a scene switch), use the panel pair:

```csharp
_modalMgr.ShowFullScreenLoading("Preparing system...");
// ...
_modalMgr.HideFullScreenLoading();
```

## Confirmation dialogs

Two convenience methods cover the common yes/no dialog without authoring a modal. Both return the
`ModalConfirmation` instance so you can further tweak its buttons via `YesButton` / `NoButton`.

```csharp
_modalMgr.ShowRegularConfirmation(
    title:   "Delete file?",
    message: "This cannot be undone.",
    yesText: "Delete",
    noText:  "Cancel",
    onYes:   () => DeleteFile(),
    onNo:    null);
```

`ShowAdvancedConfirmation` adds `subtitle` and scrollable `details` fields for richer dialogs. Pass
`showNoButton: false` for an acknowledge-only dialog. Clicking either button closes the dialog first,
then invokes your callback.

For a no-code, inspector-driven path, drop a `ModalConfirmationHelper` MonoBehaviour on a scene
object: fill in its localized `ConfirmationData` and wire `confirmCallback` / `cancelCallback` as
`UnityEvent`s, then call `Create()` (e.g. from a button `onClick`).

## Custom modals

### Authoring a `BaseModal`

Any bespoke overlay subclasses `BaseModal` (`abstract MonoBehaviour`, `Molca.Modals`). Place your
subclass in your working area — for a project, `Assets/YourProject/Scripts/Modals/`.

```csharp
// Assets/YourProject/Scripts/Modals/RewardModal.cs
using Molca.Modals;
using UnityEngine;

/// <summary>Celebration overlay shown when the user earns a reward.</summary>
public class RewardModal : BaseModal
{
    [SerializeField] private TMPro.TextMeshProUGUI _rewardLabel;

    /// <summary>Populates the reward text before the modal is shown.</summary>
    public void Setup(string reward) => _rewardLabel.text = reward;

    public override void Open(bool showNoButton = true)
    {
        base.Open();            // activates the GameObject
        // ...add an entrance animation here.
    }

    public override void Close()
    {
        // ...play an exit animation, then:
        base.Close();           // untracks with ModalManager, then Destroys
    }
}
```

`BaseModal` gives you three virtual hooks:

| Member | Purpose |
|---|---|
| `Open(bool showNoButton = true)` | Make the modal visible; override to add entrance animation. |
| `Close()` | Untrack from `ModalManager` and destroy. Override to animate, but **always call `base.Close()` last**. |
| `SetNoButtonVisible(bool)` | No-op by default; override if your modal has a dismiss button. |

`BaseModal.OnDestroy` also untracks the modal if it is destroyed without going through `Close()`
(scene unload, direct `Destroy`), so the shared panel never wedges open. If you override `OnDestroy`,
call `base.OnDestroy()`.

### Registering and showing

Build your modal as a prefab, then either register it on the manager or pass the prefab directly:

- **By key** — add an entry (`{ key, prefab }`) to the manager's `Modal Prefabs` list in the
  inspector, then `ShowModal("reward")` or the typed `ShowModal<RewardModal>("reward")`.
- **By prefab** — `ShowModal(prefab)` instantiates, opens, tracks, and returns the instance.

```csharp
var reward = _modalMgr.ShowModal<RewardModal>("reward");
reward.Setup("500 XP");
```

`ShowModal` parents the instance under the shared modal panel by default (pass `defaultParent: false`
to opt out) and activates that panel. Call `CloseAllModals()` to dismiss every tracked modal at once;
the panel hides automatically once the last one closes.

## Shipped prefabs

Core ships two ready-made overlay prefabs that the manager references from its inspector fields:

- [Modal Message.prefab](molca://asset/Packages/com.molca.core/Runtime/Modals/Modal Message.prefab) —
  the toast fed by `AddMessage`.
- [Modal Loading.prefab](molca://asset/Packages/com.molca.core/Runtime/Modals/Modal Loading.prefab) —
  the inline loading indicator fed by `AddLoading`.

Both are pooled (`ObjectPool<T>`) by the manager, so showing many is cheap. An SDK layer or project
supplies its own confirmation and custom-modal prefabs and wires them into the manager's inspector
lists.

## See also

- [Color ID](COLOR_ID.md)
- [UI Tokens](UI_TOKENS.md)
- [Subsystems](SUBSYSTEMS.md)
