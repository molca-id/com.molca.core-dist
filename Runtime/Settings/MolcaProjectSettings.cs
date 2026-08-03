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
        // No packaged template path: the live asset is generated from field initializers, never cloned
        // from a .asset inside the package. See CreateEditorInstance.
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
        /// <summary>
        /// Opaque control-plane identity when this Unity repository is connected to a Molca project.
        /// An identifier alone grants no access; <see cref="ProjectBinding"/> proves an authorized binding.
        /// </summary>
        public string ProjectId
        {
            get => projectId;
            set => projectId = value;
        }

        [SerializeField] private string projectCode = "";
        /// <summary>Short, support-friendly control-plane code (for example <c>MOLCA-A1B2C3</c>).</summary>
        public string ProjectCode
        {
            get => projectCode;
            set => projectCode = value;
        }

        [SerializeField] private string contentChannel = "stable";
        /// <summary>
        /// Content channel this project's builds request: <c>stable</c>, <c>beta</c>, or <c>internal</c>.
        /// </summary>
        /// <remarks>
        /// Serialized on the project asset rather than in per-user <c>EditorPrefs</c>, deliberately. Which
        /// channel a build ships against is a reviewable project decision — two developers building the same
        /// commit must produce players that resolve the same content, and a per-user preference silently
        /// breaks that.
        ///
        /// It is a <em>request</em>, not an entitlement. The control plane records the channel on the build
        /// token only if the requesting developer holds <c>project.build.channel.select</c>, and thereafter
        /// resolves content from the stored row rather than from anything a player sends. Setting this to
        /// <c>internal</c> without that capability fails at mint time, not at runtime.
        /// </remarks>
        public string ContentChannel
        {
            get => string.IsNullOrWhiteSpace(contentChannel) ? "stable" : contentChannel;
            set => contentChannel = value;
        }

        [SerializeField, TextArea(2, 5)] private string projectBinding = "";
        /// <summary>
        /// Signed, non-secret receipt proving an authorized owner/manager connected this repository.
        /// Safe to commit; API access still requires the signed-in developer entitlement.
        /// </summary>
        public string ProjectBinding
        {
            get => projectBinding;
            set => projectBinding = value;
        }

        [SerializeField] private int projectBindingVersion;
        /// <summary>Schema version of <see cref="ProjectBinding"/>; zero when not connected.</summary>
        public int ProjectBindingVersion
        {
            get => projectBindingVersion;
            set => projectBindingVersion = value;
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
        /// True if settings already exist at the current live path or the legacy 1.x path. Unlike
        /// <see cref="Instance"/>, this never creates or moves an asset — first-run and upgrade checks use
        /// it to distinguish a genuinely fresh project from a pre-relocation consumer project.
        /// </summary>
        public static bool LiveAssetExists =>
            LiveAssetExistsAt(path => AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(path) != null);

        /// <summary>Checks both durable settings locations without creating or moving either asset.</summary>
        internal static bool LiveAssetExistsAt(Func<string, bool> exists) =>
            exists != null && (exists(ASSET_PATH) || exists(OLD_ASSET_PATH));

        /// <summary>Loads existing settings without invoking the getter's create-or-move behavior.</summary>
        internal static MolcaProjectSettings LoadExistingAssetWithoutMigration() =>
            AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(ASSET_PATH)
            ?? AssetDatabase.LoadAssetAtPath<MolcaProjectSettings>(OLD_ASSET_PATH);

        /// <summary>
        /// Seeds the live project instance at <see cref="ASSET_PATH"/> when none exists yet, generated
        /// from this type's own field initializers.
        /// </summary>
        /// <returns>The newly created live <see cref="MolcaProjectSettings"/> instance.</returns>
        /// <remarks>
        /// This used to clone a <c>.asset</c> shipped inside the Core package. Generating instead means the
        /// package ships no editable configuration at all: nothing to re-GUID when it is copied, nothing to
        /// drift from the schema when a field is added, and no asset a consumer can edit only to have the
        /// next package upgrade replace it. The defaults are the ones declared on the fields, so they are
        /// defined in exactly one place.
        /// <para>What this deliberately does not do is configure the project. It produces the minimum that
        /// lets the editor load and the bootstrap check run; choosing a RuntimeManager and enabling features
        /// is the starter's job (<c>Molca ▸ Hub ▸ Remediation</c> and the onboarding wizard).</para>
        /// </remarks>
        private static MolcaProjectSettings CreateEditorInstance()
        {
            EnsureAssetDirectory(ASSET_PATH);

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
