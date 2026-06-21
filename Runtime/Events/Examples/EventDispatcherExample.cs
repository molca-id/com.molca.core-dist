using System;
using UnityEngine;

namespace Molca.Events.Examples
{
    /// <summary>
    /// Example class that demonstrates how to use the EventDispatcher system with dependency injection.
    /// This class both publishes events and subscribes to events.
    /// </summary>
    public class EventDispatcherExample : EventListenerBehaviour
    {
        [SerializeField] private bool _triggerEventsOnStart = true;
        
        // Dependency injection - automatically injected after RuntimeManager.IsReady
        [Inject] private EventDispatcher _eventDispatcher;
        
        private async void Start()
        {
            // Wait for RuntimeManager to initialize (dependency injection happens here)
            await RuntimeManager.WaitForInitialization();
            
            // Ensure dependencies are injected
            if (_eventDispatcher == null)
            {
                RuntimeManager.InjectDependencies(this);
            }
            
            if (_triggerEventsOnStart && _eventDispatcher != null)
            {
                // Example of dispatching a parameterless event
                Debug.Log("[EventDispatcherExample] Dispatching Application.Initialized event");
                _eventDispatcher.DispatchEvent(EventConstants.Application.Initialized);
                
                // Example of dispatching an event with a string parameter
                Debug.Log("[EventDispatcherExample] Dispatching UI.LanguageChanged event with 'en-US' parameter");
                _eventDispatcher.DispatchEvent(EventConstants.UI.LanguageChanged, "en-US");
                
                // Example of dispatching an event with a complex parameter
                var sceneData = new SceneLoadEventData("MainScene", false, 1.0f);
                Debug.Log($"[EventDispatcherExample] Dispatching Scene.LoadCompleted event with SceneLoadEventData: {sceneData.SceneName}");
                _eventDispatcher.DispatchEvent(EventConstants.Scene.LoadCompleted, sceneData);
            }
        }
        
        /// <summary>
        /// Registers for events when this component is enabled.
        /// </summary>
        public override void RegisterEvents()
        {
            if (_eventDispatcher == null)
            {
                Debug.LogWarning("[EventDispatcherExample] EventDispatcher not injected yet. Waiting for RuntimeManager.");
                return;
            }
            
            // Register for parameterless events
            _eventDispatcher.RegisterEvent(EventConstants.Application.Initialized, OnApplicationInitialized);
            
            // Register for events with a string parameter
            _eventDispatcher.RegisterEvent<string>(EventConstants.UI.LanguageChanged, OnLanguageChanged);
            
            // Register for events with a complex parameter
            _eventDispatcher.RegisterEvent<SceneLoadEventData>(EventConstants.Scene.LoadCompleted, OnSceneLoadCompleted);
        }
        
        /// <summary>
        /// Unregisters from events when this component is disabled.
        /// </summary>
        public override void UnregisterEvents()
        {
            if (_eventDispatcher == null)
                return;
            
            // Unregister from parameterless events
            _eventDispatcher.UnregisterEvent(EventConstants.Application.Initialized, OnApplicationInitialized);
            
            // Unregister from events with a string parameter
            _eventDispatcher.UnregisterEvent<string>(EventConstants.UI.LanguageChanged, OnLanguageChanged);
            
            // Unregister from events with a complex parameter
            _eventDispatcher.UnregisterEvent<SceneLoadEventData>(EventConstants.Scene.LoadCompleted, OnSceneLoadCompleted);
        }
        
        // Event handlers
        
        private void OnApplicationInitialized()
        {
            Debug.Log("[EventDispatcherExample] Application initialized event received");
            
            // Example of dispatching a new event in response to receiving an event
            _eventDispatcher?.DispatchEvent(EventConstants.Application.Initialized);
        }
        
        private void OnLanguageChanged(string languageCode)
        {
            Debug.Log($"[EventDispatcherExample] Language changed to: {languageCode}");
            
            // You can perform actions based on the event data
            if (languageCode == "en-US")
            {
                Debug.Log("[EventDispatcherExample] English (US) language selected");
            }
        }
        
        private void OnSceneLoadCompleted(SceneLoadEventData data)
        {
            Debug.Log($"[EventDispatcherExample] Scene '{data.SceneName}' loaded successfully. Additive: {data.IsAdditive}, Progress: {data.Progress}");
            
            // You can access all properties of the complex event data
            if (data.SceneName == "MainScene")
            {
                Debug.Log("[EventDispatcherExample] Main scene loaded - initializing gameplay systems");
            }
        }
    }
} 