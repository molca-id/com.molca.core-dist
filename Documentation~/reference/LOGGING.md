---
title: Logging
category: Diagnostics
order: 1100
---

# Logging

Molca captures every `Debug.Log` in the process and fans it out to destinations you choose. The
pipeline is in `Molca.Logging` (`Runtime/Logging/`), and `LogManager` — a `RuntimeSubsystem` on the
Runtime Manager prefab — configures it.

You do not log through Molca. Keep calling `Debug.Log`, `Debug.LogWarning`, `Debug.LogError` and
`Debug.LogException` exactly as you would in any Unity project. The pipeline observes them.

## The one rule

**Nothing here can hide a message from Unity.** Capture forwards to Unity's own log handler first and
unconditionally, before any threshold is consulted. The Console, the player log,
`Application.logMessageReceived`, and anything built on it — a crash reporter, the development-player
bridge — always receive every message.

Verbosity settings narrow what *Molca's own destinations* record. They are not a mute.

> This is worth stating because the previous implementation got it wrong. It replaced Unity's handler
> instead of wrapping it, and returned before forwarding whenever its filter rejected a message. A
> setting that read as log-file verbosity silently suppressed `Debug` process-wide.

## Layers

| Layer | Type | Job |
|---|---|---|
| Capture | `LogCapture` | Decorates `ILogHandler`. Forwards to Unity, then builds a `MolcaLogEntry`. |
| Pipeline | `MolcaLogPipeline` | Static. Installs capture, owns the sink list, dispatches. |
| Destination | `ILogSink` | Receives entries that pass its own `MinimumLevel`. |
| Configuration | `LogManager` | Sets levels, owns the file sink, exposes `EntryLogged`. |

Capture installs at `RuntimeInitializeLoadType.SubsystemRegistration` — before the first scene loads,
and long before the Runtime Manager prefab exists. That is deliberate: bootstrap is the window where a
project actually breaks, and no component that bootstrap creates could observe it. Entries captured
before `LogManager` exists are buffered in a `MemoryLogSink` and drained into the file sink once there
is one.

## Levels

`MolcaLogLevel` is ascending, and `Verbose` is `0`:

```csharp
public enum MolcaLogLevel { Verbose = 0, Info, Warning, Error, None }
```

Zero being the most permissive value is the point. An unset serialized field defaults to `0`, and a
logger that fails open loses volume while one that fails closed loses the evidence for why — only the
first is recoverable.

Unity's `LogType` is unusable as a threshold, which is why this enum exists: its ordinals are
`Error=0, Assert=1, Warning=2, Log=3, Exception=4`, neither ascending nor descending in severity.
`MolcaLogLevels.FromUnity` maps `Log` to `Info`, `Warning` to `Warning`, and `Assert`, `Error` and
`Exception` all to `Error`.

`None` disables a sink without unregistering it, so a runtime toggle does not lose a queued tail.

## Configuring `LogManager`

| Field | Default | Meaning |
|---|---|---|
| `playerLogLevel` | `Info` | Lowest severity Molca's destinations record in a player build. |
| `editorLogLevel` | `Verbose` | Same, in the Editor and in PlayMode tests. |
| `writeLogFiles` | off | Write rotating files under `persistentDataPath/Logs`. |
| `maxLogFiles` | 5 | Total files retained, including the current session's. |
| `maxLogSizeInMB` | 10 | Rotate once the current file passes this. |
| `includeStackTraces` | on | Write stack traces for errors and exceptions. |

The Editor default is `Verbose` on purpose: an author at a keyboard wants their own
`Debug.LogWarning` calls to be visible, and PlayMode tests need
`LogAssert.Expect(LogType.Warning, …)` to be able to match.

## Reading logs

### A callback

```csharp
var manager = RuntimeManager.GetSubsystem<LogManager>();
manager.EntryLogged += entry =>
{
    if (entry.Level >= MolcaLogLevel.Warning) ShowBanner(entry.Message);
};
```

`EntryLogged` uses `LogManager`'s own threshold. Unsubscribe on destroy.

### Recent history, including bootstrap

```csharp
foreach (var entry in MolcaLogPipeline.Memory.Snapshot())
    Debug.Log(entry.Format());
```

`Snapshot()` returns a copy, oldest first. The ring holds 512 entries and reports `DroppedCount`.

### Your own destination

Implement `ILogSink` when you need your own threshold or buffering:

```csharp
public sealed class CrashBreadcrumbSink : ILogSink
{
    public string Name => "breadcrumbs";
    public MolcaLogLevel MinimumLevel => MolcaLogLevel.Warning;

    public void Write(in MolcaLogEntry entry) => _queue.Enqueue(entry);   // never blocks
    public void Flush() => Upload(_queue);                               // may block
    public void Dispose() { }
}

MolcaLogPipeline.AddSink(new CrashBreadcrumbSink());
```

For a one-off callback, `ActionLogSink` saves writing a class:

```csharp
var sink = new ActionLogSink("hud", e => _hud.Append(e.Message), MolcaLogLevel.Error);
MolcaLogPipeline.AddSink(sink);
// ...
MolcaLogPipeline.RemoveSink(sink);
```

## Sink contract

Three rules, all consequences of how Unity dispatches logs:

1. **`Write` may be called from any thread.** Unity invokes log handlers on whichever thread called
   `Debug.Log`, including network and worker threads. Be thread-safe.
2. **`Write` must not block.** Anything slow belongs behind a queue that `Flush` drains. `Flush` runs
   on the main thread on a cadence, on pause, on focus loss, and on teardown.
3. **Never log from a sink.** Re-entrancy is guarded per thread, so a log raised while handling a log
   is dropped rather than recursing — but it is still a dropped log.

A sink that throws is isolated and counted in `MolcaLogPipeline.PipelineFailures`; the entry still
reaches the other sinks, and the exception never propagates into the `Debug.Log` call site. A non-zero
`PipelineFailures` means records were lost.

## `MolcaLogEntry`

```csharp
MolcaLogLevel Level;      LogType UnityLogType;
string Message;           string StackTrace;      // may be null
string ContextName;       // may be null — see below
DateTime TimestampUtc;    int ThreadId;   bool IsMainThread;
```

`Format(includeStackTrace)` renders one log-file record: a sortable UTC header, the log type, the
thread when it is not the main one, the context, then the message.

`ContextName` is the *name* of the object passed as `Debug.LogWarning(message, this)`, resolved at
capture time. It is a string and not the object because `UnityEngine.Object.name` is main-thread-only
and a sink must never be able to read a destroyed object. On a background-thread log it is `null` even
when a context was supplied. Carrying this name at all is the reason capture decorates `ILogHandler`
rather than subscribing to `Application.logMessageReceivedThreaded`, which does not provide it.

## Log files

`FileLogSink` never touches the disk from a `Debug.Log` call. `Write` enqueues; `Flush` writes. The
queue is bounded and drops the oldest entries when full, counting them in `DroppedCount` — the newest
entries describe whatever is going wrong now.

File names are `runtime-log_<timestamp>.txt`, and a name is claimed by creating the file with
`FileMode.CreateNew`, so two sinks or two rotations inside the same second cannot merge into one file.
The file currently being written is never a rotation-deletion candidate.

The sink is unavailable on **WebGL**, where `persistentDataPath` is an IndexedDB shim that only reaches
storage on an explicit sync. `IsAvailable` is `false` there, and also after a write failure — retrying a
broken destination would produce one error per flush for the rest of the session.

## Testing against logs

`LogAssert` observes Unity's handler, which capture always forwards to, so it behaves normally.

To assert on what Molca's destinations received, register a sink and remove it in teardown. Do not
swap `Debug.unityLogger.logHandler`.

```csharp
[Test]
public void MyFeature_WarnsAboutMisconfiguration()
{
    var sink = new ActionLogSink("test", e => _seen.Add(e));
    MolcaLogPipeline.AddSink(sink);
    try
    {
        LogAssert.Expect(LogType.Warning, "expected text");
        MyFeature.Run();
        Assert.AreEqual(1, _seen.Count);
    }
    finally
    {
        MolcaLogPipeline.RemoveSink(sink);
    }
}
```

`LogAssert.NoUnexpectedReceived()` now also sees info-level framework messages, because capture no
longer suppresses them. Prefer asserting the specific consequence: an unexpected error, assert or
exception log already fails a test by default.

## Migrating from the old API

| Old | New |
|---|---|
| `LogManager.minimumLogLevel` (`LogType`) | `playerLogLevel` / `editorLogLevel` (`MolcaLogLevel`) |
| `LogManager.saveToStreamingAssets` | `writeLogFiles` (the old name never referred to StreamingAssets) |
| `LogManager.onLogInfo` / `onLogWarning` / `onLogError` | `LogManager.EntryLogged`, or an `ILogSink` |
| `new LogHandler(manager)` | nothing — `MolcaLogPipeline` owns capture |
| `LogHandler.SetMinimumLogLevel` | a sink's `MinimumLevel` |

The three `onLog*` fields still work and are `[Obsolete]`. `LogHandler` is `[Obsolete]` and inert:
constructing it installs nothing, and its setters are no-ops, because honouring them would re-create the
global mute.

Projects that never touched the log level get warnings back on upgrade. That is the fix, but it is a
behaviour change — if a project relied on the quiet, set `playerLogLevel` explicitly.

## See also

- [Runtime Manager & Bootstrap](RUNTIME_MANAGER.md) — where `LogManager` sits in the bootstrap order
- [Telemetry & Diagnostics](TELEMETRY.md) — structured events, distinct from log capture
- [Modals](MODALS.md) — `ModalManager` registers a sink to show logs on screen
