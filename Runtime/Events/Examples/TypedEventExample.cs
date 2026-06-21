using System;
using UnityEngine;

namespace Molca.Events.Examples
{
    /// <summary>
    /// Example class that demonstrates how to use the strongly-typed event system.
    /// This approach provides compile-time safety and better IDE support.
    /// </summary>
    public class TypedEventExample : EventListenerBehaviour
    {
        [SerializeField] private bool _triggerEventsOnStart = true;
        
        private void Start()
        {
            if (_triggerEventsOnStart)
            {
                // Example of dispatching a parameterless event using the typed event system
                Debug.Log("Dispatching ApplicationInitialized event");
                TypedEvents.ApplicationInitialized.Dispatch();
                
                // Example of dispatching an event with a string parameter
                Debug.Log("Dispatching LanguageChanged event with 'en-US' parameter");
                TypedEvents.LanguageChanged.Dispatch("en-US");
                
                // Example of dispatching an event with a complex parameter
                var sceneData = new SceneLoadEventData("MainScene", false, 1.0f);
                Debug.Log($"Dispatching SceneLoadCompleted event with SceneLoadEventData: {sceneData.SceneName}");
                TypedEvents.SceneLoadCompleted.Dispatch(sceneData);
            }
        }
        
        /// <summary>
        /// Registers for events when this component is enabled.
        /// </summary>
        public override void RegisterEvents()
        {
            // Method 1: Direct registration using the typed event and extension methods
            this.Register(TypedEvents.ApplicationInitialized, OnApplicationInitialized);
            this.Register(TypedEvents.LanguageChanged, OnLanguageChanged);
            this.Register(TypedEvents.SceneLoadCompleted, OnSceneLoadCompleted);
            
            // Method 2: Direct registration using the typed event itself
            TypedEvents.ApplicationQuitting.Register(OnApplicationQuitting);
            
            // Method 3 (Advanced): Custom event creation for events not predefined
            var customEvent = new TypedEvents.Event<int>("Custom.CountChanged");
            customEvent.Register(OnCountChanged);
            
            // Note: For method 3, you would typically store the event reference somewhere
            // to be able to unregister later, or define it as a static readonly field
        }
        
        /// <summary>
        /// Unregisters from events when this component is disabled.
        /// </summary>
        public override void UnregisterEvents()
        {
            // Method 1: Unregister specific events
            this.Unregister(TypedEvents.ApplicationInitialized, OnApplicationInitialized);
            this.Unregister(TypedEvents.LanguageChanged, OnLanguageChanged);
            this.Unregister(TypedEvents.SceneLoadCompleted, OnSceneLoadCompleted);
            
            // Method 2: Direct unregistration using the typed event itself
            TypedEvents.ApplicationQuitting.Unregister(OnApplicationQuitting);
            
            // Method 3 (Alternative): Unregister all events at once
            // This requires using the extension methods for registration
            // this.UnregisterAll();
        }
        
        // Event handlers
        
        private void OnApplicationInitialized()
        {
            Debug.Log("Application initialized event received via typed events");
        }
        
        private void OnApplicationQuitting()
        {
            Debug.Log("Application quitting event received via typed events");
        }
        
        private void OnLanguageChanged(string languageCode)
        {
            Debug.Log($"Language changed to: {languageCode} via typed events");
        }
        
        private void OnSceneLoadCompleted(SceneLoadEventData data)
        {
            Debug.Log($"Scene '{data.SceneName}' loaded successfully via typed events");
        }
        
        private void OnCountChanged(int newCount)
        {
            Debug.Log($"Count changed to: {newCount} via custom typed event");
        }
        
        // Example of how to define custom events in a real application
        public static class CustomEvents
        {
            public static readonly TypedEvents.Event<int> CountChanged = 
                new TypedEvents.Event<int>("Custom.CountChanged");
                
            public static readonly TypedEvents.Event<Vector3> PlayerMoved = 
                new TypedEvents.Event<Vector3>("Game.PlayerMoved");
        }
    }
} 