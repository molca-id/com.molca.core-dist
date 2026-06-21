using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Generic tracker for managing referenceable objects of a specific type.
    /// Provides type-safe methods for registering, unregistering, and querying objects.
    /// </summary>
    /// <typeparam name="T">The type of referenceable object to track.</typeparam>
    [Obsolete("Parallel registry; use ReferenceManager directly. Removed next major.")]
    public class ReferenceTracker<T> where T : class, IReferenceable
    {
        private readonly string _referenceType;
        private readonly Dictionary<string, T> _trackedObjects = new Dictionary<string, T>();
        private readonly HashSet<T> _registeredObjects = new HashSet<T>();

        /// <summary>
        /// Event fired when an object is registered with this tracker.
        /// </summary>
        public event Action<T> OnObjectRegistered;

        /// <summary>
        /// Event fired when an object is unregistered from this tracker.
        /// </summary>
        public event Action<T> OnObjectUnregistered;

        /// <summary>
        /// Create a new ReferenceTracker for the specified type.
        /// </summary>
        /// <param name="referenceType">The reference type to track. If null, uses the type name of T.</param>
        public ReferenceTracker(string referenceType = null)
        {
            _referenceType = referenceType ?? typeof(T).Name;
        }

        /// <summary>
        /// Register an object with both the ReferenceManager and this tracker.
        /// </summary>
        /// <param name="obj">The object to register.</param>
        /// <returns>True if registration was successful, false otherwise.</returns>
        public bool Register(T obj)
        {
            if (obj == null)
            {
                Debug.LogError($"[ReferenceTracker<{typeof(T).Name}>] Cannot register null object");
                return false;
            }

            // Check if ReferenceManager is available
            var referenceManager = ReferenceManager.Instance;
            if (referenceManager == null)
            {
                Debug.LogWarning($"[ReferenceTracker<{typeof(T).Name}>] ReferenceManager not available, only local tracking will be used");
            }
            else
            {
                // Register with the central manager first
                if (!referenceManager.Register(obj))
                {
                    Debug.LogError($"[ReferenceTracker<{typeof(T).Name}>] Failed to register {obj} with ReferenceManager");
                    return false;
                }
            }

            // Add to our local tracking
            _trackedObjects[obj.RefId] = obj;
            _registeredObjects.Add(obj);

            // Fire the event
            try
            {
                OnObjectRegistered?.Invoke(obj);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceTracker<{typeof(T).Name}>] Error in OnObjectRegistered event: {e.Message}");
            }

            if (referenceManager != null)
            {
                Debug.Log($"[ReferenceTracker<{typeof(T).Name}>] Registered: {obj.RefId}");
            }
            return true;
        }

        /// <summary>
        /// Unregister an object from both the ReferenceManager and this tracker.
        /// </summary>
        /// <param name="obj">The object to unregister.</param>
        /// <returns>True if unregistration was successful, false otherwise.</returns>
        public bool Unregister(T obj)
        {
            if (obj == null)
            {
                Debug.LogError($"[ReferenceTracker<{typeof(T).Name}>] Cannot unregister null object");
                return false;
            }

            // Check if ReferenceManager is available
            var referenceManager = ReferenceManager.Instance;

            // Remove from our local tracking first
            _trackedObjects.Remove(obj.RefId);
            _registeredObjects.Remove(obj);

            // Fire the event
            try
            {
                OnObjectUnregistered?.Invoke(obj);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceTracker<{typeof(T).Name}>] Error in OnObjectUnregistered event: {e.Message}");
            }

            // Unregister from the central manager if available
            if (referenceManager != null && !referenceManager.Unregister(obj))
            {
                Debug.LogWarning($"[ReferenceTracker<{typeof(T).Name}>] Failed to unregister {obj} from ReferenceManager");
                return false;
            }

            if (referenceManager != null)
            {
                Debug.Log($"[ReferenceTracker<{typeof(T).Name}>] Unregistered: {obj.RefId}");
            }
            return true;
        }

        /// <summary>
        /// Get an object by its reference ID.
        /// </summary>
        /// <param name="referenceId">The reference ID to look up.</param>
        /// <returns>The object, or null if not found.</returns>
        public T Get(string referenceId)
        {
            return _trackedObjects.TryGetValue(referenceId, out var obj) ? obj : null;
        }

        /// <summary>
        /// Try to get an object by its reference ID.
        /// </summary>
        /// <param name="referenceId">The reference ID to look up.</param>
        /// <param name="obj">The found object, or null if not found.</param>
        /// <returns>True if the object was found, false otherwise.</returns>
        public bool TryGet(string referenceId, out T obj)
        {
            return _trackedObjects.TryGetValue(referenceId, out obj);
        }

        /// <summary>
        /// Check if an object is registered with this tracker.
        /// </summary>
        /// <param name="obj">The object to check.</param>
        /// <returns>True if the object is registered, false otherwise.</returns>
        public bool IsRegistered(T obj)
        {
            return obj != null && _registeredObjects.Contains(obj);
        }

        /// <summary>
        /// Check if a reference ID is registered with this tracker.
        /// </summary>
        /// <param name="referenceId">The reference ID to check.</param>
        /// <returns>True if the reference ID is registered, false otherwise.</returns>
        public bool IsRegistered(string referenceId)
        {
            return _trackedObjects.ContainsKey(referenceId);
        }

        /// <summary>
        /// Get all objects tracked by this tracker.
        /// </summary>
        /// <returns>A list of all tracked objects.</returns>
        public List<T> GetAll()
        {
            return _trackedObjects.Values.ToList();
        }

        /// <summary>
        /// Get all reference IDs tracked by this tracker.
        /// </summary>
        /// <returns>A list of all tracked reference IDs.</returns>
        public List<string> GetAllIds()
        {
            return _trackedObjects.Keys.ToList();
        }

        /// <summary>
        /// Get the number of objects tracked by this tracker.
        /// </summary>
        public int Count => _trackedObjects.Count;

        /// <summary>
        /// Clear all registrations from this tracker.
        /// </summary>
        public void Clear()
        {
            var objectsToUnregister = _registeredObjects.ToList();

            foreach (var obj in objectsToUnregister)
            {
                Unregister(obj);
            }

            Debug.Log($"[ReferenceTracker<{typeof(T).Name}>] Cleared all registrations");
        }

        /// <summary>
        /// Get the reference type this tracker is managing.
        /// </summary>
        public string ReferenceType => _referenceType;

        /// <summary>
        /// Find objects that match a predicate.
        /// </summary>
        /// <param name="predicate">The predicate to match against.</param>
        /// <returns>A list of objects that match the predicate.</returns>
        public List<T> Find(Func<T, bool> predicate)
        {
            if (predicate == null)
                return new List<T>();

            return _trackedObjects.Values.Where(predicate).ToList();
        }

        /// <summary>
        /// Find the first object that matches a predicate.
        /// </summary>
        /// <param name="predicate">The predicate to match against.</param>
        /// <returns>The first matching object, or null if none found.</returns>
        public T FindFirst(Func<T, bool> predicate)
        {
            if (predicate == null)
                return null;

            return _trackedObjects.Values.FirstOrDefault(predicate);
        }
    }

    /// <summary>
    /// Static helper class for managing multiple reference trackers.
    /// </summary>
    [Obsolete("Parallel registry; use ReferenceManager directly. Removed next major.")]
    public static class ReferenceTrackers
    {
        private static readonly Dictionary<Type, object> _trackers = new Dictionary<Type, object>();

        /// <summary>
        /// Get or create a reference tracker for the specified type.
        /// </summary>
        /// <typeparam name="T">The type of referenceable object to track.</typeparam>
        /// <param name="referenceType">Optional reference type override.</param>
        /// <returns>The reference tracker for the specified type.</returns>
        public static ReferenceTracker<T> Get<T>(string referenceType = null) where T : class, IReferenceable
        {
            var type = typeof(T);

            if (!_trackers.TryGetValue(type, out var tracker))
            {
                tracker = new ReferenceTracker<T>(referenceType);
                _trackers[type] = tracker;
            }

            return (ReferenceTracker<T>)tracker;
        }

        /// <summary>
        /// Clear all trackers.
        /// </summary>
        public static void ClearAll()
        {
            foreach (var tracker in _trackers.Values)
            {
                var clearMethod = tracker.GetType().GetMethod("Clear");
                clearMethod?.Invoke(tracker, null);
            }

            _trackers.Clear();
            Debug.Log("[ReferenceTrackers] Cleared all trackers");
        }
    }
}
