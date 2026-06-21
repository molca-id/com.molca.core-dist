using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Events
{
    /// <summary>
    /// A robust, type-safe event dispatcher for Unity applications.
    /// This class allows publishing and subscribing to events with strongly typed parameters.
    /// </summary>
    public class EventDispatcher : RuntimeSubsystem
    {
        #region Fields and Properties
        
        // Stores event handlers for generic events (no parameters)
        private Dictionary<string, Delegate> _eventHandlers;
        
        // Stores event handlers for typed events, organized by parameter type and event name
        private Dictionary<Type, Dictionary<string, Delegate>> _typedEvents;
        
        #endregion
        
        #region RuntimeSubsystem Implementation
        
        /// <summary>
        /// Initializes the event dispatcher system.
        /// </summary>
        /// <param name="finishCallback">Callback to invoke when initialization is complete.</param>
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            // Initialize event storage
            _eventHandlers = new Dictionary<string, Delegate>();
            _typedEvents = new Dictionary<Type, Dictionary<string, Delegate>>();
            
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
            
            if (!_eventHandlers.TryGetValue(eventName, out Delegate existingHandlers))
            {
                _eventHandlers[eventName] = callback;
            }
            else
            {
                _eventHandlers[eventName] = Delegate.Combine(existingHandlers, callback);
            }
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
            
            if (!_typedEvents.TryGetValue(paramType, out Dictionary<string, Delegate> eventDict))
            {
                eventDict = new Dictionary<string, Delegate>();
                _typedEvents[paramType] = eventDict;
            }
            
            if (!eventDict.TryGetValue(eventName, out Delegate existingHandlers))
            {
                eventDict[eventName] = callback;
            }
            else
            {
                eventDict[eventName] = Delegate.Combine(existingHandlers, callback);
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
                
            if (_eventHandlers.TryGetValue(eventName, out Delegate existingHandlers))
            {
                Delegate newHandlers = Delegate.Remove(existingHandlers, callback);
                
                if (newHandlers == null)
                {
                    _eventHandlers.Remove(eventName);
                }
                else
                {
                    _eventHandlers[eventName] = newHandlers;
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
            
            if (_typedEvents.TryGetValue(paramType, out Dictionary<string, Delegate> eventDict) &&
                eventDict.TryGetValue(eventName, out Delegate existingHandlers))
            {
                Delegate newHandlers = Delegate.Remove(existingHandlers, callback);
                
                if (newHandlers == null)
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
                    eventDict[eventName] = newHandlers;
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
            
            if (_eventHandlers.TryGetValue(eventName, out Delegate handler))
            {
                // Invoke each subscriber individually so one throwing handler
                // cannot prevent the remaining handlers from running.
                foreach (var subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        (subscriber as Action)?.Invoke();
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
            
            Type paramType = typeof(T);
            
            if (_typedEvents.TryGetValue(paramType, out Dictionary<string, Delegate> eventDict) &&
                eventDict.TryGetValue(eventName, out Delegate handler))
            {
                // Invoke each subscriber individually so one throwing handler
                // cannot prevent the remaining handlers from running.
                foreach (var subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        (subscriber as Action<T>)?.Invoke(eventData);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[EventDispatcher] Handler error dispatching event '{eventName}' with data of type '{paramType.Name}':");
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
            List<Type> emptyTypes = new List<Type>();
            foreach (var kvp in _typedEvents)
            {
                if (kvp.Value.Count == 0)
                {
                    emptyTypes.Add(kvp.Key);
                }
            }
            
            foreach (var type in emptyTypes)
            {
                _typedEvents.Remove(type);
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