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

## MolcaDiagnostics — crash/breadcrumb sinks

Telemetry answers *what the app did*; diagnostics answers *what went wrong*. `MolcaDiagnostics`
(`Runtime/Diagnostics/`, namespace `Molca`) is a static, vendor-neutral facade for forwarding
breadcrumbs and captured exceptions to an optional customer-owned crash reporter. It is deliberately
**not** a subsystem: framework code at any lifecycle stage — including before bootstrap — can call it,
and with no registered sink every operation is a safe no-op.

| Member | Purpose |
|---|---|
| `Register(IMolcaDiagnosticsSink)` | Adds a sink; returns an `IDisposable` that unregisters it. |
| `Unregister(IMolcaDiagnosticsSink)` | Removes a sink explicitly. |
| `AddBreadcrumb(MolcaBreadcrumb)` | Forwards a breadcrumb to every enabled sink. |
| `CaptureException(exception, context = null)` | Forwards an explicitly captured exception (context defaults to component `"framework"`). |
| `FlushAsync(ct)` | Awaits each enabled sink's flush; cancellation propagates, other failures do not. |

A sink implements `IMolcaDiagnosticsSink` (`Name`, `IsEnabled`, `AddBreadcrumb`, `CaptureException`,
`FlushAsync`). Register it from project space and dispose the returned handle to detach:

```csharp
// e.g. in a project subsystem's InitializeAsync
_diagnostics = MolcaDiagnostics.Register(new SentryDiagnosticsSink());

MolcaDiagnostics.AddBreadcrumb(new MolcaBreadcrumb(
    "sequence", "step started", MolcaBreadcrumbLevel.Info,
    new Dictionary<string, string> { ["stepId"] = step.Id }));

try { await UploadAsync(ct); }
catch (Exception exception)
{
    MolcaDiagnostics.CaptureException(exception,
        new MolcaDiagnosticContext("report-upload"));
    throw;
}
```

Contract notes:

- **Payloads are bounded at construction.** `MolcaBreadcrumb` / `MolcaDiagnosticContext` trim category
  and component to 64 chars, messages and property values to 256, and keep at most 16 properties, so an
  unbounded string can never reach a sink.
- **Sinks are isolated.** Every sink call is wrapped in `try/catch`; a throwing or misbehaving sink can
  never change application success/failure behavior or recursively log. Implementations must also avoid
  mutating application state.
- **Registration is thread-safe** (lock + snapshot on dispatch), so a sink can be registered or removed
  while breadcrumbs are being added.
- **Diagnostics is not usage telemetry.** The sanitized `TelemetrySubsystem` contract above stays
  separate on purpose — don't route product analytics through a crash sink.

## Logging

Framework logging (the `Debug.Log` write path) is separate from telemetry and is safe to call from any
thread — its buffering clock and re-entrancy guard never touch main-thread-only Unity APIs. See the
threading section of the [Async Contract](ASYNC_CONTRACT.md) for the full rule.

## See also

- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
- [Utilities](UTILITIES.md)
- [Runtime Subsystems](SUBSYSTEMS.md)
- [Async Contract](ASYNC_CONTRACT.md)
