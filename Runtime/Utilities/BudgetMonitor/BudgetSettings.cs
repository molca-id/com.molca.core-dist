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
    }
}
