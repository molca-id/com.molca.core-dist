using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>What kind of event a <see cref="ReferenceDiagnostic"/> records.</summary>
    public enum ReferenceDiagnosticKind
    {
        /// <summary>A provider took a key.</summary>
        Registered = 0,

        /// <summary>A provider released a key.</summary>
        Unregistered = 1,

        /// <summary>A registration was refused because a live provider already held the key.</summary>
        RegistrationConflict = 2,

        /// <summary>A registration was refused because the provider or key was unusable.</summary>
        InvalidRegistration = 3,

        /// <summary>A reference resolved on its exact key.</summary>
        ResolvedExact = 4,

        /// <summary>A reference resolved only through the v1 id-only compatibility path.</summary>
        ResolvedViaLegacyFallback = 5,

        /// <summary>The compatibility path found more than one candidate and refused to guess.</summary>
        AmbiguousFallback = 6,

        /// <summary>A resolved provider was of the wrong runtime type or in the wrong scope.</summary>
        WrongTypeOrScope = 7,

        /// <summary>A deferred resolve ran out of its wait budget.</summary>
        TimedOut = 8,

        /// <summary>A wait was cancelled by its caller or by owner teardown.</summary>
        Cancelled = 9,

        /// <summary>A destroyed entry was found still in the registry and dropped.</summary>
        DestroyedEntryPurged = 10,

        /// <summary>A deferred resolve succeeded after waiting.</summary>
        LateSuccess = 11,
    }

    /// <summary>One recorded reference-system event.</summary>
    /// <remarks>
    /// Holds only strings and value types. Retaining the <see cref="IReferenceable"/> would keep a
    /// destroyed scene object's managed wrapper alive for as long as the buffer held the entry, and
    /// would make the diagnostic stream a memory leak proportional to churn.
    /// </remarks>
    public readonly struct ReferenceDiagnostic
    {
        /// <summary>What happened.</summary>
        public ReferenceDiagnosticKind Kind { get; }

        /// <summary>The key involved, in <see cref="ReferenceRuntimeKey.ToString"/> form.</summary>
        public string Key { get; }

        /// <summary>Display name of the provider involved, if any.</summary>
        public string Provider { get; }

        /// <summary>Free-form detail: the conflicting holder, the expected type, the timeout, and so on.</summary>
        public string Detail { get; }

        /// <summary>The frame this was recorded on.</summary>
        public int Frame { get; }

        /// <summary>Seconds since startup when this was recorded.</summary>
        public double Time { get; }

        internal ReferenceDiagnostic(
            ReferenceDiagnosticKind kind, string key, string provider, string detail, int frame, double time)
        {
            Kind = kind;
            Key = key;
            Provider = provider ?? string.Empty;
            Detail = detail ?? string.Empty;
            Frame = frame;
            Time = time;
        }

        /// <summary>True for the kinds that indicate something went wrong.</summary>
        public bool IsProblem =>
            Kind == ReferenceDiagnosticKind.RegistrationConflict ||
            Kind == ReferenceDiagnosticKind.InvalidRegistration ||
            Kind == ReferenceDiagnosticKind.AmbiguousFallback ||
            Kind == ReferenceDiagnosticKind.WrongTypeOrScope ||
            Kind == ReferenceDiagnosticKind.TimedOut;

        /// <inheritdoc/>
        public override string ToString()
        {
            string suffix = string.IsNullOrEmpty(Detail) ? string.Empty : $" — {Detail}";
            string who = string.IsNullOrEmpty(Provider) ? string.Empty : $" '{Provider}'";
            return $"[f{Frame}] {Kind}{who} {Key}{suffix}";
        }
    }

    /// <summary>
    /// A bounded in-memory record of what the reference system did, for the Hub's Runtime view and
    /// for diagnosing load-order problems after the fact.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An unbounded log of every registration in a long play session is a leak,
    /// and the interesting window is almost always the most recent one — a conflict is diagnosed
    /// from what happened around it, not from the first minute of the session.
    ///
    /// This is a development aid, not telemetry: nothing here leaves the process.
    /// </remarks>
    public sealed class ReferenceRuntimeDiagnostics
    {
        /// <summary>How many events are retained before the oldest is dropped.</summary>
        public const int DefaultCapacity = 256;

        private readonly Queue<ReferenceDiagnostic> _events;

        /// <summary>The retention limit.</summary>
        public int Capacity { get; }

        /// <summary>How many events have ever been recorded, including dropped ones.</summary>
        public long TotalRecorded { get; private set; }

        /// <summary>How many events were dropped to stay within <see cref="Capacity"/>.</summary>
        public long Dropped { get; private set; }

        /// <summary>Raised for each recorded event. Handlers are isolated by the caller.</summary>
        public event Action<ReferenceDiagnostic> Recorded;

        /// <summary>Creates a stream retaining <paramref name="capacity"/> events.</summary>
        /// <param name="capacity">Retention limit; values below one fall back to <see cref="DefaultCapacity"/>.</param>
        public ReferenceRuntimeDiagnostics(int capacity = DefaultCapacity)
        {
            Capacity = capacity > 0 ? capacity : DefaultCapacity;
            _events = new Queue<ReferenceDiagnostic>(Capacity);
        }

        /// <summary>The retained events, oldest first.</summary>
        public IReadOnlyList<ReferenceDiagnostic> Snapshot() => new List<ReferenceDiagnostic>(_events);

        /// <summary>How many events are currently retained.</summary>
        public int Count => _events.Count;

        /// <summary>Records an event, dropping the oldest if the buffer is full.</summary>
        /// <param name="kind">What happened.</param>
        /// <param name="key">The key involved.</param>
        /// <param name="provider">Display name of the provider involved.</param>
        /// <param name="detail">Free-form detail.</param>
        public void Record(
            ReferenceDiagnosticKind kind,
            ReferenceRuntimeKey key,
            string provider = null,
            string detail = null)
        {
            Record(kind, key.IsValid ? key.ToString() : string.Empty, provider, detail);
        }

        /// <summary>Records an event whose key is not a well-formed runtime key.</summary>
        /// <param name="kind">What happened.</param>
        /// <param name="key">The key text involved.</param>
        /// <param name="provider">Display name of the provider involved.</param>
        /// <param name="detail">Free-form detail.</param>
        public void Record(
            ReferenceDiagnosticKind kind,
            string key,
            string provider = null,
            string detail = null)
        {
            var entry = new ReferenceDiagnostic(
                kind, key ?? string.Empty, provider, detail, Time.frameCount, Time.realtimeSinceStartupAsDouble);

            while (_events.Count >= Capacity)
            {
                _events.Dequeue();
                Dropped++;
            }

            _events.Enqueue(entry);
            TotalRecorded++;

            var handlers = Recorded;
            if (handlers == null)
                return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<ReferenceDiagnostic>)handler).Invoke(entry);
                }
                catch (Exception e)
                {
                    // A misbehaving observer — typically an editor window — must not be able to
                    // break the registry operation that produced the event.
                    Debug.LogError($"[ReferenceDiagnostics] Recorded handler threw: {e}");
                }
            }
        }

        /// <summary>Drops every retained event and resets the counters.</summary>
        public void Clear()
        {
            _events.Clear();
            TotalRecorded = 0;
            Dropped = 0;
        }
    }
}
