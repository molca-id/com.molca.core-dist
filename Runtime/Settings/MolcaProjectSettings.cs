using UnityEngine;
using Molca.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#else
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
#endif

namespace Molca
{
    /// <summary>
    /// Central project settings for Molca framework.
    /// Runtime-accessible properties are defined here.
    /// Editor-only properties are in MolcaProjectSettings.Editor.cs
    /// </summary>
    public class MolcaProjectSettings : ScriptableObject
    {
        private static MolcaProjectSettings instance;
        // Live, editable instance lives in consumer/project space — NOT inside the (read-only)
        // Core package. A binary/read-only Core package cannot contain editable config.
        private const string ASSET_PATH = "Assets/_Molca/Settings/MolcaProjectSettings.asset";
        // Read-only seed shipped inside the Core package. Cloned into ASSET_PATH on first
        // access when the consumer has no live instance yet. Never written to at runtime.
        private const string DEFAULT_TEMPLATE_PATH = "Packages/com.molca.core/Runtime/Settings/Defaults/MolcaProjectSettings.asset";
        // Legacy location migrated forward into ASSET_PATH if still present.
        private const string OLD_ASSET_PATH = "Assets/_Molca/Resources/MolcaProjectSettings.asset";
        private const string ADDRESSABLE_KEY = "MolcaProjectSettings"; // Addressable address/key for runtime loading
#if !UNITY_EDITOR
#if UNITY_WEBGL
        private static bool isLoading;
#endif
        private static AsyncOperationHandle<MolcaProjectSettings> loadHandle;
        private static AwaitableCompletionSource<MolcaProjectSettings> loadCompletion;
#endif
        
        public static MolcaProjectSettings Instance
        {
            get
            {
                if (instance == null)
                {
#if UNITY_EDITOR
                    // In editor, resolve the live instance from consumer/project space.
                    instance = AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(ASSET_PATH);
                    if (instance == null)
                    {
                        // Migrate forward from the legacy Resources location if it still exists.
                        var oldInstance = AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(OLD_ASSET_PATH);
                        if (oldInstance != null)
                        {
                            EnsureAssetDirectory(ASSET_PATH);
                            string error = AssetDatabase.MoveAsset(OLD_ASSET_PATH, ASSET_PATH);
                            if (string.IsNullOrEmpty(error))
                            {
                                instance = AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(ASSET_PATH);
                            }
                            else
                            {
                                Debug.LogWarning($"Failed to migrate MolcaProjectSettings from '{OLD_ASSET_PATH}': {error}. Seeding from package default.");
                                instance = CreateEditorInstance();
                            }
                        }
                        else
                        {
                            // No live instance yet — seed one from the read-only package default.
                            instance = CreateEditorInstance();
                        }
                    }
#else
                    // At runtime, use Addressables to load without Resources folder
                    // The asset must be marked as Addressable with the key "MolcaProjectSettings"
                    try
                    {
#if UNITY_WEBGL
                        if (!isLoading)
                        {
                            isLoading = true;
                            _ = LoadAsync();
                        }

                        if (loadHandle.IsDone)
                        {
                            instance = loadHandle.Result;
                        }
#else
                        var handle = Addressables.LoadAssetAsync<MolcaProjectSettings>(ADDRESSABLE_KEY);
                        instance = handle.WaitForCompletion(); // Synchronous wait for early initialization

                        if (instance == null)
                        {
                            Debug.LogError($"MolcaProjectSettings not found at Addressable key '{ADDRESSABLE_KEY}'! " +
                                           $"Please ensure the asset at {ASSET_PATH} is marked as Addressable with this key.");
                        }
                        else
                        {
                            // Keep handle alive to prevent unloading
                            Addressables.ResourceManager.Acquire(handle);
                        }
#endif
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Failed to load MolcaProjectSettings from Addressables: {ex.Message}. " +
                                     $"Please ensure the asset at {ASSET_PATH} is marked as Addressable with key '{ADDRESSABLE_KEY}'.");
                    }
#endif
                }
                return instance;
            }
        }

        [SerializeField, Expandable] private GlobalSettings globalSettings;
        public GlobalSettings GlobalSettings
        {
            get => globalSettings;
            set => globalSettings = value;
        }

        [SerializeField, Expandable] private RuntimeManager runtimeManager;
        public RuntimeManager RuntimeManager
        {
            get => runtimeManager;
            set => runtimeManager = value;
        }

        [SerializeField] private string companyName = "Molca";
        public string CompanyName
        {
            get => companyName;
            set => companyName = value;
        }

        [SerializeField] private string projectName = "Molca Project";
        public string ProjectName
        {
            get => projectName;
            set => projectName = value;
        }

        [SerializeField] private Sprite projectLogo;
        public Sprite ProjectLogo
        {
            get => projectLogo;
            set => projectLogo = value;
        }

        [SerializeField] private string projectId = "";
        public string ProjectId
        {
            get => projectId;
            set => projectId = value;
        }

        [SerializeField] private List<BootstrapExtension> bootstrapExtensions = new List<BootstrapExtension>();
        /// <summary>
        /// Optional <see cref="BootstrapExtension"/> assets invoked by
        /// <see cref="RuntimeManager"/> during bootstrap, after the RuntimeManager prefab
        /// is instantiated and before <see cref="GlobalSettings.Initialize"/> runs.
        /// SDK layers use this list to register layer-specific bootstrap hooks
        /// without subclassing this asset.
        /// </summary>
        /// <remarks>
        /// Extensions are invoked in list order. Each <see cref="BootstrapExtension.OnBootstrap"/>
        /// is awaited before the next runs. Null entries are skipped with a warning.
        /// </remarks>
        public IReadOnlyList<BootstrapExtension> BootstrapExtensions
            => bootstrapExtensions ?? (IReadOnlyList<BootstrapExtension>)Array.Empty<BootstrapExtension>();

#if !UNITY_EDITOR
        /// <summary>
        /// Async load for runtime platforms that cannot block (WebGL).
        /// </summary>
        public static Awaitable<MolcaProjectSettings> LoadAsync()
        {
            if (instance != null)
            {
                var completed = new AwaitableCompletionSource<MolcaProjectSettings>();
                completed.SetResult(instance);
                return completed.Awaitable;
            }

            if (loadCompletion != null)
            {
                return loadCompletion.Awaitable;
            }

            loadCompletion = new AwaitableCompletionSource<MolcaProjectSettings>();
#if UNITY_WEBGL
            if (!isLoading)
            {
                isLoading = true;
                _ = LoadAsyncInternal();
            }
#else
            _ = LoadAsyncInternal();
#endif
            return loadCompletion.Awaitable;
        }

        private static async Awaitable<MolcaProjectSettings> LoadAsyncInternal()
        {
            try
            {
                if (!HasAddressablesInitialized())
                {
                    var initHandle = Addressables.InitializeAsync();
                    await RuntimeManager.AwaitHandle(initHandle);
                }

                loadHandle = Addressables.LoadAssetAsync<MolcaProjectSettings>(ADDRESSABLE_KEY);
                await RuntimeManager.AwaitHandle(loadHandle);
                instance = loadHandle.Result;

                if (instance == null)
                {
                    Debug.LogError($"MolcaProjectSettings not found at Addressable key '{ADDRESSABLE_KEY}'! " +
                                   $"Please ensure the asset at {ASSET_PATH} is marked as Addressable with this key.");
                }
                else
                {
                    // Keep handle alive to prevent unloading
                    Addressables.ResourceManager.Acquire(loadHandle);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load MolcaProjectSettings from Addressables: {ex.Message}. " +
                               $"Please ensure the asset at {ASSET_PATH} is marked as Addressable with key '{ADDRESSABLE_KEY}'.");
            }

            if (loadCompletion != null)
            {
                loadCompletion.SetResult(instance);
            }

            return instance;
        }

        

        private static bool HasAddressablesInitialized()
        {
            return Addressables.ResourceLocators != null && Addressables.ResourceLocators.Count() > 0;
        }
#endif

#if UNITY_EDITOR
        /// <summary>
        /// True if the live, consumer-space settings asset (<see cref="ASSET_PATH"/>) already exists on
        /// disk. Unlike <see cref="Instance"/>, this never creates it — used by first-run checks (e.g.
        /// the Onboarding Wizard's auto-offer) that need to know whether the project is genuinely fresh
        /// without triggering the clone-on-first-access side effect.
        /// </summary>
        public static bool LiveAssetExists =>
            AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(ASSET_PATH) != null;

        /// <summary>
        /// Seeds the live project instance at <see cref="ASSET_PATH"/> when none exists yet.
        /// Clones the read-only package default at <see cref="DEFAULT_TEMPLATE_PATH"/> into
        /// consumer space so the Core package is never written to; falls back to a fresh
        /// blank instance if the template is missing.
        /// </summary>
        /// <returns>The newly created live <see cref="MolcaProjectSettings"/> instance.</returns>
        private static MolcaProjectSettings CreateEditorInstance()
        {
            EnsureAssetDirectory(ASSET_PATH);

            // Prefer cloning the shipped default so consumers inherit sensible Core defaults.
            var template = AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(DEFAULT_TEMPLATE_PATH);
            if (template != null && AssetDatabase.CopyAsset(DEFAULT_TEMPLATE_PATH, ASSET_PATH))
            {
                AssetDatabase.SaveAssets();
                return AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(ASSET_PATH);
            }

            // Template unavailable — create an empty instance so the project still boots.
            Debug.LogWarning($"MolcaProjectSettings default template not found at '{DEFAULT_TEMPLATE_PATH}'. Creating a blank instance at '{ASSET_PATH}'.");
            var created = CreateInstance<MolcaProjectSettings>();
            AssetDatabase.CreateAsset(created, ASSET_PATH);
            AssetDatabase.SaveAssets();
            return created;
        }

        /// <summary>Ensures the parent directory of <paramref name="assetPath"/> exists on disk.</summary>
        /// <param name="assetPath">Project-relative asset path whose containing folder must exist.</param>
        private static void EnsureAssetDirectory(string assetPath)
        {
            string directory = System.IO.Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                System.IO.Directory.CreateDirectory(directory);
        }
#endif
    }
}