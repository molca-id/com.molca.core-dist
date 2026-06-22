using System;
using UnityEngine;

namespace Molca.Events
{
    /// <summary>
    /// Contains constants for all event names used in the application.
    /// Centralizing event names helps prevent typos and makes it easier to refactor.
    /// </summary>
    public static class EventConstants
    {
        // Application lifecycle events
        public static class Application
        {
            /// <summary>
            /// Dispatched when the application is fully initialized and ready.
            /// </summary>
            public const string Initialized = "Application.Initialized";
            
            /// <summary>
            /// Dispatched when the application is about to pause.
            /// </summary>
            public const string Pausing = "Application.Pausing";
            
            /// <summary>
            /// Dispatched when the application resumes from a paused state.
            /// </summary>
            public const string Resuming = "Application.Resuming";
            
            /// <summary>
            /// Dispatched when the application is about to quit.
            /// </summary>
            public const string Quitting = "Application.Quitting";
        }
        
        // Scene management events
        public static class Scene
        {
            /// <summary>
            /// Dispatched when a scene load begins. Data: SceneLoadEventData
            /// </summary>
            public const string LoadStarted = "Scene.LoadStarted";
            
            /// <summary>
            /// Dispatched when a scene has finished loading. Data: SceneLoadEventData
            /// </summary>
            public const string LoadCompleted = "Scene.LoadCompleted";
            
            /// <summary>
            /// Dispatched when a scene load fails. Data: SceneLoadErrorEventData
            /// </summary>
            public const string LoadFailed = "Scene.LoadFailed";
            
            /// <summary>
            /// Dispatched when a scene unload begins. Data: string (scene name)
            /// </summary>
            public const string UnloadStarted = "Scene.UnloadStarted";
            
            /// <summary>
            /// Dispatched when a scene has finished unloading. Data: string (scene name)
            /// </summary>
            public const string UnloadCompleted = "Scene.UnloadCompleted";
        }
        
        // Performance budget events (Sprint 54)
        public static class Performance
        {
            /// <summary>
            /// Dispatched when a performance-budget metric crosses into the critical state.
            /// Data: <see cref="Molca.Utilities.BudgetThresholdEventData"/>
            /// </summary>
            public const string BudgetCritical = "Performance.BudgetCritical";

            /// <summary>
            /// Dispatched when all performance-budget metrics return below critical.
            /// Data: <see cref="Molca.Utilities.BudgetThresholdEventData"/>
            /// </summary>
            public const string BudgetRecovered = "Performance.BudgetRecovered";
        }

        // User interface events
        public static class UI
        {
            /// <summary>
            /// Dispatched when a dialog or modal is shown. Data: string (dialog ID)
            /// </summary>
            public const string DialogShown = "UI.DialogShown";
            
            /// <summary>
            /// Dispatched when a dialog or modal is hidden. Data: string (dialog ID)
            /// </summary>
            public const string DialogHidden = "UI.DialogHidden";
            
            /// <summary>
            /// Dispatched when the UI language changes. Data: string (language code)
            /// </summary>
            public const string LanguageChanged = "UI.LanguageChanged";
        }
        
        // Network events
        public static class Network
        {
            /// <summary>
            /// Dispatched when a network connection is established.
            /// </summary>
            public const string Connected = "Network.Connected";
            
            /// <summary>
            /// Dispatched when a network connection is lost. Data: string (error reason)
            /// </summary>
            public const string Disconnected = "Network.Disconnected";
            
            /// <summary>
            /// Dispatched when network data is received. Data: NetworkPacketEventData
            /// </summary>
            public const string DataReceived = "Network.DataReceived";
        }
        
        // Input events
        public static class Input
        {
            /// <summary>
            /// Dispatched when an input device is connected. Data: InputDeviceEventData
            /// </summary>
            public const string DeviceConnected = "Input.DeviceConnected";
            
            /// <summary>
            /// Dispatched when an input device is disconnected. Data: InputDeviceEventData
            /// </summary>
            public const string DeviceDisconnected = "Input.DeviceDisconnected";
            
            /// <summary>
            /// Dispatched when the input scheme changes. Data: string (scheme name)
            /// </summary>
            public const string SchemeChanged = "Input.SchemeChanged";
        }
        
        // Audio events
        public static class Audio
        {
            /// <summary>
            /// Dispatched when master volume changes. Data: float (volume level 0-1)
            /// </summary>
            public const string MasterVolumeChanged = "Audio.MasterVolumeChanged";

            /// <summary>
            /// Dispatched when music volume changes. Data: float (volume level 0-1)
            /// </summary>
            public const string MusicVolumeChanged = "Audio.MusicVolumeChanged";

            /// <summary>
            /// Dispatched when SFX volume changes. Data: float (volume level 0-1)
            /// </summary>
            public const string SfxVolumeChanged = "Audio.SfxVolumeChanged";

            /// <summary>
            /// Dispatched when voice volume changes. Data: float (volume level 0-1)
            /// </summary>
            public const string VoiceVolumeChanged = "Audio.VoiceVolumeChanged";
        }

        // Content Package events
        public static class ContentPackage
        {
            /// <summary>
            /// Dispatched when a package download starts. Data: string (package ID)
            /// </summary>
            public const string DownloadStarted = "ContentPackage.DownloadStarted";

            /// <summary>
            /// Dispatched when a package download completes successfully. Data: string (package ID)
            /// </summary>
            public const string DownloadCompleted = "ContentPackage.DownloadCompleted";

            /// <summary>
            /// Dispatched when a package download fails. Data: PackageOperationErrorEventData
            /// </summary>
            public const string DownloadFailed = "ContentPackage.DownloadFailed";

            /// <summary>
            /// Dispatched when a package installation starts. Data: string (package ID)
            /// </summary>
            public const string InstallStarted = "ContentPackage.InstallStarted";

            /// <summary>
            /// Dispatched when a package installation completes successfully. Data: string (package ID)
            /// </summary>
            public const string InstallCompleted = "ContentPackage.InstallCompleted";

            /// <summary>
            /// Dispatched when a package installation fails. Data: PackageOperationErrorEventData
            /// </summary>
            public const string InstallFailed = "ContentPackage.InstallFailed";

            /// <summary>
            /// Dispatched when a package uninstall starts. Data: string (package ID)
            /// </summary>
            public const string UninstallStarted = "ContentPackage.UninstallStarted";

            /// <summary>
            /// Dispatched when a package uninstall completes successfully. Data: string (package ID)
            /// </summary>
            public const string UninstallCompleted = "ContentPackage.UninstallCompleted";

            /// <summary>
            /// Dispatched when a package uninstall fails. Data: PackageOperationErrorEventData
            /// </summary>
            public const string UninstallFailed = "ContentPackage.UninstallFailed";

            /// <summary>
            /// Dispatched when a package update starts. Data: string (package ID)
            /// </summary>
            public const string UpdateStarted = "ContentPackage.UpdateStarted";

            /// <summary>
            /// Dispatched when a package update completes successfully. Data: string (package ID)
            /// </summary>
            public const string UpdateCompleted = "ContentPackage.UpdateCompleted";

            /// <summary>
            /// Dispatched when a package update fails. Data: PackageOperationErrorEventData
            /// </summary>
            public const string UpdateFailed = "ContentPackage.UpdateFailed";

            /// <summary>
            /// Dispatched when package validation starts. Data: string (package ID)
            /// </summary>
            public const string ValidationStarted = "ContentPackage.ValidationStarted";

            /// <summary>
            /// Dispatched when package validation completes. Data: PackageValidationResultEventData
            /// </summary>
            public const string ValidationCompleted = "ContentPackage.ValidationCompleted";

            /// <summary>
            /// Dispatched when package cache cleanup starts. Data: long (target free space in bytes)
            /// </summary>
            public const string CacheCleanupStarted = "ContentPackage.CacheCleanupStarted";

            /// <summary>
            /// Dispatched when package cache cleanup completes. Data: long (freed space in bytes)
            /// </summary>
            public const string CacheCleanupCompleted = "ContentPackage.CacheCleanupCompleted";

            /// <summary>
            /// Dispatched when package cache cleanup fails. Data: PackageOperationErrorEventData
            /// </summary>
            public const string CacheCleanupFailed = "ContentPackage.CacheCleanupFailed";

            /// <summary>
            /// Dispatched during package download progress. Data: PackageProgressEventData
            /// </summary>
            public const string DownloadProgress = "ContentPackage.DownloadProgress";

            /// <summary>
            /// Dispatched during package installation progress. Data: PackageProgressEventData
            /// </summary>
            public const string InstallProgress = "ContentPackage.InstallProgress";

            /// <summary>
            /// Dispatched during package update progress. Data: PackageProgressEventData
            /// </summary>
            public const string UpdateProgress = "ContentPackage.UpdateProgress";

            /// <summary>
            /// Dispatched during package validation progress. Data: PackageValidationProgressEventData
            /// </summary>
            public const string ValidationProgress = "ContentPackage.ValidationProgress";

            /// <summary>
            /// Dispatched during cache cleanup progress. Data: StorageCleanupProgressEventData
            /// </summary>
            public const string CacheCleanupProgress = "ContentPackage.CacheCleanupProgress";
        }
    }
    
    #region Event Data Classes
    
    /// <summary>
    /// Base class for all event data classes.
    /// </summary>
    public abstract class EventData
    {
        /// <summary>
        /// Gets the timestamp when this event data was created.
        /// </summary>
        public DateTime Timestamp { get; }
        
        protected EventData()
        {
            Timestamp = DateTime.Now;
        }
    }
    
    /// <summary>
    /// Contains data related to scene loading events.
    /// </summary>
    public class SceneLoadEventData : EventData
    {
        /// <summary>
        /// Gets the name of the scene being loaded.
        /// </summary>
        public string SceneName { get; }
        
        /// <summary>
        /// Gets a value indicating whether the scene is being loaded additively.
        /// </summary>
        public bool IsAdditive { get; }
        
        /// <summary>
        /// Gets the progress of the scene loading operation (0-1).
        /// </summary>
        public float Progress { get; set; }
        
        public SceneLoadEventData(string sceneName, bool isAdditive, float progress = 0)
        {
            SceneName = sceneName;
            IsAdditive = isAdditive;
            Progress = progress;
        }
    }
    
    /// <summary>
    /// Contains error data related to scene loading failures.
    /// </summary>
    public class SceneLoadErrorEventData : SceneLoadEventData
    {
        /// <summary>
        /// Gets the error message describing why the scene load failed.
        /// </summary>
        public string ErrorMessage { get; }

        public SceneLoadErrorEventData(string sceneName, bool isAdditive, string errorMessage)
            : base(sceneName, isAdditive)
        {
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// Contains error data for failed package operations.
    /// </summary>
    public class PackageOperationErrorEventData : EventData
    {
        /// <summary>
        /// Gets the package ID that had the operation error.
        /// </summary>
        public string PackageId { get; }

        /// <summary>
        /// Gets the type of operation that failed.
        /// </summary>
        public string OperationType { get; }

        /// <summary>
        /// Gets the error message describing what went wrong.
        /// </summary>
        public string ErrorMessage { get; }

        public PackageOperationErrorEventData(string packageId, string operationType, string errorMessage)
        {
            PackageId = packageId;
            OperationType = operationType;
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// Contains result data for package validation operations.
    /// </summary>
    public class PackageValidationResultEventData : EventData
    {
        /// <summary>
        /// Gets the package ID that was validated.
        /// </summary>
        public string PackageId { get; }

        /// <summary>
        /// Gets whether the validation passed.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the list of validation errors (empty if successful).
        /// </summary>
        public System.Collections.Generic.List<string> Errors { get; }

        /// <summary>
        /// Gets the validation duration.
        /// </summary>
        public System.TimeSpan Duration { get; }

        public PackageValidationResultEventData(string packageId, bool success,
            System.Collections.Generic.List<string> errors, System.TimeSpan duration)
        {
            PackageId = packageId;
            Success = success;
            Errors = errors ?? new System.Collections.Generic.List<string>();
            Duration = duration;
        }
    }

    /// <summary>
    /// Contains progress data for package operations.
    /// </summary>
    public class PackageProgressEventData : EventData
    {
        /// <summary>
        /// Gets the package ID for the operation.
        /// </summary>
        public string PackageId { get; }

        /// <summary>
        /// Gets the operation type.
        /// </summary>
        public string OperationType { get; }

        /// <summary>
        /// Gets the current progress message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the progress value (0.0 to 1.0).
        /// </summary>
        public float Progress { get; }

        public PackageProgressEventData(string packageId, string operationType, string message, float progress)
        {
            PackageId = packageId;
            OperationType = operationType;
            Message = message;
            Progress = Mathf.Clamp01(progress);
        }
    }

    /// <summary>
    /// Contains progress data for package validation operations.
    /// </summary>
    public class PackageValidationProgressEventData : EventData
    {
        /// <summary>
        /// Gets the package ID being validated.
        /// </summary>
        public string PackageId { get; }

        /// <summary>
        /// Gets the current progress message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the progress value (0.0 to 1.0).
        /// </summary>
        public float Progress { get; }

        /// <summary>
        /// Gets the current validation step.
        /// </summary>
        public string CurrentStep { get; }

        public PackageValidationProgressEventData(string packageId, string message, float progress, string currentStep)
        {
            PackageId = packageId;
            Message = message;
            Progress = Mathf.Clamp01(progress);
            CurrentStep = currentStep;
        }
    }

    /// <summary>
    /// Contains progress data for storage cleanup operations.
    /// </summary>
    public class StorageCleanupProgressEventData : EventData
    {
        /// <summary>
        /// Gets the current progress message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the progress value (0.0 to 1.0).
        /// </summary>
        public float Progress { get; }

        /// <summary>
        /// Gets the target free space in bytes.
        /// </summary>
        public long TargetFreeSpace { get; }

        /// <summary>
        /// Gets the current freed space in bytes.
        /// </summary>
        public long CurrentFreedSpace { get; }

        public StorageCleanupProgressEventData(string message, float progress, long targetFreeSpace, long currentFreedSpace)
        {
            Message = message;
            Progress = Mathf.Clamp01(progress);
            TargetFreeSpace = targetFreeSpace;
            CurrentFreedSpace = currentFreedSpace;
        }
    }

    #endregion
} 