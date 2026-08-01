using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Central singleton manager for tracking and retrieving referenceable objects.
    /// Provides global access to all registered objects by their reference IDs.
    /// </summary>
    /// <remarks>
    /// The authoritative registry is keyed by <see cref="ReferenceRuntimeKey"/> — scope plus
    /// <c>(RefType, RefId)</c>. Every other index is derived from it through
    /// <see cref="AddToIndexes"/>/<see cref="RemoveFromIndexes"/> so they cannot disagree.
    ///
    /// The v1 API surface — <see cref="Register(IReferenceable)"/>, the
    /// <see cref="ReferenceId"/>-keyed lookups, and the per-type queries — continues to behave
    /// exactly as before by mapping onto <see cref="ReferenceScopeKind.LegacyGlobal"/> keys. Scoped
    /// entries are deliberately invisible to those lookups: a prefab-local id is not unique across
    /// the project, so answering a bare <c>(RefType, RefId)</c> query with one would reach into a
    /// scope the caller had no way to name, which is the exact collision the scope model exists to
    /// prevent.
    /// </remarks>
    public class ReferenceManager : RuntimeSubsystem
    {
        private static ReferenceManager _instance;
        private static readonly object _lock = new object();

        /// <summary>One registration: a provider, the key it holds, and the handle that owns it.</summary>
        private sealed class Entry
        {
            public IReferenceable Provider;
            public ReferenceRuntimeKey Key;
            public ReferenceRegistrationHandle Handle;
        }

        // Authoritative registry: full scoped key -> entry.
        private readonly Dictionary<ReferenceRuntimeKey, Entry> _byKey = new Dictionary<ReferenceRuntimeKey, Entry>();

        // Derived: v1 (RefType, RefId) compatibility index. Global-scope entries only.
        private readonly Dictionary<ReferenceId, Entry> _byLegacyId = new Dictionary<ReferenceId, Entry>();

        // Derived: per-type id multimap backing the v1 type queries. Global-scope entries only.
        private readonly Dictionary<string, Dictionary<string, IReferenceable>> _typeRegistries = new Dictionary<string, Dictionary<string, IReferenceable>>();

        // Derived: reverse lookup. One provider holds at most one registration.
        private readonly Dictionary<IReferenceable, Entry> _byProvider = new Dictionary<IReferenceable, Entry>();

        // Scope instance ids currently open, published by ReferenceScopeRoot.
        private readonly HashSet<string> _openScopes = new HashSet<string>(StringComparer.Ordinal);

        private bool _isShuttingDown;

        /// <summary>
        /// Bounded record of what the registry did, for the Hub's Runtime view and for diagnosing
        /// load-order problems after the fact.
        /// </summary>
        public ReferenceRuntimeDiagnostics Diagnostics { get; } = new ReferenceRuntimeDiagnostics();

        /// <summary>
        /// Raised after an object is newly registered (after the registries are updated).
        /// Handlers are isolated: an exception in one handler is logged and does not
        /// prevent the others from running. Useful for awaiting late registration.
        /// </summary>
        public event Action<IReferenceable> Registered;

        /// <summary>
        /// Raised after an object is unregistered (after the registries are updated).
        /// Handlers are isolated the same way as <see cref="Registered"/>.
        /// </summary>
        public event Action<IReferenceable> Unregistered;

        /// <summary>
        /// Raised after a registration, carrying the key that was taken.
        /// </summary>
        /// <remarks>
        /// <see cref="Registered"/> reports only the object, so an awaiting resolver had to re-probe
        /// the registry to discover whether the arrival was the one it wanted. This overload lets a
        /// scoped wait match on the full key it asked for.
        /// </remarks>
        public event Action<ReferenceRuntimeKey, IReferenceable> KeyRegistered;

        /// <summary>Raised after a registration is released, carrying the key that was freed.</summary>
        public event Action<ReferenceRuntimeKey, IReferenceable> KeyUnregistered;

        /// <summary>
        /// Cached convenience accessor that resolves the subsystem through
        /// <see cref="RuntimeManager"/>. Prefer <c>[Inject]</c> or
        /// <see cref="RuntimeManager.GetSubsystem{T}"/> directly.
        /// </summary>
        [Obsolete(
            "Use RuntimeManager.GetSubsystem<ReferenceManager>() or [Inject] ReferenceManager. "
            + "Removed next major.")]
        public static ReferenceManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = RuntimeManager.GetSubsystem<ReferenceManager>();

                            if (_instance == null)
                            {
                                Debug.LogWarning("[ReferenceManager] No instance found. Make sure ReferenceManager is added to RuntimeManager.");
                            }
                        }
                    }
                }

                return _instance;
            }
        }

        /// <summary>
        /// RuntimeSubsystem Initialize method.
        /// Called by RuntimeManager during system initialization.
        /// </summary>
        /// <param name="finishCallback">Callback to invoke when initialization is complete.</param>
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            try
            {
                if (IsDebugEnabled)
                {
                    Debug.Log("[ReferenceManager] Starting initialization...");
                }

                _isShuttingDown = false;

                // Cache the settings module for debug-logging gating. The generator is
                // static and stateless, so there is nothing further to configure.
                _settings = ReferenceManagerSettings.Instance;
                if (_settings == null && IsDebugEnabled)
                {
                    Debug.LogWarning("[ReferenceManager] ReferenceManagerSettings not available");
                }

                // ReferenceGenerator is now stateless and doesn't need initialization

                if (IsDebugEnabled)
                {
                    Debug.Log($"[ReferenceManager] Initialized with {_byKey.Count} existing references");
                }

                // Mark as ready
                finishCallback?.Invoke(this);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceManager] Initialization failed: {e.Message}");
                finishCallback?.Invoke(this);
            }
        }

        /// <summary>
        /// Clears all registered references on shutdown.
        /// </summary>
        public override void Teardown()
        {
            if (IsDebugEnabled)
                Debug.Log("[ReferenceManager] Teardown");

            // Refuse further registrations before dropping the entries. A provider whose OnDisable
            // runs during teardown would otherwise re-enter a half-cleared registry.
            _isShuttingDown = true;
            ClearAll();

            // Drop every subscription too: a stale handler on a torn-down subsystem keeps its owner
            // alive and would fire against a registry that no longer means anything.
            Registered = null;
            Unregistered = null;
            KeyRegistered = null;
            KeyUnregistered = null;

            // Drop the singleton so a torn-down subsystem can't be resolved again;
            // the Instance getter re-resolves through RuntimeManager on next access.
            if (_instance == this)
                _instance = null;

            base.Teardown();
        }

        #region Registration

        /// <summary>
        /// Register an IReferenceable object with the manager.
        /// </summary>
        /// <param name="referenceable">The object to register.</param>
        /// <returns>True if registration was successful, false otherwise.</returns>
        /// <remarks>
        /// The v1 entry point. It adapts to a <see cref="ReferenceScopeKind.LegacyGlobal"/> key, so
        /// existing callers and existing serialized data keep their project-wide uniqueness
        /// semantics unchanged. Prefer
        /// <see cref="Register(IReferenceable, ReferenceRuntimeKey, out ReferenceRegistrationHandle)"/>,
        /// which says which scope it means and reports why a registration was refused.
        /// </remarks>
        public bool Register(IReferenceable referenceable)
        {
            if (referenceable == null)
            {
                Debug.LogError("[ReferenceManager] Cannot register null referenceable object");
                return false;
            }

            if (string.IsNullOrEmpty(referenceable.RefId) ||
                string.IsNullOrEmpty(referenceable.RefType))
            {
                Debug.LogError($"[ReferenceManager] Cannot register object with invalid reference data: {referenceable}");
                return false;
            }

            var key = ReferenceRuntimeKey.Legacy(referenceable.RefType, referenceable.RefId);
            var result = RegisterCore(referenceable, key, logLegacyDiagnostics: true);
            return result.IsRegistered;
        }

        /// <summary>
        /// Register a provider under an explicit scoped key, reporting exactly what happened.
        /// </summary>
        /// <param name="referenceable">The object to register.</param>
        /// <param name="key">The full scoped identity to take.</param>
        /// <param name="handle">
        /// On success, the handle owning this registration; disposing it releases exactly this
        /// entry. Null when the registration was refused.
        /// </param>
        /// <returns>The outcome, the key, and the conflicting holder's name when refused.</returns>
        public ReferenceRegistrationResult Register(
            IReferenceable referenceable,
            ReferenceRuntimeKey key,
            out ReferenceRegistrationHandle handle)
        {
            var result = Register(referenceable, key);
            handle = result.Handle;
            return result;
        }

        /// <summary>
        /// Register a provider under an explicit scoped key, reporting exactly what happened.
        /// </summary>
        /// <param name="referenceable">The object to register.</param>
        /// <param name="key">The full scoped identity to take.</param>
        /// <returns>The outcome, including the owning handle on success.</returns>
        public ReferenceRegistrationResult Register(IReferenceable referenceable, ReferenceRuntimeKey key) =>
            RegisterCore(referenceable, key, logLegacyDiagnostics: false);

        /// <summary>
        /// The single registration path. Every public overload funnels through here so the outcome
        /// rules exist in exactly one place.
        /// </summary>
        /// <param name="logLegacyDiagnostics">
        /// True for the v1 <see cref="Register(IReferenceable)"/> entry point, which reports
        /// conflicts and no-ops through the console because its <c>bool</c> return cannot. The
        /// scoped overloads return the same information in the result and stay quiet.
        /// </param>
        private ReferenceRegistrationResult RegisterCore(
            IReferenceable referenceable, ReferenceRuntimeKey key, bool logLegacyDiagnostics)
        {
            if (_isShuttingDown)
            {
                Diagnostics.Record(ReferenceDiagnosticKind.InvalidRegistration, key, SafeDisplayName(referenceable), "registry shutting down");
                return new ReferenceRegistrationResult(ReferenceRegistrationOutcome.RegistryShuttingDown, key);
            }

            if (referenceable == null || IsDestroyed(referenceable))
            {
                Diagnostics.Record(ReferenceDiagnosticKind.InvalidRegistration, key, SafeDisplayName(referenceable), "provider null or destroyed");
                return new ReferenceRegistrationResult(ReferenceRegistrationOutcome.InvalidProvider, key);
            }

            if (!key.IsValid)
            {
                Diagnostics.Record(ReferenceDiagnosticKind.InvalidRegistration, key, SafeDisplayName(referenceable), "incomplete key");
                return new ReferenceRegistrationResult(ReferenceRegistrationOutcome.InvalidKey, key);
            }

            // A prefab-local id only means anything inside a live scope. Accepting one whose scope
            // root is gone would let a local id sit in the registry as if it were project-wide.
            if (key.ScopeKind == ReferenceScopeKind.PrefabLocal && !_openScopes.Contains(key.ScopeId))
            {
                Diagnostics.Record(ReferenceDiagnosticKind.InvalidRegistration, key, SafeDisplayName(referenceable), "scope is not open");
                return new ReferenceRegistrationResult(ReferenceRegistrationOutcome.WrongScope, key);
            }

            // Global and LegacyGlobal are distinct keys that make the same project-wide claim on one
            // (RefType, RefId). Letting both in would leave the v1 index able to name only one of
            // them, so the second is a conflict even though its full key is free.
            if (!_byKey.ContainsKey(key) &&
                key.TryToLegacyId(out var claimedLegacyId) &&
                _byLegacyId.TryGetValue(claimedLegacyId, out var legacyIncumbent) &&
                !ReferenceEquals(legacyIncumbent.Provider, referenceable) &&
                !PurgeIfDestroyed(legacyIncumbent.Provider))
            {
                string legacyHolder = SafeDisplayName(legacyIncumbent.Provider);
                Diagnostics.Record(
                    ReferenceDiagnosticKind.RegistrationConflict, key, SafeDisplayName(referenceable),
                    $"'{claimedLegacyId}' held by '{legacyHolder}' as {legacyIncumbent.Key.ScopeKind}");

                if (logLegacyDiagnostics)
                    Debug.LogError($"[ReferenceManager] Reference ID conflict: {key.RefType}:{key.RefId} is already used by '{legacyHolder}'; cannot register '{SafeDisplayName(referenceable)}'.");

                return new ReferenceRegistrationResult(
                    ReferenceRegistrationOutcome.DuplicateKey, key, null, legacyHolder);
            }

            if (_byKey.TryGetValue(key, out var incumbent))
            {
                if (ReferenceEquals(incumbent.Provider, referenceable))
                {
                    if (logLegacyDiagnostics)
                        Debug.LogWarning($"[ReferenceManager] Object already registered: {key.RefType}:{key.RefId}");

                    return new ReferenceRegistrationResult(
                        ReferenceRegistrationOutcome.AlreadyRegisteredSameKey, key, incumbent.Handle);
                }

                // A destroyed incumbent is not a conflict — it is a stale entry that never
                // unregistered. Purging it lets a legitimate replacement (a respawned object, or the
                // next prefab instance) take the key, rather than being refused by a dead one.
                if (!PurgeIfDestroyed(incumbent.Provider))
                {
                    // True collision: a different live object already holds this key. Keep the
                    // incumbent and refuse the newcomer, so which provider wins is decided by the
                    // key rather than by load order.
                    string holder = SafeDisplayName(incumbent.Provider);
                    Diagnostics.Record(
                        ReferenceDiagnosticKind.RegistrationConflict, key, SafeDisplayName(referenceable),
                        $"held by '{holder}'");

                    if (logLegacyDiagnostics)
                        Debug.LogError($"[ReferenceManager] Reference ID conflict: {key.RefType}:{key.RefId} is already used by '{holder}'; cannot register '{SafeDisplayName(referenceable)}'.");

                    return new ReferenceRegistrationResult(
                        ReferenceRegistrationOutcome.DuplicateKey, key, null, holder);
                }
            }

            // Re-key: the same object is already registered under a different key (e.g. its RefId
            // changed after the first Register). Drop the stale entry instead of orphaning it.
            bool rekeyed = false;
            if (_byProvider.TryGetValue(referenceable, out var previous) && previous.Key != key)
            {
                RemoveFromIndexes(previous);
                previous.Handle?.MarkReleased();
                rekeyed = true;

                if (IsDebugEnabled)
                    Debug.Log($"[ReferenceManager] Re-keyed: {previous.Key} -> {key}");
            }

            var entry = new Entry { Provider = referenceable, Key = key };
            entry.Handle = new ReferenceRegistrationHandle(this, key, referenceable);
            AddToIndexes(entry);

            if (IsDebugEnabled)
                Debug.Log($"[ReferenceManager] Registered: {key}");

            Diagnostics.Record(ReferenceDiagnosticKind.Registered, key, SafeDisplayName(referenceable));

            Raise(Registered, referenceable, nameof(Registered));
            RaiseKey(KeyRegistered, key, referenceable, nameof(KeyRegistered));

            return new ReferenceRegistrationResult(
                rekeyed ? ReferenceRegistrationOutcome.RekeyRequired : ReferenceRegistrationOutcome.Registered,
                key,
                entry.Handle);
        }

        /// <summary>
        /// Unregister an IReferenceable object from the manager.
        /// </summary>
        /// <param name="referenceable">The object to unregister.</param>
        /// <returns>True if unregistration was successful, false otherwise.</returns>
        public bool Unregister(IReferenceable referenceable)
        {
            if (referenceable == null)
            {
                Debug.LogError("[ReferenceManager] Cannot unregister null referenceable object");
                return false;
            }

            if (!_byProvider.TryGetValue(referenceable, out var entry))
            {
                Debug.LogWarning($"[ReferenceManager] Object not registered: {referenceable}");
                return false;
            }

            RemoveEntry(entry);
            return true;
        }

        /// <summary>
        /// Release the registration owned by <paramref name="handle"/>, and only that one.
        /// </summary>
        /// <remarks>
        /// Keyed on the handle's captured identity rather than the provider's current
        /// <see cref="IReferenceable.RefId"/>: a provider re-keyed since this handle was issued must
        /// not have its <i>current</i> entry torn down by a stale handle.
        /// </remarks>
        internal void ReleaseHandle(ReferenceRegistrationHandle handle)
        {
            if (handle == null)
                return;

            if (_byKey.TryGetValue(handle.Key, out var entry) && ReferenceEquals(entry.Handle, handle))
                RemoveEntry(entry);
            else
                handle.MarkReleased();
        }

        /// <summary>Drops an entry from every index and announces it.</summary>
        private void RemoveEntry(Entry entry)
        {
            RemoveFromIndexes(entry);
            entry.Handle?.MarkReleased();

            if (IsDebugEnabled)
                Debug.Log($"[ReferenceManager] Unregistered: {entry.Key}");

            Diagnostics.Record(ReferenceDiagnosticKind.Unregistered, entry.Key, SafeDisplayName(entry.Provider));

            Raise(Unregistered, entry.Provider, nameof(Unregistered));
            RaiseKey(KeyUnregistered, entry.Key, entry.Provider, nameof(KeyUnregistered));
        }

        /// <summary>
        /// Adds an entry to the authoritative map and every derived index.
        /// </summary>
        /// <remarks>
        /// Paired with <see cref="RemoveFromIndexes"/>. Nothing else writes to the indexes, which is
        /// what keeps the v1 views from drifting away from the scoped registry behind them.
        /// </remarks>
        private void AddToIndexes(Entry entry)
        {
            _byKey[entry.Key] = entry;
            _byProvider[entry.Provider] = entry;

            if (!entry.Key.TryToLegacyId(out var legacyId))
                return;

            _byLegacyId[legacyId] = entry;

            if (!_typeRegistries.TryGetValue(legacyId.Type, out var typeRegistry))
            {
                typeRegistry = new Dictionary<string, IReferenceable>();
                _typeRegistries[legacyId.Type] = typeRegistry;
            }

            typeRegistry[legacyId.Id] = entry.Provider;
        }

        /// <summary>Removes an entry from the authoritative map and every derived index.</summary>
        private void RemoveFromIndexes(Entry entry)
        {
            _byKey.Remove(entry.Key);

            // Only if this entry still owns the reverse slot: during a re-key the newer entry may
            // already have claimed it, and dropping it here would orphan the live registration.
            if (_byProvider.TryGetValue(entry.Provider, out var owner) && ReferenceEquals(owner, entry))
                _byProvider.Remove(entry.Provider);

            if (!entry.Key.TryToLegacyId(out var legacyId))
                return;

            if (_byLegacyId.TryGetValue(legacyId, out var legacyOwner) && ReferenceEquals(legacyOwner, entry))
                _byLegacyId.Remove(legacyId);

            if (_typeRegistries.TryGetValue(legacyId.Type, out var typeRegistry))
            {
                if (typeRegistry.TryGetValue(legacyId.Id, out var holder) && ReferenceEquals(holder, entry.Provider))
                    typeRegistry.Remove(legacyId.Id);

                if (typeRegistry.Count == 0)
                    _typeRegistries.Remove(legacyId.Type);
            }
        }

        /// <summary>
        /// Invoke an event's handlers in isolation: a throwing handler is logged and
        /// does not stop the remaining handlers.
        /// </summary>
        private static void Raise(Action<IReferenceable> evt, IReferenceable referenceable, string eventName)
        {
            if (evt == null)
                return;

            foreach (var handler in evt.GetInvocationList())
            {
                try
                {
                    ((Action<IReferenceable>)handler).Invoke(referenceable);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ReferenceManager] {eventName} handler threw: {e}");
                }
            }
        }

        /// <summary>Key-carrying counterpart of <see cref="Raise"/>, isolated the same way.</summary>
        private static void RaiseKey(
            Action<ReferenceRuntimeKey, IReferenceable> evt,
            ReferenceRuntimeKey key,
            IReferenceable referenceable,
            string eventName)
        {
            if (evt == null)
                return;

            foreach (var handler in evt.GetInvocationList())
            {
                try
                {
                    ((Action<ReferenceRuntimeKey, IReferenceable>)handler).Invoke(key, referenceable);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ReferenceManager] {eventName} handler threw: {e}");
                }
            }
        }

        /// <summary>
        /// Drop a registered entry whose backing <see cref="UnityEngine.Object"/> has been
        /// destroyed (Unity fake-null). Resolution paths call this to self-heal when a
        /// referenced scene object was destroyed without unregistering, so a dead entry is
        /// never handed back to a caller.
        /// </summary>
        /// <param name="referenceable">The (possibly destroyed) entry to test and purge.</param>
        /// <returns>True if a destroyed entry was found and purged; false otherwise.</returns>
        internal bool PurgeIfDestroyed(IReferenceable referenceable)
        {
            if (referenceable == null || !IsDestroyed(referenceable))
                return false;

            if (!_byProvider.TryGetValue(referenceable, out var entry))
                return false;

            RemoveFromIndexes(entry);
            entry.Handle?.MarkReleased();
            Diagnostics.Record(ReferenceDiagnosticKind.DestroyedEntryPurged, entry.Key);

            // Deliberately silent on the public events: a purge is the registry repairing itself
            // after a provider failed to unregister, not an authored lifecycle transition, and
            // waking every resolver for it would be noise.
            return true;
        }

        /// <summary>
        /// True when <paramref name="referenceable"/> is a destroyed <see cref="UnityEngine.Object"/>
        /// (Unity fake-null) that is still sitting in the registries.
        /// </summary>
        private static bool IsDestroyed(IReferenceable referenceable) =>
            referenceable is UnityEngine.Object uo && uo == null;

        /// <summary>
        /// Purges <paramref name="referenceable"/> and nulls it out when its Unity object was destroyed.
        /// </summary>
        /// <returns>True when the entry was destroyed and has been rejected.</returns>
        /// <remarks>
        /// Every public lookup runs through this, so <see cref="Get(ReferenceId)"/> and
        /// <see cref="TryGet(ReferenceId, out IReferenceable)"/> are exactly as safe as
        /// <see cref="SceneObjectReference.Resolve{T}()"/>. They were not: a target destroyed without
        /// unregistering (a common Destroy-during-teardown case) was handed straight back to the caller,
        /// which then dereferenced a fake-null object.
        /// </remarks>
        private bool RejectIfDestroyed(ref IReferenceable referenceable)
        {
            if (!IsDestroyed(referenceable))
                return false;

            PurgeIfDestroyed(referenceable);
            referenceable = null;
            return true;
        }

        /// <summary>
        /// Best-effort display name that never throws, even if <paramref name="referenceable"/>
        /// is a destroyed <see cref="UnityEngine.Object"/> whose <see cref="IReferenceable.DisplayName"/>
        /// would dereference native state.
        /// </summary>
        private static string SafeDisplayName(IReferenceable referenceable)
        {
            if (referenceable == null)
                return "<null>";

            try
            {
                if (referenceable is UnityEngine.Object uo && uo == null)
                    return "<destroyed>";
                return referenceable.DisplayName ?? referenceable.RefId;
            }
            catch
            {
                return "<unavailable>";
            }
        }

        /// <summary>
        /// Register an object with automatic ID generation if needed.
        /// </summary>
        /// <param name="referenceable">The object to register.</param>
        /// <param name="referenceType">The type identifier to use.</param>
        /// <param name="generateId">Whether to generate an ID if the object doesn't have one.</param>
        /// <returns>True if registration was successful, false otherwise.</returns>
        /// <remarks>
        /// This overload cannot assign an id to the object — id generation is the
        /// concrete class's responsibility — so it returns <c>false</c> whenever
        /// generation would be required. Assign the id yourself with
        /// <see cref="ReferenceGenerator.GenerateUniqueId"/> and call
        /// <see cref="Register(IReferenceable)"/> directly.
        /// </remarks>
        [Obsolete("Cannot set ids on the target; returns false when generation is needed. Assign via ReferenceGenerator.GenerateUniqueId then call Register. Removed next major.")]
        public bool RegisterWithAutoId(IReferenceable referenceable, string referenceType, bool generateId = true)
        {
            if (referenceable == null)
            {
                Debug.LogError("[ReferenceManager] Cannot register null referenceable object");
                return false;
            }

            if (string.IsNullOrEmpty(referenceType))
            {
                Debug.LogError("[ReferenceManager] Reference type cannot be null or empty");
                return false;
            }

            // If the object doesn't have a valid reference ID, generate one
            if (generateId && (string.IsNullOrEmpty(referenceable.RefId) ||
                              string.IsNullOrEmpty(referenceable.RefType)))
            {
                // For objects that support setting IDs, we would need to generate and set the ID
                // This is handled by the concrete implementation
                Debug.Log($"[ReferenceManager] Object {referenceable} needs ID generation - this should be handled by the concrete class");
                return false;
            }

            return Register(referenceable);
        }

        #endregion

        #region Scopes

        /// <summary>
        /// Announce a live scope instance, so prefab-local registrations naming it are accepted.
        /// </summary>
        /// <param name="scopeInstanceId">The runtime scope id, from <see cref="ReferenceScopeRoot"/>.</param>
        /// <returns>False when the id was empty or already open.</returns>
        internal bool OpenScope(string scopeInstanceId) =>
            !string.IsNullOrEmpty(scopeInstanceId) && _openScopes.Add(scopeInstanceId);

        /// <summary>
        /// Close a scope instance and drop every registration inside it.
        /// </summary>
        /// <remarks>
        /// Entries are removed with the scope rather than left for their own <c>OnDisable</c>: a
        /// destroyed prefab instance's children may never get one, and a stale local entry would
        /// then block the next instance that legitimately reuses the same scope id.
        /// </remarks>
        /// <param name="scopeInstanceId">The runtime scope id to close.</param>
        /// <returns>How many registrations were dropped.</returns>
        internal int CloseScope(string scopeInstanceId)
        {
            if (string.IsNullOrEmpty(scopeInstanceId) || !_openScopes.Remove(scopeInstanceId))
                return 0;

            // Materialise before mutating: RemoveEntry writes to the dictionary being read.
            var inScope = _byKey.Values
                .Where(e => e.Key.ScopeKind == ReferenceScopeKind.PrefabLocal &&
                            string.Equals(e.Key.ScopeId, scopeInstanceId, StringComparison.Ordinal))
                .ToList();

            foreach (var entry in inScope)
                RemoveEntry(entry);

            return inScope.Count;
        }

        /// <summary>True when a scope instance is currently open.</summary>
        /// <param name="scopeInstanceId">The runtime scope id to test.</param>
        public bool IsScopeOpen(string scopeInstanceId) =>
            !string.IsNullOrEmpty(scopeInstanceId) && _openScopes.Contains(scopeInstanceId);

        /// <summary>How many scope instances are currently open.</summary>
        public int OpenScopeCount => _openScopes.Count;

        #endregion

        #region Lookup Methods

        /// <summary>
        /// Get a referenceable object by its reference ID.
        /// </summary>
        /// <param name="referenceId">The reference ID to look up.</param>
        /// <returns>The referenceable object, or null if not found or destroyed.</returns>
        public IReferenceable Get(ReferenceId referenceId)
        {
            TryGet(referenceId, out var referenceable);
            return referenceable;
        }

        /// <summary>
        /// Get a referenceable object by its type and ID.
        /// </summary>
        /// <param name="referenceType">The type identifier.</param>
        /// <param name="referenceId">The reference ID.</param>
        /// <returns>The referenceable object, or null if not found or destroyed.</returns>
        public IReferenceable Get(string referenceType, string referenceId)
        {
            TryGet(referenceType, referenceId, out var referenceable);
            return referenceable;
        }

        /// <summary>Get a provider by its full scoped key.</summary>
        /// <param name="key">The scoped key to look up.</param>
        /// <returns>The provider, or null if not found or destroyed.</returns>
        public IReferenceable Get(ReferenceRuntimeKey key)
        {
            TryGet(key, out var referenceable);
            return referenceable;
        }

        /// <summary>
        /// Try to get a referenceable object by its reference ID.
        /// </summary>
        /// <param name="referenceId">The reference ID to look up.</param>
        /// <param name="referenceable">The found object, or null if not found or destroyed.</param>
        /// <returns>True if a live object was found, false otherwise.</returns>
        public bool TryGet(ReferenceId referenceId, out IReferenceable referenceable)
        {
            referenceable = _byLegacyId.TryGetValue(referenceId, out var entry) ? entry.Provider : null;
            return referenceable != null && !RejectIfDestroyed(ref referenceable);
        }

        /// <summary>
        /// Try to get a referenceable object by its type and ID.
        /// </summary>
        /// <param name="referenceType">The type identifier.</param>
        /// <param name="referenceId">The reference ID.</param>
        /// <param name="referenceable">The found object, or null if not found or destroyed.</param>
        /// <returns>True if a live object was found, false otherwise.</returns>
        public bool TryGet(string referenceType, string referenceId, out IReferenceable referenceable)
        {
            referenceable = null;

            if (referenceType == null || !_typeRegistries.TryGetValue(referenceType, out var typeRegistry))
                return false;

            return referenceId != null
                && typeRegistry.TryGetValue(referenceId, out referenceable)
                && !RejectIfDestroyed(ref referenceable);
        }

        /// <summary>
        /// Try to get a provider by its full scoped key.
        /// </summary>
        /// <param name="key">The scoped key to look up.</param>
        /// <param name="referenceable">The found provider, or null if not found or destroyed.</param>
        /// <returns>True if a live provider holds the key.</returns>
        /// <remarks>
        /// The exact path, and the only one that can distinguish two prefab instances carrying the
        /// same local id. No fallback: a key that does not match is a miss, not an invitation to
        /// guess.
        /// </remarks>
        public bool TryGet(ReferenceRuntimeKey key, out IReferenceable referenceable)
        {
            referenceable = key.IsValid && _byKey.TryGetValue(key, out var entry) ? entry.Provider : null;
            return referenceable != null && !RejectIfDestroyed(ref referenceable);
        }

        /// <summary>
        /// Try to find a registered object by <paramref name="referenceId"/> across all reference types.
        /// Use when serialized refType may be stale (e.g. the reference type on <see cref="ReferenceableComponent"/> was changed after assigning a <see cref="SceneObjectReference"/>).
        /// </summary>
        /// <remarks>
        /// Compatibility path only, and global-scope only: a scoped provider is not addressable by a
        /// bare id, so it never participates. It fails rather than guessing when more than one
        /// <i>live</i> provider carries the id — a destroyed entry that has not unregistered must not
        /// make a legitimately unambiguous id look ambiguous, so dead entries are purged before the
        /// ambiguity test.
        /// </remarks>
        public bool TryGetByRefIdOnly(string referenceId, out IReferenceable referenceable)
        {
            referenceable = null;
            if (string.IsNullOrEmpty(referenceId))
                return false;

            // Destroyed entries are collected and purged after the walk: RejectIfDestroyed mutates the
            // registries, which cannot happen while enumerating them.
            List<IReferenceable> destroyed = null;
            var ambiguous = false;

            foreach (var typeRegistry in _typeRegistries.Values)
            {
                if (!typeRegistry.TryGetValue(referenceId, out var found))
                    continue;

                if (IsDestroyed(found))
                {
                    (destroyed ??= new List<IReferenceable>()).Add(found);
                    continue;
                }

                if (referenceable != null && !ReferenceEquals(referenceable, found))
                {
                    ambiguous = true;
                    break;
                }

                referenceable = found;
            }

            if (destroyed != null)
            {
                foreach (var dead in destroyed)
                    PurgeIfDestroyed(dead);
            }

            if (ambiguous)
            {
                Diagnostics.Record(ReferenceDiagnosticKind.AmbiguousFallback, string.Empty, null, $"refId '{referenceId}' under multiple types");
                Debug.LogWarning($"[ReferenceManager] Ambiguous refId '{referenceId}': found under multiple reference types; cannot resolve by id alone.");
                referenceable = null;
                return false;
            }

            return referenceable != null;
        }

        /// <summary>
        /// Get the reference ID for a registered object.
        /// </summary>
        /// <param name="referenceable">The referenceable object.</param>
        /// <returns>The reference ID, or ReferenceId.Invalid if not found.</returns>
        /// <remarks>
        /// Returns <see cref="ReferenceId.Invalid"/> for a provider registered under a scoped key:
        /// its id is unique only within its scope, and a v1 <see cref="ReferenceId"/> cannot express
        /// that. Use <see cref="TryGetKey"/> for the full identity.
        /// </remarks>
        public ReferenceId GetReferenceId(IReferenceable referenceable)
        {
            if (referenceable == null || !_byProvider.TryGetValue(referenceable, out var entry))
                return ReferenceId.Invalid;

            return entry.Key.TryToLegacyId(out var legacyId) ? legacyId : ReferenceId.Invalid;
        }

        /// <summary>Get the full scoped key a provider is registered under.</summary>
        /// <param name="referenceable">The registered provider.</param>
        /// <param name="key">The key it holds, when this returns true.</param>
        /// <returns>True when the provider holds a registration.</returns>
        public bool TryGetKey(IReferenceable referenceable, out ReferenceRuntimeKey key)
        {
            if (referenceable != null && _byProvider.TryGetValue(referenceable, out var entry))
            {
                key = entry.Key;
                return true;
            }

            key = default;
            return false;
        }

        /// <summary>
        /// Check if a reference ID is registered to a live object.
        /// </summary>
        /// <param name="referenceId">The reference ID to check.</param>
        /// <returns>True if a live object holds the reference ID, false otherwise.</returns>
        public bool IsRegistered(ReferenceId referenceId)
        {
            return TryGet(referenceId, out _);
        }

        /// <summary>Check if a scoped key is held by a live provider.</summary>
        /// <param name="key">The key to check.</param>
        public bool IsRegistered(ReferenceRuntimeKey key)
        {
            return TryGet(key, out _);
        }

        /// <summary>
        /// Check if an object is registered.
        /// </summary>
        /// <param name="referenceable">The referenceable object to check.</param>
        /// <returns>True if the object is registered and not destroyed, false otherwise.</returns>
        public bool IsRegistered(IReferenceable referenceable)
        {
            return referenceable != null
                && !IsDestroyed(referenceable)
                && _byProvider.ContainsKey(referenceable);
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Get all referenceable objects of a specific type.
        /// </summary>
        /// <param name="referenceType">The type identifier to filter by.</param>
        /// <returns>A list of all live referenceable objects of the specified type.</returns>
        /// <remarks>
        /// Destroyed entries are filtered out and purged, so a caller iterating the result cannot
        /// dereference a fake-null object. Global-scope entries only, matching the rest of the v1
        /// surface.
        /// </remarks>
        public List<IReferenceable> GetAllOfType(string referenceType)
        {
            if (referenceType == null || !_typeRegistries.TryGetValue(referenceType, out var typeRegistry))
                return new List<IReferenceable>();

            var live = new List<IReferenceable>(typeRegistry.Count);
            List<IReferenceable> destroyed = null;

            foreach (var candidate in typeRegistry.Values)
            {
                if (IsDestroyed(candidate))
                    (destroyed ??= new List<IReferenceable>()).Add(candidate);
                else
                    live.Add(candidate);
            }

            // Purge after the walk: PurgeIfDestroyed mutates the registry being enumerated.
            if (destroyed != null)
            {
                foreach (var dead in destroyed)
                    PurgeIfDestroyed(dead);
            }

            return live;
        }

        /// <summary>
        /// Get every live provider registered inside one scope.
        /// </summary>
        /// <param name="scopeKind">The scope kind to filter by.</param>
        /// <param name="scopeId">The scope instance id; ignored for the global kinds.</param>
        /// <returns>The live providers in that scope.</returns>
        public List<IReferenceable> GetAllInScope(ReferenceScopeKind scopeKind, string scopeId = null)
        {
            var live = new List<IReferenceable>();
            List<IReferenceable> destroyed = null;
            bool global = scopeKind == ReferenceScopeKind.Global || scopeKind == ReferenceScopeKind.LegacyGlobal;

            foreach (var entry in _byKey.Values)
            {
                if (entry.Key.ScopeKind != scopeKind)
                    continue;

                if (!global && !string.Equals(entry.Key.ScopeId, scopeId, StringComparison.Ordinal))
                    continue;

                if (IsDestroyed(entry.Provider))
                    (destroyed ??= new List<IReferenceable>()).Add(entry.Provider);
                else
                    live.Add(entry.Provider);
            }

            if (destroyed != null)
            {
                foreach (var dead in destroyed)
                    PurgeIfDestroyed(dead);
            }

            return live;
        }

        /// <summary>
        /// Get all registered reference IDs.
        /// </summary>
        /// <returns>A list of all registered reference IDs.</returns>
        /// <remarks>Global-scope entries only; a scoped key has no v1 <see cref="ReferenceId"/>.</remarks>
        public List<ReferenceId> GetAllReferenceIds()
        {
            return _byLegacyId.Keys.ToList();
        }

        /// <summary>Get every scoped key currently held.</summary>
        public List<ReferenceRuntimeKey> GetAllKeys()
        {
            return _byKey.Keys.ToList();
        }

        /// <summary>
        /// Get all registered reference IDs of a specific type.
        /// </summary>
        /// <param name="referenceType">The type identifier to filter by.</param>
        /// <returns>A list of all registered reference IDs of the specified type.</returns>
        public List<ReferenceId> GetAllReferenceIdsOfType(string referenceType)
        {
            if (referenceType != null && _typeRegistries.TryGetValue(referenceType, out var typeRegistry))
            {
                return typeRegistry.Keys.Select(id => new ReferenceId(id, referenceType)).ToList();
            }
            return new List<ReferenceId>();
        }

        /// <summary>
        /// Get all registered types.
        /// </summary>
        /// <returns>A list of all registered reference types.</returns>
        public List<string> GetAllTypes()
        {
            return _typeRegistries.Keys.ToList();
        }

        /// <summary>
        /// Get statistics about registered references.
        /// </summary>
        /// <returns>A dictionary mapping reference types to their object counts.</returns>
        public Dictionary<string, int> GetRegistrationStats()
        {
            var stats = new Dictionary<string, int>();
            foreach (var kvp in _typeRegistries)
            {
                stats[kvp.Key] = kvp.Value.Count;
            }
            return stats;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Clear all registrations (useful for testing or resetting).
        /// </summary>
        public void ClearAll()
        {
            // Spend every handle first: a caller holding one after a ClearAll must not be able to
            // tear down a registration made by whatever runs next.
            foreach (var entry in _byKey.Values)
                entry.Handle?.MarkReleased();

            _byKey.Clear();
            _byLegacyId.Clear();
            _typeRegistries.Clear();
            _byProvider.Clear();
            _openScopes.Clear();

            if (IsDebugEnabled)
            {
                Debug.Log("[ReferenceManager] Cleared all registrations");
            }
        }

        /// <summary>
        /// Get the total number of registered references.
        /// </summary>
        public int Count => _byKey.Count;

        /// <summary>
        /// Get the number of registered references for a specific type.
        /// </summary>
        /// <param name="referenceType">The type identifier.</param>
        /// <returns>The number of registered references of the specified type.</returns>
        public int GetCountOfType(string referenceType)
        {
            return referenceType != null && _typeRegistries.TryGetValue(referenceType, out var typeRegistry)
                ? typeRegistry.Count
                : 0;
        }

        #endregion

        #region Settings Integration

        /// <summary>
        /// Check if debug logging is enabled through settings.
        /// </summary>
        private bool IsDebugEnabled => _settings?.EnableDebugLogging ?? true;

        /// <summary>
        /// Reference to the settings module.
        /// </summary>
        private ReferenceManagerSettings _settings;

        /// <summary>
        /// Get the ReferenceManager from RuntimeManager.
        /// This is the preferred way to access the ReferenceManager when using RuntimeSubsystem integration.
        /// </summary>
        public static ReferenceManager GetFromRuntimeManager()
        {
            return RuntimeManager.GetSubsystem<ReferenceManager>();
        }

        /// <summary>
        /// Check if ReferenceManager is properly integrated with RuntimeManager.
        /// </summary>
        public static bool IsRuntimeManagerIntegrated => RuntimeManager.GetSubsystem<ReferenceManager>() != null;

        #endregion
    }
}
