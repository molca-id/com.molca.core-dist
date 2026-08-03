---
title: Modals
category: UI
order: 210
---

# Modals

Core provides the modal *system* — `ModalManager` and the `BaseModal` base class (see
[Modals](MODALS.md)). The SDK provides a **library of concrete modals** built on that base, so common
prompts (selection, text/number entry, progress, media confirmation, date picking, notifications) are
ready to show rather than authored per project.

## Shipped modals

Each type subclasses `BaseModal` and is shown through Core's `ModalManager`:

| Modal | Purpose |
|---|---|
| `SelectionModal` | Single/multi-select list; options are `SelectionOption` (`id`, `displayText`, `description`, `isSelected`, `isEnabled`, `data`) with a `SelectionType`. |
| `TextInputModal` | Free-text entry with validation. |
| `NumberInputKeyboard` | On-screen numeric entry. |
| `ProgressModal` | Determinate/indeterminate progress display. |
| `MediaConfirmationModal` | Confirm an action alongside a media preview (see [Media](SDK_MEDIA.md)). |
| `DatePicker` | Calendar date selection. |
| `Notification` | Transient notification banners. |

## Using and extending

Show a modal through the Core `ModalManager` exactly as with any `BaseModal` — the SDK types add
content and behavior, not a new presentation path. To customize, subclass the relevant modal in project
space; because they derive from `BaseModal`, they inherit the queueing, lifecycle, and dismissal
handled by Core.

`MediaConfirmationHelper` (used by the SDK's [GameManager](SDK_APP_FLOW.md)) is an example of composing
these modals into an app-level prompt.

## See also

- [Modals](MODALS.md)
- [SDK UI](SDK_UI.md)
- [SDK Overview](SDK_OVERVIEW.md)
