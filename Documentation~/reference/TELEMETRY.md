---
title: Telemetry & Diagnostics
category: Diagnostics
order: 1110
---

# Telemetry & Diagnostics

`TelemetrySubsystem` is the application-wide event collector. It records named events with properties,
tags each with a per-run session id, batches them, and forwards them to one or more pluggable
**sinks** — console, file, or an HTTP batch endpoint. It is inactive unless enabled in settings, so
shipping it costs nothing until a project opts in.

## TelemetrySubsystem

Add it as a child of the RuntimeManager prefab (a low `InitializationPriority` so it comes up late and
other systems can emit as they finish starting). Resolve it *optionally* so consumers degrade
gracefully when it is absent:

```csharp
[Inject(false)] private TelemetrySubsystem _telemetry;   // null when not present

_telemetry?.Track("sequence.step_started", new Dictionary<string, object>
{
    ["stepId"] = step.Id,
    ["elapsed"] = elapsed,
});
```

| Member | Purpose |
|---|---|
| `Track(name, properties = null)` | Records an event (no-op when disabled). Main thread. |
| `FlushAsync(ct)` | Forces all sinks to flush; overlapping callers await the in-flight flush. |
| `SessionId` | Unique id for the current run, attached to every event. |
| `IsEnabled` | True when telemetry is on and at least one sink is active. |

Events flush on a timer (`FlushIntervalSeconds`) and whenever `BatchSize` events accumulate. The flush
loop is keyed on `ShutdownToken`; on `Teardown()` each sink is disposed with a best-effort synchronous
flush so nothing is lost at quit.

## Sinks

A sink implements `ITelemetrySink` (`Write(TelemetryEvent)` + `FlushAsync(ct)` + `Dispose`). Core ships
three, toggled independently in settings:

| Sink | Behavior |
|---|---|
| `ConsoleTelemetrySink` | Writes events to the Unity console. |
| `FileTelemetrySink` | Appends events to a local log file. |
| `HttpBatchTelemetrySink` | POSTs batches to `HttpEndpointUrl`. |

To add your own, implement `ITelemetrySink` in project space; a `TelemetryEvent` exposes `Name`,
`SessionId`, `TimestampUtc`, `Properties`, and `ToJson()`.

## Configuration — TelemetrySettings

`TelemetrySettings` is a [SettingModule](SETTINGS.md); add it to `GlobalSettings.modules`.

| Field | Meaning |
|---|---|
| `EnableTelemetry` | Master switch; off ⇒ `Track` is a no-op. |
| `EnableConsoleSink` / `EnableFileSink` / `EnableHttpSink` | Which sinks are built. |
| `HttpEndpointUrl` | Destination for the HTTP batch sink. |
| `BatchSize` | Events buffered before a flush is triggered. |
| `FlushIntervalSeconds` | Periodic flush cadence. |

## Logging

Framework logging (the `Debug.Log` write path) is separate from telemetry and is safe to call from any
thread — its buffering clock and re-entrancy guard never touch main-thread-only Unity APIs. See the
threading section of the [Async Contract](ASYNC_CONTRACT.md) for the full rule.

## See also

- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
- [Utilities](UTILITIES.md)
- [Runtime Subsystems](SUBSYSTEMS.md)
- [Async Contract](ASYNC_CONTRACT.md)
