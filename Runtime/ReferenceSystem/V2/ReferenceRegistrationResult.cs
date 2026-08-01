using System;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// What happened when a provider asked to register.
    /// </summary>
    /// <remarks>
    /// V1's <c>Register</c> returned a <c>bool</c>, which collapsed "you are already registered
    /// under this exact key" (harmless, and the common re-enable case) together with "a different
    /// live object owns this key" (a real authoring defect) into the same value. Callers could not
    /// tell them apart, so neither could react correctly.
    /// </remarks>
    public enum ReferenceRegistrationOutcome
    {
        /// <summary>The provider now owns the key.</summary>
        Registered = 0,

        /// <summary>
        /// This exact provider already held this exact key. A no-op, not a failure.
        /// </summary>
        AlreadyRegisteredSameKey = 1,

        /// <summary>
        /// This provider was registered under a different key, which has been released in favour of
        /// the new one.
        /// </summary>
        RekeyRequired = 2,

        /// <summary>
        /// A different live provider already owns this key. The incumbent is kept; the caller is
        /// refused, deterministically, rather than the outcome depending on load order.
        /// </summary>
        DuplicateKey = 3,

        /// <summary>The provider was null or destroyed.</summary>
        InvalidProvider = 4,

        /// <summary>The key was incomplete — see <see cref="ReferenceRuntimeKey.IsValid"/>.</summary>
        InvalidKey = 5,

        /// <summary>
        /// The key names a scope the provider does not actually live in, e.g. a
        /// <see cref="ReferenceScopeKind.PrefabLocal"/> key whose scope root is not an ancestor.
        /// </summary>
        WrongScope = 6,

        /// <summary>The registry is tearing down and is no longer accepting registrations.</summary>
        RegistryShuttingDown = 7,
    }

    /// <summary>
    /// The full result of a registration attempt: the outcome, the key it applies to, and enough
    /// context to write a useful message without holding onto the conflicting object.
    /// </summary>
    public readonly struct ReferenceRegistrationResult
    {
        /// <summary>What happened.</summary>
        public ReferenceRegistrationOutcome Outcome { get; }

        /// <summary>The key the attempt was made against.</summary>
        public ReferenceRuntimeKey Key { get; }

        /// <summary>
        /// The handle owning the entry, or null when the attempt did not produce one. Disposing it
        /// releases exactly this registration.
        /// </summary>
        public ReferenceRegistrationHandle Handle { get; }

        /// <summary>
        /// Display name of the provider that already owned the key, for
        /// <see cref="ReferenceRegistrationOutcome.DuplicateKey"/>. A name rather than the object
        /// itself, so a failed registration never extends the incumbent's lifetime.
        /// </summary>
        public string ConflictingProviderName { get; }

        /// <summary>True when the provider owns the key once the call returns.</summary>
        public bool IsRegistered =>
            Outcome == ReferenceRegistrationOutcome.Registered ||
            Outcome == ReferenceRegistrationOutcome.AlreadyRegisteredSameKey ||
            Outcome == ReferenceRegistrationOutcome.RekeyRequired;

        internal ReferenceRegistrationResult(
            ReferenceRegistrationOutcome outcome,
            ReferenceRuntimeKey key,
            ReferenceRegistrationHandle handle = null,
            string conflictingProviderName = null)
        {
            Outcome = outcome;
            Key = key;
            Handle = handle;
            ConflictingProviderName = conflictingProviderName ?? string.Empty;
        }

        /// <summary>A one-line, log-ready summary.</summary>
        public string Describe() => Outcome switch
        {
            ReferenceRegistrationOutcome.Registered => $"registered {Key}",
            ReferenceRegistrationOutcome.AlreadyRegisteredSameKey => $"already registered as {Key}",
            ReferenceRegistrationOutcome.RekeyRequired => $"re-keyed to {Key}",
            ReferenceRegistrationOutcome.DuplicateKey =>
                $"{Key} is already held by '{ConflictingProviderName}'",
            ReferenceRegistrationOutcome.InvalidProvider => "provider was null or destroyed",
            ReferenceRegistrationOutcome.InvalidKey => $"incomplete key {Key}",
            ReferenceRegistrationOutcome.WrongScope => $"provider does not live in the scope named by {Key}",
            ReferenceRegistrationOutcome.RegistryShuttingDown => "registry is shutting down",
            _ => Outcome.ToString(),
        };

        /// <inheritdoc/>
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Owns exactly one registration. Disposing it removes that entry and nothing else.
    /// </summary>
    /// <remarks>
    /// The handle captures the key as it was at registration time. That is the point: v1 removed
    /// entries by re-reading the provider's current <see cref="IReferenceable.RefId"/>, so a
    /// provider whose id was changed while registered unregistered the wrong key — or none — and
    /// left the real entry orphaned in the registry forever.
    /// </remarks>
    public sealed class ReferenceRegistrationHandle : IDisposable
    {
        private ReferenceManager _registry;

        /// <summary>The immutable key this handle owns.</summary>
        public ReferenceRuntimeKey Key { get; }

        /// <summary>The registered provider.</summary>
        public IReferenceable Provider { get; }

        /// <summary>False once <see cref="Dispose"/> has run, or the entry was removed some other way.</summary>
        public bool IsActive => _registry != null;

        internal ReferenceRegistrationHandle(ReferenceManager registry, ReferenceRuntimeKey key, IReferenceable provider)
        {
            _registry = registry;
            Key = key;
            Provider = provider;
        }

        /// <summary>
        /// Marks the handle spent without touching the registry, for when the registry itself
        /// removed the entry (teardown, a purge, or an explicit <c>Unregister</c>).
        /// </summary>
        internal void MarkReleased() => _registry = null;

        /// <summary>Releases this registration. Safe to call more than once.</summary>
        public void Dispose()
        {
            var registry = _registry;
            _registry = null;
            registry?.ReleaseHandle(this);
        }

        /// <inheritdoc/>
        public override string ToString() => $"{Key} ({(IsActive ? "active" : "released")})";
    }
}
