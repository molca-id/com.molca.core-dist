---
title: Audio
category: Localization & Audio
order: 710
---

# Audio

The audio system plays music, sound effects, and localized voice through one subsystem —
`AudioManager` — backed by read-only ScriptableObject config. Clips are Addressable assets loaded on
demand, ref-counted, and released after playback, so you name a clip by id and let the manager own the
load/route/unload lifecycle.

## The pieces

| Type | Kind | Role |
|---|---|---|
| `AudioManager` | `RuntimeSubsystem` | Loads, plays, routes, and releases clips; owns the shared music/SFX/voice sources and volume controls. |
| `AudioModule` | `SettingModule` (SO) | Config: the three `AudioLibrary` references, the `AudioMixer`, and its mixer groups. |
| `AudioLibrary` | `ScriptableObject` (read-only) | One per `AudioType` (Music/SFX/Voice); holds a list of collections. |
| `AudioCollection` | `ScriptableObject` (read-only) | Named bag of `AudioEntry` (id + Addressable `AudioClip`). |
| `DialogAudioCollection` | `ScriptableObject` (read-only) | Voice-only collection of `LocalizedAudioEntry` (one clip per language). |
| `AudioReference` | `[Serializable]` | Inspector-authored handle (collection + id + type) you `Play()` from your own scripts. |
| `DialogAudioReference` | `[Serializable]` | Same, for localized dialog lines. |
| `AudioPlayer` | `MonoBehaviour` | Drop-in component that plays `AudioReference`s on Unity lifecycle/UI triggers. |

## Config: `AudioModule` and the libraries

Audio is configured through a `SettingModule` registered in `GlobalSettings`. Author the assets with the
**Create** menu:

- `AudioModule` — *Create → Molca → Settings → Audio*. Assign three `AudioLibrary` references (music,
  SFX, voice), the project's `AudioMixer`, and the master/BGM/SFX/VO mixer groups.
- `AudioLibrary` — *Create → Molca → Audio → Audio Library*. Set its `AudioType` and add collections.
- `AudioCollection` — *Create → Molca → Audio → Audio Collection*. Add entries, each an `id` plus an
  `AssetReferenceT<AudioClip>` (Addressables).
- `DialogAudioCollection` — *Create → Molca → Audio → Dialog Audio Collection*. Voice libraries take
  *only* these; each entry carries a clip per language code.

These ScriptableObjects are **read-only config**. Their serialized collection/entry lists are authoring
data: mutators like `AddCollection`, `RemoveCollection`, `AddEntry`, and `RemoveEntry` are edit-time
operations and refuse to run in play mode (an `AudioAuthoringGuard` logs an error and ignores the call).
Build your libraries in the editor; do not try to grow them at runtime — use C# state for anything
mutable, per the framework's SO rule. `Initialize()` / `Clear()` are lifecycle hooks the manager calls to
build/drop the lookup caches and release loaded assets — they never touch the authored lists.

## Playing audio from code

Get the subsystem the normal way — `[Inject]` a field, or `RuntimeManager.GetSubsystem<AudioManager>()`
after `WaitForInitialization()`. Never `FindObjectOfType`. The async methods follow the framework's async
contract (`Awaitable`, trailing `CancellationToken`, linked to the subsystem's `ShutdownToken`); each
completes once playback has *started*, not when the clip ends.

```csharp
public class TrainingIntro : MonoBehaviour
{
    [Inject] private AudioManager _audio;

    private async void Start() // Unity entry-point shim: body is guarded
    {
        try
        {
            await RuntimeManager.Instance.WaitForInitialization();
            await _audio.PlayMusicAsync("ambient", "menu-loop", destroyCancellationToken);
            await _audio.PlaySFXAsync("ui", "confirm", volume: 0.8f);
        }
        catch (OperationCanceledException) { /* shutdown/destroy — not an error */ }
    }
}
```

| Method | Plays | Notes |
|---|---|---|
| `PlayMusicAsync(collection, id, ct)` | Music | Loops on the shared music source with a cross-fade; a new call cross-fades the old track out. |
| `PlaySFXAsync(collection, id, volume, source, ct)` | SFX | One-shot; optional custom `AudioSource`. |
| `PlayVoiceAsync(collection, id, volume, source, ct)` | Voice | Localized; ducks music and SFX while it plays, then restores. |
| `PlayFromSourceAsync(source, collection, id, type, volume, ct)` | Any | Plays on a caller-owned `AudioSource`, routed to the mixer group for `type`. |
| `StopMusic()` / `StopVoice()` | — | Fade-out (music) / stop-and-restore-ducking (voice). |
| `SetupAudioSource(source, type)` | — | Routes an `AudioSource` to the right mixer group without playing. |

Each `PlayXAsync` has a matching **legacy** `PlayX(...)` (e.g. `PlayMusic`, `PlaySFX`, `PlayVoice`,
`PlayFromSource`). These are thin `async void` shims that await the async form and contain their own
exceptions — convenient for `UnityEvent` wiring, but prefer the `Async` methods in new code so you can
await and cancel.

Voice playback is localized: a voice `id` resolves through a `DialogAudioCollection`'s
`LocalizedAudioEntry`, which loads the clip for `LocalizationManager.CurrentLanguage`. See
[Localization](LOCALIZATION.md).

## Inspector-authored references

For designer-driven audio, serialize an `AudioReference` (collection name + id + `AudioType`) on your
component and call `Play()` — it resolves the `AudioManager` and dispatches to the right play method:

```csharp
[SerializeField] private AudioReference _clickSound; // set collection/id/type in the Inspector
public void OnClicked() => _clickSound.Play();
```

`AudioReference` also offers `PlayWithSource(AudioSource, volume)` and `SetupAudioSource(AudioSource)` for
playing through your own source. `DialogAudioReference.PlayDialog()` is the voice equivalent, with
`IsValid()` / `GetAvailableLanguages()` to check that every configured language has a clip.

### `AudioPlayer` component

Add **Molca → Audio → Audio Player** to a GameObject to play references without writing code. It maps
named `AudioEvent`s (each an `AudioReference`) to `AudioTrigger`s fired on Unity lifecycle and UI events
(`OnStart`, `OnEnable`, `OnTriggerEnter`, `OnButtonClick`, `OnPointerClick`, `Manual`, …). Call
`PlayAudio("event-name")` for the `Manual` trigger, or let the component fire the others automatically.

## Volume

Volume lives in the `AudioMixer`, not on the ScriptableObjects. `AudioManager` exposes setters that take a
normalized `0–1` value, convert it to dB, apply it to the mixer, and persist it; each also raises a typed
event so UI can react:

```csharp
_audio.SetMasterVolume(0.75f); // → mixer dB + saved + TypedEvents.MasterVolumeChanged
_audio.SetMusicVolume(0.5f);
_audio.SetSFXVolume(1f);
_audio.SetVoiceVolume(0.8f);
float current = _audio.MusicVolume; // normalized read-back from the mixer
```

`AudioManager` delegates these to `AudioModule`, which owns the dB conversion and reload of persisted
values (the mixer ignores parameter writes before its first audio frame, so `AudioModule` re-applies
saved volumes on the next frame). Because the mixer holds the truth, volume is runtime state — not a
write back into the config SO.

## How loading works

Clips are Addressable (`AssetReferenceT<AudioClip>`). On first play of a `(collection, id, type)` key,
`AudioManager` loads the clip and holds one Addressables handle; overlapping plays of the same clip share
that handle via a logical ref-count, and the asset is released only when the last playback finishes
(SFX/voice) or when the track is replaced/stopped (music). On `Teardown()`/`OnDestroy` every loaded clip
is force-released, so you never manage handles yourself. This mirrors the async contract used across the
framework — see [Async Contract](ASYNC_CONTRACT.md).

## See also

- [Localization](LOCALIZATION.md)
- [Subsystems](SUBSYSTEMS.md)
- [Settings](SETTINGS.md)
- [Content Packages](CONTENT_PACKAGES.md)
- [Async Contract](ASYNC_CONTRACT.md)
