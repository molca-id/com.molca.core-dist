using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

namespace Molca
{
    /// <summary>
    /// Instance API of the scene-loading subsystem. Resolve via
    /// <c>RuntimeManager.GetService&lt;ISceneLoader&gt;()</c> or inject with
    /// <c>[Inject] ISceneLoader</c>. Replaces the legacy static surface on
    /// <see cref="SceneLoadManager"/>, which remains as obsolete shims.
    /// </summary>
    public interface ISceneLoader
    {
        /// <summary>The currently active scene.</summary>
        Scene ActiveScene { get; }

        /// <summary>Queues a build-settings scene load by name.</summary>
        /// <param name="sceneName">Scene name as registered in Build Settings.</param>
        /// <param name="mode">Single replaces all scenes; Additive layers on top.</param>
        /// <param name="onComplete">Invoked with the loaded scene.</param>
        void LoadScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single, Action<Scene> onComplete = null);

        /// <summary>Queues an Addressable scene load.</summary>
        /// <returns>False if the reference is null or scene loading is unavailable.</returns>
        bool LoadAddressableScene(AssetReference sceneRef, LoadSceneMode mode = LoadSceneMode.Single, Action<Scene> onComplete = null);

        /// <summary>Loads the next scene in build order, if any.</summary>
        void LoadNextScene(LoadSceneMode mode = LoadSceneMode.Single, Action<Scene> onComplete = null);

        /// <summary>Unloads a scene (Addressable handles are released).</summary>
        /// <returns>Completes when the scene has finished unloading. Awaiting is optional.</returns>
        Awaitable UnloadScene(string sceneName);

        /// <summary>
        /// Unloads every Addressable scene loaded through this manager. Call before
        /// a Single-mode transition so no stale Addressable handles leak.
        /// </summary>
        Awaitable UnloadAllAddressableScenes();

        /// <summary>Resolves the name of the next scene in build order.</summary>
        bool TryGetNextSceneName(out string sceneName);

        /// <summary>Returns true if a scene with the given name is currently loaded.</summary>
        bool IsSceneLoaded(string sceneName);
    }
}
