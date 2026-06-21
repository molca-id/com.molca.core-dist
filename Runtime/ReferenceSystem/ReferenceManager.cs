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
    public class ReferenceManager : RuntimeSubsystem
    {
        private static ReferenceManager _instance;
        private static readonly object _lock = new object();

        // Main registry: ReferenceId -> Object
        private readonly Dictionary<ReferenceId, IReferenceable> _references = new Dictionary<ReferenceId, IReferenceable>();

        // Type-specific registries for faster lookups
        private readonly Dictionary<string, Dictionary<string, IReferenceable>> _typeRegistries = new Dictionary<string, Dictionary<string, IReferenceable>>();

        // Reverse lookup: Object -> ReferenceId
        private readonly Dictionary<IReferenceable, ReferenceId> _objectToReference = new Dictionary<IReferenceable, ReferenceId>();

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
        /// Get the singleton instance of the ReferenceManager.
        /// </summary>
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
                            // Try to get from RuntimeManager first
                            _instance = RuntimeManager.GetSubsystem<ReferenceManager>();

                            if (_instance == null)
                            {
                                // Fallback to FindObjectOfType for compatibility
                                _instance = FindFirstObjectByType<ReferenceManager>();
                            }

                            if (_instance == null)
                            {
                                Debug.LogWarning("[ReferenceManager] No instance found. Make sure ReferenceManager is added to RuntimeManager or scene.");
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
                    Debug.Log($"[ReferenceManager] Initialized with {_references.Count} existing references");
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

            var referenceId = new ReferenceId(referenceable.RefId, referenceable.RefType);

            // Check if already registered
            if (_references.ContainsKey(referenceId))
            {
                var existing = _references[referenceId];
                if (existing == referenceable)
                {
                    Debug.LogWarning($"[ReferenceManager] Object already registered: {referenceId}");
                    return true;
                }

                // True collision: a different object already holds this id. Keep the
                // existing registration (return false) but surface it loudly with both
                // display names — this is the runtime signal for the prefab-duplicate
                // problem addressed structurally in a later sprint.
                Debug.LogError($"[ReferenceManager] Reference ID conflict: {referenceId} is already used by '{SafeDisplayName(existing)}'; cannot register '{SafeDisplayName(referenceable)}'.");
                return false;
            }

            // Re-key: the same object is already registered under a different id
            // (e.g. its RefId changed after the first Register). Drop the stale main
            // and type entries instead of orphaning them.
            if (_objectToReference.TryGetValue(referenceable, out var oldId) && oldId != referenceId)
            {
                RemoveFromRegistries(referenceable, oldId);
                if (IsDebugEnabled)
                {
                    Debug.Log($"[ReferenceManager] Re-keyed: {oldId} -> {referenceId}");
                }
            }

            // Register in main registry
            _references[referenceId] = referenceable;

            // Register in type-specific registry
            if (!_typeRegistries.ContainsKey(referenceable.RefType))
            {
                _typeRegistries[referenceable.RefType] = new Dictionary<string, IReferenceable>();
            }
            _typeRegistries[referenceable.RefType][referenceable.RefId] = referenceable;

            // Register reverse lookup
            _objectToReference[referenceable] = referenceId;

            if (IsDebugEnabled)
            {
                Debug.Log($"[ReferenceManager] Registered: {referenceId}");
            }

            Raise(Registered, referenceable, nameof(Registered));
            return true;
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

            if (!_objectToReference.TryGetValue(referenceable, out var referenceId))
            {
                Debug.LogWarning($"[ReferenceManager] Object not registered: {referenceable}");
                return false;
            }

            RemoveFromRegistries(referenceable, referenceId);

            if (IsDebugEnabled)
            {
                Debug.Log($"[ReferenceManager] Unregistered: {referenceId}");
            }

            Raise(Unregistered, referenceable, nameof(Unregistered));
            return true;
        }

        /// <summary>
        /// Remove an object from all three registries (main, type-specific, reverse).
        /// Keyed on the supplied <paramref name="referenceId"/> rather than the object's
        /// current <see cref="IReferenceable.RefType"/>/<see cref="IReferenceable.RefId"/>,
        /// so it works correctly during a re-key and for destroyed objects whose live
        /// properties can no longer be trusted.
        /// </summary>
        private void RemoveFromRegistries(IReferenceable referenceable, ReferenceId referenceId)
        {
            _references.Remove(referenceId);

            if (_typeRegistries.TryGetValue(referenceId.Type, out var typeRegistry))
            {
                typeRegistry.Remove(referenceId.Id);
                if (typeRegistry.Count == 0)
                {
                    _typeRegistries.Remove(referenceId.Type);
                }
            }

            _objectToReference.Remove(referenceable);
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
            if (referenceable is UnityEngine.Object uo && uo == null &&
                _objectToReference.TryGetValue(referenceable, out var referenceId))
            {
                RemoveFromRegistries(referenceable, referenceId);
                return true;
            }

            return false;
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

        #region Lookup Methods

        /// <summary>
        /// Get a referenceable object by its reference ID.
        /// </summary>
        /// <param name="referenceId">The reference ID to look up.</param>
        /// <returns>The referenceable object, or null if not found.</returns>
        public IReferenceable Get(ReferenceId referenceId)
        {
            return _references.TryGetValue(referenceId, out var referenceable) ? referenceable : null;
        }

        /// <summary>
        /// Get a referenceable object by its type and ID.
        /// </summary>
        /// <param name="referenceType">The type identifier.</param>
        /// <param name="referenceId">The reference ID.</param>
        /// <returns>The referenceable object, or null if not found.</returns>
        public IReferenceable Get(string referenceType, string referenceId)
        {
            if (_typeRegistries.TryGetValue(referenceType, out var typeRegistry))
            {
                return typeRegistry.TryGetValue(referenceId, out var referenceable) ? referenceable : null;
            }
            return null;
        }

        /// <summary>
        /// Try to get a referenceable object by its reference ID.
        /// </summary>
        /// <param name="referenceId">The reference ID to look up.</param>
        /// <param name="referenceable">The found object, or null if not found.</param>
        /// <returns>True if the object was found, false otherwise.</returns>
        public bool TryGet(ReferenceId referenceId, out IReferenceable referenceable)
        {
            return _references.TryGetValue(referenceId, out referenceable);
        }

        /// <summary>
        /// Try to get a referenceable object by its type and ID.
        /// </summary>
        /// <param name="referenceType">The type identifier.</param>
        /// <param name="referenceId">The reference ID.</param>
        /// <param name="referenceable">The found object, or null if not found.</param>
        /// <returns>True if the object was found, false otherwise.</returns>
        public bool TryGet(string referenceType, string referenceId, out IReferenceable referenceable)
        {
            if (_typeRegistries.TryGetValue(referenceType, out var typeRegistry))
            {
                return typeRegistry.TryGetValue(referenceId, out referenceable);
            }

            referenceable = null;
            return false;
        }

        /// <summary>
        /// Try to find a registered object by <paramref name="referenceId"/> across all reference types.
        /// Use when serialized refType may be stale (e.g. the reference type on <see cref="ReferenceableComponent"/> was changed after assigning a <see cref="SceneObjectReference"/>).
        /// </summary>
        public bool TryGetByRefIdOnly(string referenceId, out IReferenceable referenceable)
        {
            referenceable = null;
            if (string.IsNullOrEmpty(referenceId))
                return false;

            foreach (var typeRegistry in _typeRegistries.Values)
            {
                if (!typeRegistry.TryGetValue(referenceId, out var found))
                    continue;

                if (referenceable != null && !ReferenceEquals(referenceable, found))
                {
                    Debug.LogWarning($"[ReferenceManager] Ambiguous refId '{referenceId}': found under multiple reference types; cannot resolve by id alone.");
                    referenceable = null;
                    return false;
                }

                referenceable = found;
            }

            return referenceable != null;
        }

        /// <summary>
        /// Get the reference ID for a registered object.
        /// </summary>
        /// <param name="referenceable">The referenceable object.</param>
        /// <returns>The reference ID, or ReferenceId.Invalid if not found.</returns>
        public ReferenceId GetReferenceId(IReferenceable referenceable)
        {
            return _objectToReference.TryGetValue(referenceable, out var referenceId) ? referenceId : ReferenceId.Invalid;
        }

        /// <summary>
        /// Check if a reference ID is registered.
        /// </summary>
        /// <param name="referenceId">The reference ID to check.</param>
        /// <returns>True if the reference ID is registered, false otherwise.</returns>
        public bool IsRegistered(ReferenceId referenceId)
        {
            return _references.ContainsKey(referenceId);
        }

        /// <summary>
        /// Check if an object is registered.
        /// </summary>
        /// <param name="referenceable">The referenceable object to check.</param>
        /// <returns>True if the object is registered, false otherwise.</returns>
        public bool IsRegistered(IReferenceable referenceable)
        {
            return _objectToReference.ContainsKey(referenceable);
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Get all referenceable objects of a specific type.
        /// </summary>
        /// <param name="referenceType">The type identifier to filter by.</param>
        /// <returns>A list of all referenceable objects of the specified type.</returns>
        public List<IReferenceable> GetAllOfType(string referenceType)
        {
            if (_typeRegistries.TryGetValue(referenceType, out var typeRegistry))
            {
                return typeRegistry.Values.ToList();
            }
            return new List<IReferenceable>();
        }

        /// <summary>
        /// Get all registered reference IDs.
        /// </summary>
        /// <returns>A list of all registered reference IDs.</returns>
        public List<ReferenceId> GetAllReferenceIds()
        {
            return _references.Keys.ToList();
        }

        /// <summary>
        /// Get all registered reference IDs of a specific type.
        /// </summary>
        /// <param name="referenceType">The type identifier to filter by.</param>
        /// <returns>A list of all registered reference IDs of the specified type.</returns>
        public List<ReferenceId> GetAllReferenceIdsOfType(string referenceType)
        {
            if (_typeRegistries.TryGetValue(referenceType, out var typeRegistry))
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

            _references.Clear();
            _typeRegistries.Clear();
            _objectToReference.Clear();

            if (IsDebugEnabled)
            {
                Debug.Log("[ReferenceManager] Cleared all registrations");
            }
        }

        /// <summary>
        /// Get the total number of registered references.
        /// </summary>
        public int Count => _references.Count;

        /// <summary>
        /// Get the number of registered references for a specific type.
        /// </summary>
        /// <param name="referenceType">The type identifier.</param>
        /// <returns>The number of registered references of the specified type.</returns>
        public int GetCountOfType(string referenceType)
        {
            return _typeRegistries.TryGetValue(referenceType, out var typeRegistry) ? typeRegistry.Count : 0;
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
