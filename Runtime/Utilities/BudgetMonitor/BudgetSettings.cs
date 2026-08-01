using UnityEngine;

namespace Molca.Utilities
{
    /// <summary>
    /// Authored performance budget thresholds. Read-only at runtime — never mutated.
    /// Clamping to minimum-safe values happens on the property accessors, not on the asset.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "BudgetSettings", menuName = "Molca/Settings/Budget Settings", order = 10)]
    public class BudgetSettings : ScriptableObject
    {
        [Header("Performance Budgets")]
        [SerializeField, Tooltip("Minimum acceptable FPS")] private float minFPS = 30f;
        [SerializeField, Tooltip("Maximum acceptable memory usage in MB")] private float maxMemoryMB = 500f;
        [SerializeField, Tooltip("Maximum acceptable texture memory in MB")] private float maxTextureMemoryMB = 200f;
        [SerializeField, Tooltip("Maximum acceptable active GameObjects")] private int maxGameObjects = 1000;
        [SerializeField, Tooltip("Maximum acceptable unique material instances")] private int maxMaterialInstances = 100;
        [SerializeField, Tooltip("Maximum acceptable unique mesh instances")] private int maxMeshInstances = 50;

        [Header("Rendering Budgets")]
        [SerializeField, Tooltip("Maximum acceptable draw calls")] private int maxDrawCalls = 100;
        [SerializeField, Tooltip("Maximum acceptable render batches")] private int maxBatches = 50;
        [SerializeField, Tooltip("Maximum acceptable SetPass calls")] private int maxSetPassCalls = 30;
        [SerializeField, Tooltip("Maximum acceptable triangle count")] private int maxTriangles = 100000;

        public float MinFPS => Mathf.Max(minFPS, 1f);
        public float MaxMemoryMB => Mathf.Max(maxMemoryMB, 1f);
        public float MaxTextureMemoryMB => Mathf.Max(maxTextureMemoryMB, 1f);
        public int MaxGameObjects => Mathf.Max(maxGameObjects, 1);
        public int MaxMaterialInstances => Mathf.Max(maxMaterialInstances, 1);
        public int MaxMeshInstances => Mathf.Max(maxMeshInstances, 1);
        public int MaxDrawCalls => Mathf.Max(maxDrawCalls, 1);
        public int MaxBatches => Mathf.Max(maxBatches, 1);
        public int MaxSetPassCalls => Mathf.Max(maxSetPassCalls, 1);
        public int MaxTriangles => Mathf.Max(maxTriangles, 100);

        // --- Platform presets ---------------------------------------------------------------------------

        /// <summary>Creates a budget preloaded with the thresholds Molca recommends for a platform class.</summary>
        /// <param name="preset">Which platform class to preload.</param>
        /// <returns>A new, unsaved instance. Name it after the preset so the resolver can match it.</returns>
        /// <remarks>
        /// <para>These numbers used to live in three <c>.asset</c> files shipped inside the package, which
        /// made them un-editable in an immutable install and silently replaced on every upgrade. Holding
        /// them here instead lets the project starter generate the assets into project space, where they
        /// are the author's to tune — the package ships the recommendation, the project owns the file.</para>
        /// <para><see cref="BudgetSettingsProvider"/> matches candidates by <b>asset name</b>, so an asset
        /// generated from a preset must be named via <see cref="PresetAssetName"/> to be resolved.</para>
        /// </remarks>
        public static BudgetSettings Create(BudgetPreset preset)
        {
            var settings = CreateInstance<BudgetSettings>();
            switch (preset)
            {
                case BudgetPreset.PC:
                    settings.minFPS = 60f;
                    settings.maxMemoryMB = 3000f;
                    settings.maxTextureMemoryMB = 1500f;
                    settings.maxGameObjects = 10000;
                    settings.maxMaterialInstances = 1000;
                    settings.maxMeshInstances = 500;
                    settings.maxDrawCalls = 1500;
                    settings.maxBatches = 1200;
                    settings.maxSetPassCalls = 300;
                    settings.maxTriangles = 1500000;
                    break;

                case BudgetPreset.Mobile:
                    settings.minFPS = 60f;
                    settings.maxMemoryMB = 2000f;
                    settings.maxTextureMemoryMB = 1000f;
                    settings.maxGameObjects = 2000;
                    settings.maxMaterialInstances = 400;
                    settings.maxMeshInstances = 200;
                    settings.maxDrawCalls = 120;
                    settings.maxBatches = 100;
                    settings.maxSetPassCalls = 60;
                    settings.maxTriangles = 100000;
                    break;

                // 72 Hz is the Quest display floor, not a preference: miss it and the compositor reprojects.
                case BudgetPreset.Quest:
                    settings.minFPS = 72f;
                    settings.maxMemoryMB = 2500f;
                    settings.maxTextureMemoryMB = 1500f;
                    settings.maxGameObjects = 4000;
                    settings.maxMaterialInstances = 500;
                    settings.maxMeshInstances = 300;
                    settings.maxDrawCalls = 200;
                    settings.maxBatches = 180;
                    settings.maxSetPassCalls = 100;
                    settings.maxTriangles = 750000;
                    break;
            }

            settings.name = PresetAssetName(preset);
            return settings;
        }

        /// <summary>The asset name a preset must carry for <see cref="BudgetSettingsProvider"/> to match it.</summary>
        /// <param name="preset">The preset.</param>
        /// <returns>The required asset name, e.g. <c>"Quest BudgetSettings"</c>.</returns>
        public static string PresetAssetName(BudgetPreset preset) => $"{preset} BudgetSettings";
    }

    /// <summary>A platform class with recommended performance thresholds.</summary>
    /// <remarks>
    /// Each name is also a platform token <see cref="BudgetSettingsProvider.TokensFor"/> emits, which is
    /// what lets an asset named after its preset resolve on the matching platform.
    /// </remarks>
    public enum BudgetPreset
    {
        /// <summary>Desktop and editor.</summary>
        PC,

        /// <summary>Phones and tablets.</summary>
        Mobile,

        /// <summary>Standalone Quest headsets.</summary>
        Quest,
    }
}
