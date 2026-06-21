using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Profiling;
#endif

namespace Molca.Utilities
{
    /// <summary>
    /// Collects performance, memory, scene, and rendering metrics.
    /// Pure C# — no MonoBehaviour dependency; unit-testable in isolation.
    /// </summary>
    internal class BudgetMetricCollector
    {
        private const float WarningThreshold = 0.8f;

        private readonly BudgetSettings _settings;

        internal BudgetMetricCollector(BudgetSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Populates <paramref name="metrics"/> with all currently available metrics.
        /// Existing keys are overwritten; metrics whose data sources are unavailable are removed.
        /// </summary>
        /// <param name="metrics">Dictionary to populate. Modified in-place.</param>
        /// <param name="fps">Smoothed FPS value computed by the caller.</param>
        internal void CollectAll(Dictionary<string, BudgetMonitor.MetricData> metrics, float fps)
        {
            CollectFPS(metrics, fps);
            CollectMemory(metrics);
            CollectScene(metrics);
            CollectRendering(metrics);
        }

        private void CollectFPS(Dictionary<string, BudgetMonitor.MetricData> metrics, float fps)
        {
            float target = _settings.MinFPS;
            metrics["FPS"] = new BudgetMonitor.MetricData
            {
                value = $"{fps:F1}/{target:F0}",
                currentValue = fps,
                maxValue = target,
                isWarning = fps < target * WarningThreshold,
                isCritical = fps < target,
                type = BudgetMonitor.MetricType.FPS,
                unit = ""
            };
        }

        private void CollectMemory(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
            float totalMB = 0f, texMB = 0f;
            bool hasData = false;

#if UNITY_EDITOR
            try
            {
                totalMB = Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f);
                texMB = UnityStats.usedTextureMemorySize / (1024f * 1024f);
                hasData = totalMB > 0 || texMB > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BudgetMonitor] Memory profiler unavailable: {e.Message}");
            }
#endif

            if (hasData)
            {
                float maxMem = _settings.MaxMemoryMB;
                float maxTex = _settings.MaxTextureMemoryMB;

                metrics["Total Memory"] = new BudgetMonitor.MetricData
                {
                    value = $"{totalMB:F1}/{maxMem:F0}",
                    currentValue = totalMB, maxValue = maxMem,
                    isWarning = totalMB > maxMem * WarningThreshold,
                    isCritical = totalMB > maxMem,
                    type = BudgetMonitor.MetricType.Memory, unit = "MB"
                };

                metrics["Texture Memory"] = new BudgetMonitor.MetricData
                {
                    value = $"{texMB:F1}/{maxTex:F0}",
                    currentValue = texMB, maxValue = maxTex,
                    isWarning = texMB > maxTex * WarningThreshold,
                    isCritical = texMB > maxTex,
                    type = BudgetMonitor.MetricType.Memory, unit = "MB"
                };
            }
            else
            {
                metrics.Remove("Total Memory");
                metrics.Remove("Texture Memory");
            }
        }

        private void CollectScene(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
            int goCount = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None)?.Length ?? 0;
            int maxGO = _settings.MaxGameObjects;

            metrics["GameObjects"] = new BudgetMonitor.MetricData
            {
                value = $"{goCount}/{maxGO}",
                currentValue = goCount, maxValue = maxGO,
                isWarning = goCount > maxGO * WarningThreshold,
                isCritical = goCount > maxGO,
                type = BudgetMonitor.MetricType.Count, unit = ""
            };

            CollectMaterialsAndMeshes(metrics);
        }

        private void CollectMaterialsAndMeshes(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
            var uniqueMaterials = new HashSet<Material>();
            var uniqueMeshes = new HashSet<Mesh>();

            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (renderers != null)
                foreach (var r in renderers)
                    if (r?.sharedMaterials != null)
                        foreach (var m in r.sharedMaterials)
                            if (m != null) uniqueMaterials.Add(m);

            var meshFilters = UnityEngine.Object.FindObjectsByType<MeshFilter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (meshFilters != null)
                foreach (var f in meshFilters)
                    if (f?.sharedMesh != null) uniqueMeshes.Add(f.sharedMesh);

            var skinnedRenderers = UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (skinnedRenderers != null)
                foreach (var r in skinnedRenderers)
                    if (r?.sharedMesh != null) uniqueMeshes.Add(r.sharedMesh);

            int matCount = uniqueMaterials.Count;
            int meshCount = uniqueMeshes.Count;
            int maxMat = _settings.MaxMaterialInstances;
            int maxMesh = _settings.MaxMeshInstances;

            metrics["Material Count"] = MakeCountMetric($"{matCount}/{maxMat}", matCount, maxMat);
            metrics["Mesh Count"] = MakeCountMetric($"{meshCount}/{maxMesh}", meshCount, maxMesh);
        }

        private void CollectRendering(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
#if UNITY_EDITOR
            try
            {
                int maxDC  = _settings.MaxDrawCalls;
                int maxBat = _settings.MaxBatches;
                int maxSPC = _settings.MaxSetPassCalls;
                int maxTri = _settings.MaxTriangles;

                int dc  = UnityStats.drawCalls;
                int bat = UnityStats.batches;
                int spc = UnityStats.setPassCalls;
                int tri = UnityStats.triangles;
                int tex = UnityStats.usedTextureCount;

                metrics["Draw Calls"]  = MakeCountMetric($"{dc}/{maxDC}", dc, maxDC);
                metrics["Batches"]     = MakeCountMetric($"{bat}/{maxBat}", bat, maxBat);
                metrics["SetPass Calls"] = MakeCountMetric($"{spc}/{maxSPC}", spc, maxSPC);
                metrics["Triangles"]   = MakeCountMetric($"{tri:N0}/{maxTri:N0}", tri, maxTri);
                metrics["Texture Count"] = new BudgetMonitor.MetricData
                {
                    value = tex.ToString(), currentValue = tex, maxValue = 0,
                    type = BudgetMonitor.MetricType.Count, unit = ""
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BudgetMonitor] Rendering stats unavailable: {e.Message}");
                foreach (var key in new[] { "Draw Calls", "Batches", "SetPass Calls", "Triangles", "Texture Count" })
                    metrics.Remove(key);
            }
#endif
        }

        private static BudgetMonitor.MetricData MakeCountMetric(string value, float current, float max) =>
            new BudgetMonitor.MetricData
            {
                value = value, currentValue = current, maxValue = max,
                isWarning = current > max * WarningThreshold,
                isCritical = current > max,
                type = BudgetMonitor.MetricType.Count, unit = ""
            };
    }
}
