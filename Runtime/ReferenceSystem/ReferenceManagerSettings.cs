using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Molca.Settings;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Editor-time <b>validation database</b> for the Reference System — not the runtime registry.
    /// </summary>
    /// <remarks>
    /// This asset records known RefIds per type and per scene so editor tooling can flag missing
    /// or duplicate ids and drive the inspector pickers. It is <b>never consulted to resolve a
    /// reference at runtime</b> — the live <see cref="ReferenceManager"/> holds loaded scene
    /// MonoBehaviours and is the only resolution path. Asset (ScriptableObject) ids stored here
    /// are <i>data-identity</i> only and are not runtime-resolvable (SOs-out boundary).
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Reference Manager Settings", menuName = "Molca/Reference System/Reference Manager Settings", order = 60)]
    public class ReferenceManagerSettings : SettingModule, ISerializationCallbackReceiver
    {
        [Header("Debug Settings")]
        [SerializeField] private bool enableDebugLogging = false;

        [Header("Reference Management")]
        [SerializeField] private bool autoValidateOnScan = true;
        [SerializeField] private bool showValidationResults = true;
        [SerializeField] private bool comprehensiveSceneScanning = false;

        [Header("On Scene Save")]
        [Tooltip("When enabled, validates RefIds in the scene being saved (missing or duplicate within scene).")]
        [SerializeField] private bool validateRefIdsOnSceneSave = false;
        [Tooltip("When enabled, auto-fixes missing or duplicate RefIds in the scene being saved. Only applies when Validate Ref Ids On Scene Save is enabled.")]
        [SerializeField] private bool fixRefIdsOnSceneSave = false;

        [Tooltip("ScriptableObject / asset reference IDs (filled by full project scan).")]
        [SerializeField] private List<ReferenceTypeData> assetKnownIds = new List<ReferenceTypeData>();

        [Tooltip("Per-scene reference IDs (filled by scene scan or refresh).")]
        [SerializeField] private List<SceneKnownIdsCollection> sceneKnownIds = new List<SceneKnownIdsCollection>();

        [Header("Prefab Scanning")]
        [Tooltip("Asset paths or folder paths of prefabs to include in project scans. " +
                 "A prefab is scanned only when its path starts with at least one entry in this list. " +
                 "Leave empty to skip all prefab scanning.")]
        [SerializeField] private List<string> prefabScanPaths = new List<string>();

        [SerializeField, HideInInspector]
        private List<ReferenceTypeData> knownIds = new List<ReferenceTypeData>();

        [Serializable]
        public class ReferenceTypeData
        {
            public string type;
            public List<string> ids = new List<string>();
        }

        [Serializable]
        public class SceneKnownIdsCollection
        {
            [Tooltip("Asset path of the scene, or a synthetic key for unsaved/runtime-only scenes.")]
            public string sceneAssetPath;
            public List<ReferenceTypeData> types = new List<ReferenceTypeData>();
        }

        #region Properties

        // Internal accessors so the paired state can seed from authored defaults.
        internal bool DefaultEnableDebugLogging => enableDebugLogging;
        internal bool DefaultAutoValidateOnScan => autoValidateOnScan;
        internal bool DefaultShowValidationResults => showValidationResults;

        private ReferenceManagerState TypedState => (ReferenceManagerState)State;

        // The persisted toggles read through the runtime state when GlobalSettings
        // has created one; outside bootstrap (edit mode, tooling) they fall back to
        // the authored defaults. The SerializeFields themselves are never written
        // at runtime (SO cardinal rule).
        public bool EnableDebugLogging => TypedState?.EnableDebugLogging ?? enableDebugLogging;
        public bool AutoValidateOnScan => TypedState?.AutoValidateOnScan ?? autoValidateOnScan;
        public bool ShowValidationResults => TypedState?.ShowValidationResults ?? showValidationResults;
        public bool ComprehensiveSceneScanning => comprehensiveSceneScanning;
        public bool ValidateRefIdsOnSceneSave => validateRefIdsOnSceneSave;
        public bool FixRefIdsOnSceneSave => fixRefIdsOnSceneSave;
        public IReadOnlyList<string> PrefabScanPaths => prefabScanPaths;

        #endregion

        #region SettingModule Implementation

        public override void Initialize()
        {
            base.Initialize();

            if (enableDebugLogging)
            {
                Debug.Log($"[ReferenceManagerSettings] Initializing Reference System Settings");
            }
        }

        public override SettingState CreateState() => new ReferenceManagerState(this);

        public override void LoadSettings()
        {
            TypedState?.Load(this);

            if (EnableDebugLogging)
            {
                Debug.Log($"[ReferenceManagerSettings] Settings loaded: Debug={EnableDebugLogging}, Validation={AutoValidateOnScan}");
            }
        }

        public override void SaveSettings()
        {
            TypedState?.Save(this);

            if (EnableDebugLogging)
            {
                Debug.Log($"[ReferenceManagerSettings] Settings saved");
            }
        }

        public override void ResetToDefaults()
        {
            // Restore the runtime state to the authored defaults; the SerializeFields
            // are the defaults and are never mutated.
            var state = TypedState;
            if (state != null)
            {
                state.EnableDebugLogging = enableDebugLogging;
                state.AutoValidateOnScan = autoValidateOnScan;
                state.ShowValidationResults = showValidationResults;
                state.Save(this);
            }

            if (EnableDebugLogging)
            {
                Debug.Log($"[ReferenceManagerSettings] Reset to defaults");
            }
        }

        #endregion

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            MigrateLegacyKnownIdsIfNeeded();
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            MigrateLegacyKnownIdsIfNeeded();
        }

        private void MigrateLegacyKnownIdsIfNeeded()
        {
            if (knownIds == null || knownIds.Count == 0)
                return;

            // Lazy null-guards for lists Unity may deserialize as null — replaces
            // nothing authored, so not a cardinal-rule violation.
            if (assetKnownIds == null)
                assetKnownIds = new List<ReferenceTypeData>(); // doctor:ignore — null-guard, not a runtime mutation
            if (sceneKnownIds == null)
                sceneKnownIds = new List<SceneKnownIdsCollection>(); // doctor:ignore — null-guard, not a runtime mutation

            foreach (var td in knownIds)
            {
                if (td == null || string.IsNullOrEmpty(td.type))
                    continue;

                var dest = assetKnownIds.Find(t => t.type == td.type);
                if (dest == null)
                {
                    dest = new ReferenceTypeData { type = td.type, ids = new List<string>() };
                    assetKnownIds.Add(dest);
                }

                if (td.ids == null)
                    continue;

                foreach (var id in td.ids)
                {
                    if (!string.IsNullOrEmpty(id) && !dest.ids.Contains(id))
                        dest.ids.Add(id);
                }
            }

            knownIds.Clear();
        }

        private Dictionary<string, HashSet<string>> BuildMergedIdSets()
        {
            var merged = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            AddIntoMerged(merged, assetKnownIds);
            foreach (var sceneCol in sceneKnownIds)
            {
                if (sceneCol?.types != null)
                    AddIntoMerged(merged, sceneCol.types);
            }

            return merged;
        }

        private static void AddIntoMerged(Dictionary<string, HashSet<string>> merged, List<ReferenceTypeData> source)
        {
            if (source == null)
                return;

            foreach (var td in source)
            {
                if (td == null || string.IsNullOrEmpty(td.type) || td.ids == null)
                    continue;

                if (!merged.TryGetValue(td.type, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    merged[td.type] = set;
                }

                foreach (var id in td.ids)
                {
                    if (!string.IsNullOrEmpty(id))
                        set.Add(id);
                }
            }
        }

        #region Public Methods

        /// <summary>
        /// Get the settings instance from GlobalSettings.
        /// This follows the same pattern as other settings modules.
        /// </summary>
        public static ReferenceManagerSettings Instance
        {
            get
            {
                var settings = GlobalSettings.GetModule<ReferenceManagerSettings>();
                if (settings == null)
                {
                    Debug.LogWarning("[ReferenceManagerSettings] Not found in GlobalSettings. Make sure it's added to the modules array.");
                }
                return settings;
            }
        }

        /// <summary>
        /// Get reference statistics (distinct IDs per type across asset and all scenes).
        /// </summary>
        public Dictionary<string, int> GetReferenceStats()
        {
            var merged = BuildMergedIdSets();
            return merged.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        /// <summary>
        /// Get all known reference types.
        /// </summary>
        public List<string> GetReferenceTypes()
        {
            return BuildMergedIdSets().Keys.ToList();
        }

        /// <summary>
        /// Get all distinct IDs for a specific reference type.
        /// </summary>
        public List<string> GetReferenceIds(string refType)
        {
            var merged = BuildMergedIdSets();
            return merged.TryGetValue(refType, out var set)
                ? set.ToList()
                : new List<string>();
        }

        /// <summary>
        /// Find duplicate IDs (same id registered more than once across asset + scene collections).
        /// </summary>
        public Dictionary<string, List<string>> FindDuplicateIds()
        {
            var duplicates = new Dictionary<string, List<string>>();
            var typeOccurrences = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            void CountIds(List<ReferenceTypeData> source)
            {
                if (source == null)
                    return;

                foreach (var td in source)
                {
                    if (td == null || string.IsNullOrEmpty(td.type) || td.ids == null)
                        continue;

                    if (!typeOccurrences.TryGetValue(td.type, out var idCounts))
                    {
                        idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                        typeOccurrences[td.type] = idCounts;
                    }

                    foreach (var id in td.ids)
                    {
                        if (string.IsNullOrEmpty(id))
                            continue;

                        idCounts.TryGetValue(id, out var c);
                        idCounts[id] = c + 1;
                    }
                }
            }

            CountIds(assetKnownIds);
            foreach (var col in sceneKnownIds)
            {
                if (col?.types != null)
                    CountIds(col.types);
            }

            foreach (var kv in typeOccurrences)
            {
                var dup = kv.Value.Where(x => x.Value > 1).Select(x => x.Key).ToList();
                if (dup.Count > 0)
                    duplicates[kv.Key] = dup;
            }

            return duplicates;
        }

        #endregion
    }
}
