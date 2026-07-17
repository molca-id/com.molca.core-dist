using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Events
{
    /// <summary>
    /// A robust, type-safe event dispatcher for Unity applications.
    /// This class allows publishing and subscribing to events with strongly typed parameters.
    /// </summary>
    /// <remarks>
    /// <para><b>Typed dispatch semantics:</b> handlers receive an event when their
    /// registered parameter type is the dispatched payload's type <i>or any of its base
    /// types</i> (including <c>object</c>, which acts as a catch-all). Registering under a
    /// derived type does NOT receive base-type dispatches.</para>
    /// <para>Dispatch iterates a snapshot captured at registration time, so handlers may
    /// safely register/unregister during a dispatch, and one throwing handler never blocks
    /// the rest.</para>
    /// </remarks>
    public class EventDispatcher : RuntimeSubsystem
    {
        #region Fields and Properties

        /// <summary>
        /// A combined delegate plus its invocation-list snapshot. The snapshot is
        /// rebuilt on register/unregister so dispatch (the hot path) iterates a
        /// cached array instead of allocating via GetInvocationList() per dispatch.
        /// </summary>
        private sealed class HandlerList
        {
            public Delegate Combined;
            public Delegate[] Snapshot = Array.Empty<Delegate>();

            public void Rebuild()
            {
                Snapshot = Combined?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
        }

        // Field-initialized (not in Initialize) so Register/Dispatch calls that
        // arrive before subsystem init — or on an instance constructed in tests —
        // never NRE.
        // Stores event handlers for generic events (no parameters)
        private Dictionary<string, HandlerList> _eventHandlers = new Dictionary<string, HandlerList>();

        // Stores event handlers for typed events, organized by parameter type and event name
        private Dictionary<Type, Dictionary<string, HandlerList>> _typedEvents = new Dictionary<Type, Dictionary<string, HandlerList>>();

        #endregion
        
        #region RuntimeSubsystem Implementation
        
        /// <summary>
        /// Initializes the event dispatcher system.
        /// </summary>
        /// <param name="finishCallback">Callback to invoke when initialization is complete.</param>
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            Debug.Log("[EventDispatcher] Initialized");
            
            // Notify that initialization is complete
            finishCallback?.Invoke(this);
        }
        
        /// <summary>
        /// Shuts down the event dispatcher system and clears all events.
        /// </summary>
        public override void Shutdown()
        {
            ClearAllEvents();
            base.Shutdown();
        }
        
        #endregion
        
        #region Event Registration Methods
        
        /// <summary>
        /// Registers a callback for a parameterless event.
        /// </summary>
        /// <param name="eventName">The unique name of the event.</param>
        /// <param name="callback">The method to call when the event is triggered.</param>
        public void RegisterEvent(string eventName, Action callback)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                Debug.LogError("[EventDispatcher] Cannot register an event with a null or empty name");
                return;
            }
            
            if (callback == null)
            {
                Debug.LogError("[EventDispatcher] Cannot register a null callback");
                return;
            }
            
            if (!_eventHandlers.TryGetValue(eventName, out HandlerList list))
            {
                list = new HandlerList();
                _eventHandlers[eventName] = list;
            }

            WarnIfDuplicate(list, callback, eventName);
            list.Combined = Delegate.Combine(list.Combined, callback);
            list.Rebuild();
        }
        
        /// <summary>
        /// Registers a callback for an event with a typed parameter.
        /// </summary>
        /// <typeparam name="T">The type of the event parameter.</typeparam>
        /// <param name="eventName">The unique name of the event.</param>
        /// <param name="callback">The method to call when the event is triggered.</param>
        public void RegisterEvent<T>(string eventName, Action<T> callback)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                Debug.LogError("[EventDispatcher] Cannot register an event with a null or empty name");
                return;
            }
            
            if (callback == null)
            {
                Debug.LogError("[EventDispatcher] Cannot register a null callback");
                return;
            }
            
            Type paramType = typeof(T);

            if (!_typedEvents.TryGetValue(paramType, out Dictionary<string, HandlerList> eventDict))
            {
                eventDict = new Dictionary<string, HandlerList>();
                _typedEvents[paramType] = eventDict;
            }

            if (!eventDict.TryGetValue(eventName, out HandlerList list))
            {
                list = new HandlerList();
                eventDict[eventName] = list;
            }

            WarnIfDuplicate(list, callback, eventName);
            list.Combined = Delegate.Combine(list.Combined, callback);
            list.Rebuild();
        }

        /// <summary>
        /// Dev-build-only guard for the classic OnEnable-without-OnDisable bug:
        /// double-registering the same callback silently doubles its invocations.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void WarnIfDuplicate(HandlerList list, Delegate callback, string eventName)
        {
            var snapshot = list.Snapshot;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == (object)callback || snapshot[i].Equals(callback))
                {
                    Debug.LogWarning(
                        $"[EventDispatcher] Callback {callback.Method.DeclaringType?.Name}.{callback.Method.Name} " +
                        $"is already registered for event '{eventName}' — it will now be invoked multiple times per dispatch. " +
                        "Check for a missing Unregister (e.g. OnEnable without OnDisable).");
                    return;
                }
            }
        }
        
        #endregion
        
        #region Event Unregistration Methods
        
        /// <summary>
        /// Unregisters a callback for a parameterless event.
        /// </summary>
        /// <param name="eventName">The unique name of the event.</param>
        /// <param name="callback">The method to remove from the event callbacks.</param>
        public void UnregisterEvent(string eventName, Action callback)
        {
            if (string.IsNullOrEmpty(eventName) || callback == null)
                return;
                
            if (_eventHandlers.TryGetValue(eventName, out HandlerList list))
            {
                list.Combined = Delegate.Remove(list.Combined, callback);

                if (list.Combined == null)
                {
                    _eventHandlers.Remove(eventName);
                }
                else
                {
                    list.Rebuild();
                }
            }
        }
        
        /// <summary>
        /// Unregisters a callback for an event with a typed parameter.
        /// </summary>
        /// <typeparam name="T">The type of the event parameter.</typeparam>
        /// <param name="eventName">The unique name of the event.</param>
        /// <param name="callback">The method to remove from the event callbacks.</param>
        public void UnregisterEvent<T>(string eventName, Action<T> callback)
        {
            if (string.IsNullOrEmpty(eventName) || callback == null)
                return;
                
            Type paramType = typeof(T);

            if (_typedEvents.TryGetValue(paramType, out Dictionary<string, HandlerList> eventDict) &&
                eventDict.TryGetValue(eventName, out HandlerList list))
            {
                list.Combined = Delegate.Remove(list.Combined, callback);

                if (list.Combined == null)
                {
                    eventDict.Remove(eventName);

                    // Clean up empty dictionaries
                    if (eventDict.Count == 0)
                    {
                        _typedEvents.Remove(paramType);
                    }
                }
                else
                {
                    list.Rebuild();
                }
            }
        }
        
        #endregion
        
        #region Event Dispatching Methods
        
        /// <summary>
        /// Dispatches a parameterless event, triggering all registered callbacks.
        /// </summary>
        /// <param name="eventName">The unique name of the event to dispatch.</param>
        public void DispatchEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                Debug.LogError("[EventDispatcher] Cannot dispatch an event with a null or empty name");
                return;
            }
            
            if (_eventHandlers.TryGetValue(eventName, out HandlerList list))
            {
                // Iterate the registration-time snapshot (no per-dispatch allocation);
                // invoke each subscriber individually so one throwing handler cannot
                // prevent the remaining handlers from running.
                var snapshot = list.Snapshot;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        (snapshot[i] as Action)?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[EventDispatcher] Handler error dispatching event '{eventName}':");
                        Debug.LogException(ex);
                    }
                }
            }
        }
        
        /// <summary>
        /// Dispatches an event with a typed parameter, triggering all registered callbacks.
        /// </summary>
        /// <typeparam name="T">The type of the event parameter.</typeparam>
        /// <param name="eventName">The unique name of the event to dispatch.</param>
        /// <param name="eventData">The data to pass to event handlers.</param>
        public void DispatchEvent<T>(string eventName, T eventData)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                Debug.LogError("[EventDispatcher] Cannot dispatch an event with a null or empty name");
                return;
            }
            
            // Polymorphic delivery: walk the payload's type hierarchy so handlers
            // registered under a base type (including object as a catch-all) also
            // receive derived/boxed dispatches. Exact-type-only lookup silently
            // dropped e.g. a float payload for a handler registered as <object>.
            for (Type lookupType = typeof(T); lookupType != null; lookupType = lookupType.BaseType)
            {
                if (!_typedEvents.TryGetValue(lookupType, out Dictionary<string, HandlerList> eventDict) ||
                    !eventDict.TryGetValue(eventName, out HandlerList list))
                {
                    continue;
                }

                // Iterate the registration-time snapshot (no per-dispatch allocation);
                // invoke each subscriber individually so one throwing handler cannot
                // prevent the remaining handlers from running.
                var snapshot = list.Snapshot;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        // Contravariance covers reference-type payloads (Action<Base>
                        // as Action<Derived>); the DynamicInvoke fallback covers value
                        // types boxed into a base-type handler (e.g. Action<object>
                        // receiving a float). Type safety is guaranteed by the walk:
                        // eventData IS a lookupType.
                        if (snapshot[i] is Action<T> typed)
                        {
                            typed.Invoke(eventData);
                        }
                        else
                        {
                            snapshot[i].DynamicInvoke(eventData);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[EventDispatcher] Handler error dispatching event '{eventName}' with data of type '{typeof(T).Name}':");
                        Debug.LogException(ex);
                    }
                }
            }
        }
        
        #endregion
        
        #region Cleanup Methods
        
        /// <summary>
        /// Removes all registered event handlers for a specific event.
        /// </summary>
        /// <param name="eventName">The unique name of the event to clear.</param>
        public void ClearEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
                return;
                
            _eventHandlers.Remove(eventName);

            foreach (var typeDict in _typedEvents.Values)
            {
                typeDict.Remove(eventName);
            }

            // Clean up empty dictionaries
            List<Type> emptyTypes = null;
            foreach (var kvp in _typedEvents)
            {
                if (kvp.Value.Count == 0)
                {
                    (emptyTypes ??= new List<Type>()).Add(kvp.Key);
                }
            }

            if (emptyTypes != null)
            {
                foreach (var type in emptyTypes)
                {
                    _typedEvents.Remove(type);
                }
            }
        }
        
        /// <summary>
        /// Removes all registered event handlers.
        /// </summary>
        public void ClearAllEvents()
        {
            if (_eventHandlers != null)
                _eventHandlers.Clear();
                
            if (_typedEvents != null)
                _typedEvents.Clear();
            
            Debug.Log("[EventDispatcher] All events cleared");
        }
        
        private void OnDestroy()
        {
            ClearAllEvents();
        }
        
        #endregion
    }
} 