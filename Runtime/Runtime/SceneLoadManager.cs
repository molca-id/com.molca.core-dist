using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Linq;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using Molca.Events;

namespace Molca
{
    /// <summary>
    /// Manages scene loading operations and tracks scene states in the application.
    /// New code should use the <see cref="ISceneLoader"/> instance API (resolve via
    /// <c>RuntimeManager.GetService&lt;ISceneLoader&gt;()</c>); the static members
    /// remain as obsolete compatibility shims.
    /// </summary>
    public class SceneLoadManager : RuntimeSubsystem, ISceneLoader
    {
        private static SceneLoadManager _instance;

        [Obsolete("Use ISceneLoader.ActiveScene (RuntimeManager.GetService<ISceneLoader>()).")]
        public static Scene ActiveScene => SceneManager.GetActiveScene();

        private readonly HashSet<Scene> _loadedScenes = new();
        private readonly Queue<SceneLoadRequest> _loadQueue = new();
        private readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> _addressableHandles = new();
        private bool _isProcessingQueue;
        private bool _isLoading;
        private SceneLoadRequest _activeRequest;

        private class SceneLoadRequest
        {
            public string SceneName { get; }
            public AssetReference SceneRef { get; }
            public LoadSceneMode LoadMode { get; }
            public Action<Scene> Callback { get; }
            public bool IsAddressable { get; }
            public SceneLoadEventData EventData { get; }

            public SceneLoadRequest(string sceneName, LoadSceneMode mode, Action<Scene> callback, bool isAddressable)
            {
                SceneName = sceneName;
                LoadMode = mode;
                Callback = callback;
                IsAddressable = isAddressable;
                EventData = new SceneLoadEventData(sceneName, mode == LoadSceneMode.Additive, 0);
            }

            public SceneLoadRequest(AssetReference sceneRef, LoadSceneMode mode, Action<Scene> callback)
            {
                SceneRef = sceneRef;
                LoadMode = mode;
                Callback = callback;
                IsAddressable = true;
                EventData = new SceneLoadEventData(sceneRef.ToString(), mode == LoadSceneMode.Additive, 0);
            }
        }

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            if (_instance != null)
            {
                Debug.LogError("Multiple instances of SceneLoadManager detected!");
                // Still signal completion — bootstrap blocks until every subsystem
                // invokes its finishCallback (otherwise this duplicate stalls boot
                // for the full init timeout).
                finishCallback?.Invoke(this);
                return;
            }

            _instance = this;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            finishCallback?.Invoke(this);
        }

        public override void Teardown()
        {
            base.Teardown();
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            // Drop the legacy-shim singleton so a torn-down subsystem can't be reached.
            if (_instance == this)
                _instance = null;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _loadedScenes.Add(scene);

            // Use current request's event data if available
            if (_activeRequest != null && _activeRequest.SceneName == scene.name)
            {
                TypedEvents.SceneLoadCompleted.Dispatch(_activeRequest.EventData);
                _activeRequest = null;
            }
            else
            {
                // Fallback for scenes loaded outside our system
                TypedEvents.SceneLoadCompleted.Dispatch(new SceneLoadEventData(scene.name, false, (int)mode));
            }
        }

        private void HandleSceneUnloaded(Scene scene)
        {
            _loadedScenes.Remove(scene);
            TypedEvents.SceneUnloadCompleted.Dispatch(scene.name);
        }

        #region Instance API (ISceneLoader)

        // Explicit implementations: the legacy statics keep these names
        // (protected-zone rule), so the instance API lives on the interface.

        Scene ISceneLoader.ActiveScene => SceneManager.GetActiveScene();

        void ISceneLoader.LoadScene(string sceneName, LoadSceneMode mode, Action<Scene> onComplete)
            => LoadSceneCore(sceneName, mode, onComplete);

        bool ISceneLoader.LoadAddressableScene(AssetReference sceneRef, LoadSceneMode mode, Action<Scene> onComplete)
            => LoadAddressableSceneCore(sceneRef, mode, onComplete);

        void ISceneLoader.LoadNextScene(LoadSceneMode mode, Action<Scene> onComplete)
            => LoadNextSceneCore(mode, onComplete);

        Awaitable ISceneLoader.UnloadScene(string sceneName) => UnloadSceneCore(sceneName);
        Awaitable ISceneLoader.UnloadAllAddressableScenes() => UnloadAllAddressableScenesCore();
        bool ISceneLoader.TryGetNextSceneName(out string sceneName) => TryGetNextSceneNameCore(out sceneName);
        bool ISceneLoader.IsSceneLoaded(string sceneName) => IsSceneLoadedCore(sceneName);

        private void LoadSceneCore(string sceneName, LoadSceneMode mode, Action<Scene> onComplete)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                Debug.LogError("Scene name cannot be null or empty");
                return;
            }

            if (!Application.isPlaying)
            {
                Debug.LogWarning("Cannot load scenes in edit mode");
                return;
            }

            var request = new SceneLoadRequest(sceneName, mode, onComplete, false);
            TypedEvents.SceneLoadStarted.Dispatch(request.EventData);
            QueueSceneLoad(request);
        }

        private bool LoadAddressableSceneCore(AssetReference sceneRef, LoadSceneMode mode, Action<Scene> onComplete)
        {
            if (sceneRef == null)
            {
                Debug.LogError("Scene reference cannot be null");
                return false;
            }

            if (!Application.isPlaying)
            {
                Debug.LogWarning("Cannot load scenes in edit mode");
                return false;
            }

            var request = new SceneLoadRequest(sceneRef, mode, onComplete);
            TypedEvents.SceneLoadStarted.Dispatch(request.EventData);
            QueueSceneLoad(request);
            return true;
        }

        private void LoadNextSceneCore(LoadSceneMode mode, Action<Scene> onComplete)
        {
            if (!Application.isPlaying) return;
            if (!TryGetNextSceneNameCore(out string nextSceneName))
            {
                Debug.LogError("No next scene available in build.");
                return;
            }

            LoadSceneCore(nextSceneName, mode, onComplete);
        }

        private async Awaitable UnloadSceneCore(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                Debug.LogError("Scene name cannot be null or empty");
                return;
            }

            TypedEvents.SceneUnloadStarted.Dispatch(sceneName);

            if (_addressableHandles.TryGetValue(sceneName, out var handle))
            {
                var unloadOp = Addressables.UnloadSceneAsync(handle);
                while (!unloadOp.IsDone)
                    await Awaitable.NextFrameAsync();
                _addressableHandles.Remove(sceneName);
            }
            else
            {
                await SceneManager.UnloadSceneAsync(sceneName);
            }
        }

        private async Awaitable UnloadAllAddressableScenesCore()
        {
            foreach (var kvp in _addressableHandles)
            {
                TypedEvents.SceneUnloadStarted.Dispatch(kvp.Key);
                var unloadOp = Addressables.UnloadSceneAsync(kvp.Value);
                while (!unloadOp.IsDone)
                    await Awaitable.NextFrameAsync();
            }
            _addressableHandles.Clear();
        }

        private static bool TryGetNextSceneNameCore(out string sceneName)
        {
            sceneName = null;
            int nextIndex = SceneManager.GetActiveScene().buildIndex + 1;
            if (nextIndex >= SceneManager.sceneCountInBuildSettings)
            {
                Debug.LogError("No next scene available in build settings");
                return false;
            }

            sceneName = System.IO.Path.GetFileNameWithoutExtension(
                SceneUtility.GetScenePathByBuildIndex(nextIndex));
            return true;
        }

        private bool IsSceneLoadedCore(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            return _loadedScenes.Any(scene => scene.name == sceneName);
        }

        #endregion

        #region Legacy static API (obsolete shims)

        [Obsolete("Use ISceneLoader.LoadScene (RuntimeManager.GetService<ISceneLoader>()).")]
        public static void LoadScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single, Action<Scene> onComplete = null)
        {
            _instance?.LoadSceneCore(sceneName, mode, onComplete);
        }

        [Obsolete("Use ISceneLoader.LoadAddressableScene (RuntimeManager.GetService<ISceneLoader>()).")]
        public static bool LoadAddressableScene(AssetReference sceneRef, LoadSceneMode mode = LoadSceneMode.Single, Action<Scene> onComplete = null)
        {
            return _instance != null && _instance.LoadAddressableSceneCore(sceneRef, mode, onComplete);
        }

        [Obsolete("Use ISceneLoader.LoadNextScene (RuntimeManager.GetService<ISceneLoader>()).")]
        public static void LoadNextScene(LoadSceneMode mode = LoadSceneMode.Single, Action<Scene> onComplete = null)
        {
            _instance?.LoadNextSceneCore(mode, onComplete);
        }

        /// <returns>Completes when the scene has finished unloading. Awaiting is optional.</returns>
        [Obsolete("Use ISceneLoader.UnloadScene (RuntimeManager.GetService<ISceneLoader>()).")]
        public static async Awaitable UnloadScene(string sceneName)
        {
            if (_instance == null) return;
            await _instance.UnloadSceneCore(sceneName);
        }

        /// <summary>
        /// Unloads every Addressable scene that was loaded through this manager.
        /// Call this before transitioning to a Single-mode scene to ensure no stale
        /// Addressable handles survive the transition and leak memory.
        /// </summary>
        [Obsolete("Use ISceneLoader.UnloadAllAddressableScenes (RuntimeManager.GetService<ISceneLoader>()).")]
        public static async Awaitable UnloadAllAddressableScenes()
        {
            if (_instance == null) return;
            await _instance.UnloadAllAddressableScenesCore();
        }

        [Obsolete("Use ISceneLoader.TryGetNextSceneName (RuntimeManager.GetService<ISceneLoader>()).")]
        public static bool TryGetNextSceneName(out string sceneName)
        {
            return TryGetNextSceneNameCore(out sceneName);
        }

        [Obsolete("Use ISceneLoader.IsSceneLoaded (RuntimeManager.GetService<ISceneLoader>()).")]
        public static bool IsSceneLoaded(string sceneName)
        {
            return _instance != null && _instance.IsSceneLoadedCore(sceneName);
        }

        #endregion

        private void QueueSceneLoad(SceneLoadRequest request)
        {
            _loadQueue.Enqueue(request);
            if (!_isProcessingQueue)
            {
                // Explicit fire-and-forget: the loop owns its exceptions and
                // unwinds on Shutdown via ShutdownToken.
                _ = ProcessLoadQueueAsync();
            }
        }

        private async Awaitable ProcessLoadQueueAsync()
        {
            _isProcessingQueue = true;

            try
            {
                while (_loadQueue.Count > 0)
                {
                    if (ShutdownToken.IsCancellationRequested) return;

                    if (_isLoading)
                    {
                        await Awaitable.NextFrameAsync(ShutdownToken);
                        continue;
                    }

                    var request = _loadQueue.Dequeue();
                    await LoadSceneAsync(request);
                }
            }
            catch (OperationCanceledException)
            {
                // Subsystem shut down while the queue was draining — exit quietly.
            }
            finally
            {
                _isProcessingQueue = false;
            }
        }

        private async Awaitable LoadSceneAsync(SceneLoadRequest request)
        {
            _isLoading = true;
            _activeRequest = request;

            try
            {
                if (request.LoadMode == LoadSceneMode.Single)
                    await UnloadAllAddressableScenesCore();

                if (request.IsAddressable)
                {
                    // Load addressable scene
                    var loadOperation = request.SceneRef.LoadSceneAsync(request.LoadMode);

                    while (!loadOperation.IsDone)
                    {
                        request.EventData.Progress = loadOperation.PercentComplete;
                        await Awaitable.NextFrameAsync();
                    }

                    if (loadOperation.Status == AsyncOperationStatus.Succeeded)
                    {
                        var scene = loadOperation.Result.Scene;
                        _addressableHandles[scene.name] = loadOperation;
                        request.Callback?.Invoke(scene);
                    }
                    else
                    {
                        Debug.LogError($"Failed to load addressable scene: {request.SceneRef}");
                        TypedEvents.SceneLoadFailed.Dispatch(new SceneLoadErrorEventData(request.EventData.SceneName, request.EventData.IsAdditive, "Failed to load addressable scene"));
                    }
                }
                else
                {
                    // Load regular scene
                    var loadOperation = SceneManager.LoadSceneAsync(request.SceneName, request.LoadMode);
                    loadOperation.allowSceneActivation = false;

                    while (loadOperation.progress < 0.9f)
                    {
                        request.EventData.Progress = loadOperation.progress;
                        await Awaitable.NextFrameAsync();
                    }

                    loadOperation.allowSceneActivation = true;
                    await Awaitable.NextFrameAsync();

                    var scene = SceneManager.GetSceneByName(request.SceneName);
                    request.Callback?.Invoke(scene);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading scene: {e}");
                TypedEvents.SceneLoadFailed.Dispatch(new SceneLoadErrorEventData(request.EventData.SceneName, request.EventData.IsAdditive, e.Message));
            }
            finally
            {
                _isLoading = false;
                _activeRequest = null;
            }
        }

        public override void Shutdown()
        {
            _loadQueue.Clear();
            _isProcessingQueue = false;
            _isLoading = false;
            _activeRequest = null;
            _loadedScenes.Clear();

            // Cancels ShutdownToken (unwinding the load-queue loop) and runs Teardown.
            base.Shutdown();

            // Release Addressable scene handles. The sync Shutdown pipeline cannot
            // await, so this is explicit fire-and-forget into a method that awaits
            // each unload to completion and owns its exceptions (previously the bare
            // discard dropped unload failures unobserved).
            _ = ReleaseAddressableHandlesAsync();
        }

        private async Awaitable ReleaseAddressableHandlesAsync()
        {
            try
            {
                await UnloadAllAddressableScenesCore();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneLoadManager] Failed to unload addressable scenes during shutdown: {e}");
            }
        }
    }
}
