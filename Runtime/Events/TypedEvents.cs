using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Events
{
    /// <summary>
    /// Provides a type-safe way to define and access events.
    /// This allows for compile-time checking of event names and parameter types.
    ///
    /// Events are organized across multiple partial classes for better maintainability:
    /// - TypedEvents.cs (main class with Event/Event<T> definitions)
    /// - TypedEvents.Scene.cs (scene management events)
    /// - TypedEvents.UI.cs (user interface events)
    /// - TypedEvents.Audio.cs (audio system events)
    /// - TypedEvents.ContentPackage.cs (content package events)
    /// - TypedEvents.Network.cs (network communication events)
    /// - TypedEvents.Input.cs (input device events)
    /// </summary>
    public static partial class TypedEvents
    {
        #region Event Definitions
        
        /// <summary>
        /// Defines an event with no parameters.
        /// </summary>
        public class Event
        {
            private readonly string _eventName;
            
            public Event(string eventName)
            {
                _eventName = eventName;
            }
            
            /// <summary>
            /// Registers a callback for this event.
            /// </summary>
            public void Register(Action callback)
            {
                var dispatcher = RuntimeManager.GetService<EventDispatcher>();
                if (dispatcher == null)
                {
                    Debug.LogError("[TypedEvents] EventDispatcher is not initialized");
                    return;
                }

                dispatcher.RegisterEvent(_eventName, callback);
            }
            
            /// <summary>
            /// Unregisters a callback from this event.
            /// </summary>
            public void Unregister(Action callback)
            {
                var dispatcher = RuntimeManager.GetService<EventDispatcher>();
                if (dispatcher == null)
                {
                    Debug.LogError("[TypedEvents] EventDispatcher is not initialized");
                    return;
                }

                dispatcher.UnregisterEvent(_eventName, callback);
            }
            
            /// <summary>
            /// Dispatches this event.
            /// </summary>
            public void Dispatch()
            {
                var dispatcher = RuntimeManager.GetService<EventDispatcher>();
                if (dispatcher == null)
                {
                    Debug.LogError("[TypedEvents] EventDispatcher is not initialized");
                    return;
                }

                dispatcher.DispatchEvent(_eventName);
            }
        }
        
        /// <summary>
        /// Defines an event with a typed parameter.
        /// </summary>
        public class Event<T>
        {
            private readonly string _eventName;
            
            public Event(string eventName)
            {
                _eventName = eventName;
            }
            
            /// <summary>
            /// Registers a callback for this event.
            /// </summary>
            public void Register(Action<T> callback)
            {
                var dispatcher = RuntimeManager.GetService<EventDispatcher>();
                if (dispatcher == null)
                {
                    Debug.LogWarning("[TypedEvents] EventDispatcher is not initialized");
                    return;
                }

                dispatcher.RegisterEvent<T>(_eventName, callback);
            }
            
            /// <summary>
            /// Unregisters a callback from this event.
            /// </summary>
            public void Unregister(Action<T> callback)
            {
                var dispatcher = RuntimeManager.GetService<EventDispatcher>();
                if (dispatcher == null)
                {
                    return;
                }

                dispatcher.UnregisterEvent<T>(_eventName, callback);
            }
            
            /// <summary>
            /// Dispatches this event with the specified data.
            /// </summary>
            public void Dispatch(T data)
            {
                var dispatcher = RuntimeManager.GetService<EventDispatcher>();
                if (dispatcher == null)
                {
                    Debug.LogError("[TypedEvents] EventDispatcher is not initialized");
                    return;
                }

                dispatcher.DispatchEvent<T>(_eventName, data);
            }
        }
        
        #endregion
        
        #region Core System Events
        
        // Core system events (kept in main file)
        public static readonly Event ApplicationInitialized = new Event(EventConstants.Application.Initialized);
        public static readonly Event ApplicationPausing = new Event(EventConstants.Application.Pausing);
        public static readonly Event ApplicationResuming = new Event(EventConstants.Application.Resuming);
        public static readonly Event ApplicationQuitting = new Event(EventConstants.Application.Quitting);
        
        #endregion
    }
    
    /// <summary>
    /// Extension methods for EventListenerBehaviour to work with typed events.
    /// </summary>
    public static class EventListenerExtensions
    {
        // ConditionalWeakTable: entries do not pin destroyed/abandoned listeners.
        // EventListenerBehaviour.OnDestroy calls UnregisterAll for deterministic
        // dispatcher cleanup; the weak table is the backstop if that is bypassed.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EventListenerBehaviour, List<(object, Delegate)>> _registrations =
            new System.Runtime.CompilerServices.ConditionalWeakTable<EventListenerBehaviour, List<(object, Delegate)>>();
        
        /// <summary>
        /// Registers a callback for a typed event and automatically tracks it for cleanup.
        /// </summary>
        public static void Register(this EventListenerBehaviour listener, TypedEvents.Event eventDef, Action callback)
        {
            TrackRegistration(listener, eventDef, callback);
            eventDef.Register(callback);
        }
        
        /// <summary>
        /// Registers a callback for a typed event with parameter and automatically tracks it for cleanup.
        /// </summary>
        public static void Register<T>(this EventListenerBehaviour listener, TypedEvents.Event<T> eventDef, Action<T> callback)
        {
            TrackRegistration(listener, eventDef, callback);
            eventDef.Register(callback);
        }
        
        /// <summary>
        /// Unregisters a callback from a typed event.
        /// </summary>
        public static void Unregister(this EventListenerBehaviour listener, TypedEvents.Event eventDef, Action callback)
        {
            RemoveRegistration(listener, eventDef, callback);
            eventDef.Unregister(callback);
        }
        
        /// <summary>
        /// Unregisters a callback from a typed event with parameter.
        /// </summary>
        public static void Unregister<T>(this EventListenerBehaviour listener, TypedEvents.Event<T> eventDef, Action<T> callback)
        {
            RemoveRegistration(listener, eventDef, callback);
            eventDef.Unregister(callback);
        }
        
        /// <summary>
        /// Unregisters all callbacks registered by this listener.
        /// </summary>
        public static void UnregisterAll(this EventListenerBehaviour listener)
        {
            if (_registrations.TryGetValue(listener, out List<(object, Delegate)> registrations))
            {
                foreach (var (eventDef, callback) in registrations)
                {
                    if (eventDef is TypedEvents.Event typedEvent && callback is Action action)
                    {
                        typedEvent.Unregister(action);
                    }
                    else
                    {
                        // Use reflection to call the right Unregister method with the right type
                        Type eventType = eventDef.GetType();
                        Type paramType = eventType.GenericTypeArguments[0];
                        
                        // Call the Unregister method using reflection
                        // This is more complex than direct calls but handles any generic type
                        eventType.GetMethod("Unregister").Invoke(eventDef, new object[] { callback });
                    }
                }
                
                _registrations.Remove(listener);
            }
        }
        
        // Helper methods to track registrations
        private static void TrackRegistration(EventListenerBehaviour listener, object eventDef, Delegate callback)
        {
            var registrations = _registrations.GetValue(listener, _ => new List<(object, Delegate)>());
            registrations.Add((eventDef, callback));
        }
        
        private static void RemoveRegistration(EventListenerBehaviour listener, object eventDef, Delegate callback)
        {
            if (_registrations.TryGetValue(listener, out List<(object, Delegate)> registrations))
            {
                registrations.RemoveAll(item => 
                    item.Item1 == eventDef && item.Item2 == callback);
                
                if (registrations.Count == 0)
                {
                    _registrations.Remove(listener);
                }
            }
        }
    }
} 