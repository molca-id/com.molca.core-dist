using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Utils
{
    /// <summary>
    /// Generic object pool for Unity Components
    /// </summary>
    public class ObjectPool<T> where T : Component
    {
        // HashSet: O(1) membership for double-return protection in Return().
        private readonly HashSet<T> _activeObjects;
        private readonly List<T> _pooledObjects;
        private readonly T _prefab;
        private readonly Transform _prefabRoot;
        private readonly Action<T> _onObjectSpawned;
        private readonly Action<T> _onObjectDespawned;

        public int TotalObjects { get; private set; }
        public int ActiveCount => _activeObjects.Count;
        public int PooledCount => _pooledObjects.Count;
        public Action<T> OnObjectReturned;

        /// <summary>
        /// Creates a new object pool
        /// </summary>
        /// <param name="prefab">Prefab to pool</param>
        /// <param name="initialPoolSize">Initial size of the pool</param>
        /// <param name="root">Parent transform for pooled objects</param>
        /// <param name="onSpawned">Optional callback when object is spawned</param>
        /// <param name="onDespawned">Optional callback when object is despawned</param>
        public ObjectPool(T prefab, int initialPoolSize, Transform root, 
            Action<T> onSpawned = null, Action<T> onDespawned = null)
        {
            _activeObjects = new HashSet<T>();
            _pooledObjects = new List<T>();
            _prefab = prefab ? prefab : throw new ArgumentNullException(nameof(prefab));
            _prefabRoot = root ? root : throw new ArgumentNullException(nameof(root));
            _onObjectSpawned = onSpawned;
            _onObjectDespawned = onDespawned;

            IncreaseSize(Mathf.Max(0, initialPoolSize));
        }

        /// <summary>
        /// Increases the pool size by the specified amount
        /// </summary>
        public void IncreaseSize(int additionalCount)
        {
            for (int i = 0; i < additionalCount; i++)
            {
                CreateNewPooledObject();
            }
        }

        /// <summary>
        /// Gets an object from the pool. Creates a new one if pool is empty.
        /// </summary>
        public T Get()
        {
            T obj = GetPooledObject();
            _activeObjects.Add(obj);
            
            obj.gameObject.SetActive(true);
            _onObjectSpawned?.Invoke(obj);
            
            return obj;
        }

        /// <summary>
        /// Returns an object to the pool
        /// </summary>
        public void Return(T obj)
        {
            if (ReferenceEquals(obj, null))
            {
                Debug.LogWarning($"Attempting to return null object to {typeof(T)} pool");
                return;
            }

            // Destroyed but still tracked: drop it from the pool's bookkeeping
            // instead of recycling a dead component (or leaking the active entry).
            if (obj == null)
            {
                if (_activeObjects.Remove(obj))
                {
                    TotalObjects--;
                }
                Debug.LogWarning($"Attempting to return a destroyed object to {typeof(T)} pool; dropping it.");
                return;
            }

            // Also rejects double-returns: the first Return removed it from the
            // active set, so a second call lands here instead of duplicating the
            // object in _pooledObjects.
            if (!_activeObjects.Contains(obj))
            {
                Debug.LogWarning($"Attempting to return an object that is not managed by this pool (or was already returned): {obj.name}");
                return;
            }

            ReturnToPool(obj);
        }

        /// <summary>
        /// Returns all active objects to the pool
        /// </summary>
        public void ReturnAll()
        {
            // Copy to avoid collection modification during iteration
            var activeObjectsCopy = new List<T>(_activeObjects);
            foreach (var obj in activeObjectsCopy)
            {
                Return(obj);
            }
        }

        /// <summary>
        /// Destroys all pooled and active objects
        /// </summary>
        public void Clear()
        {
            foreach (var obj in _activeObjects)
            {
                if (obj) UnityEngine.Object.Destroy(obj.gameObject);
            }
            foreach (var obj in _pooledObjects)
            {
                if (obj) UnityEngine.Object.Destroy(obj.gameObject);
            }

            _activeObjects.Clear();
            _pooledObjects.Clear();
            TotalObjects = 0;
        }

        private T CreateNewPooledObject()
        {
            T newObject = InstantiateObject();
            _pooledObjects.Add(newObject);
            return newObject;
        }

        private T InstantiateObject()
        {
            T newObject = UnityEngine.Object.Instantiate(_prefab, _prefabRoot);
            newObject.gameObject.SetActive(false);
            TotalObjects++;
            return newObject;
        }

        private T GetPooledObject()
        {
            // Pooled entries can be destroyed externally (e.g., scene unload of a
            // reparented object); skip Unity-null entries instead of handing out
            // a destroyed component.
            while (_pooledObjects.Count > 0)
            {
                T obj = _pooledObjects[^1]; // Get last object
                _pooledObjects.RemoveAt(_pooledObjects.Count - 1);

                if (obj == null)
                {
                    TotalObjects--;
                    continue;
                }
                return obj;
            }

            // Pool exhausted: hand out a fresh instance WITHOUT adding it to
            // _pooledObjects — the caller marks it active. Adding it to both
            // lists (the previous behavior) duplicated the entry once returned.
            return InstantiateObject();
        }

        private void ReturnToPool(T obj)
        {
            _onObjectDespawned?.Invoke(obj);
            OnObjectReturned?.Invoke(obj);
            
            obj.gameObject.SetActive(false);
            obj.transform.SetParent(_prefabRoot);
            
            _activeObjects.Remove(obj);
            _pooledObjects.Add(obj);
        }
    }
}