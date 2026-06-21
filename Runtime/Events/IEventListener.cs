using System;
using UnityEngine;

namespace Molca.Events
{
    /// <summary>
    /// Interface for classes that listen to events from the EventDispatcher.
    /// Implementing this interface helps standardize event handling across components.
    /// </summary>
    public interface IEventListener
    {
        /// <summary>
        /// Called to register all event handlers for this listener.
        /// Implementation should connect all necessary event handlers to the EventDispatcher.
        /// </summary>
        void RegisterEvents();
        
        /// <summary>
        /// Called to unregister all event handlers for this listener.
        /// Implementation should disconnect all previously registered event handlers.
        /// </summary>
        void UnregisterEvents();
    }
    
    /// <summary>
    /// Base MonoBehaviour for components that listen to events.
    /// Provides automatic registration and unregistration of events.
    /// </summary>
    public abstract class EventListenerBehaviour : MonoBehaviour, IEventListener
    {
        /// <summary>
        /// When the component is enabled, register for events.
        /// </summary>
        protected virtual void OnEnable()
        {
            RegisterEvents();
        }
        
        /// <summary>
        /// When the component is disabled, unregister from events.
        /// </summary>
        protected virtual void OnDisable()
        {
            UnregisterEvents();
        }
        
        /// <summary>
        /// Releases every registration made through the
        /// <see cref="EventListenerExtensions"/> tracking helpers, so a destroyed
        /// listener cannot leak handlers into the <see cref="EventDispatcher"/>.
        /// </summary>
        protected virtual void OnDestroy()
        {
            this.UnregisterAll();
        }

        /// <summary>
        /// Implementation should register all event handlers with the EventDispatcher.
        /// </summary>
        public abstract void RegisterEvents();
        
        /// <summary>
        /// Implementation should unregister all event handlers from the EventDispatcher.
        /// </summary>
        public abstract void UnregisterEvents();
    }
} 