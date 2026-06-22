using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Molca.Utilities
{
    /// <summary>
    /// Collects performance, memory, scene, and rendering metrics (Sprint 54: build-parity via
    /// <see cref="ProfilerRecorder"/>). Pure C# — no MonoBehaviour dependency; unit-testable in isolation.
    /// </summary>
    /// <remarks>
    /// Rendering and texture-memory metrics come from <see cref="ProfilerRecorder"/> counters that report in
    /// <b>development players</b> (not just the editor), with the editor-only <c>UnityStats</c> kept as a
    /// fallback. Recorders are created on construction and released by <see cref="Dispose"/>, which the owning
    /// <see cref="BudgetMonitor"/> calls on teardown. Buffers (the unique-material/mesh sets and the format
    /// <see cref="StringBuilder"/>) are reused across calls so the overlay does not generate the GC garbage it
    /// is meant to surface. Main-thread only.
    /// </remarks>
    internal sealed class BudgetMetricCollector : IDisposable
    {
        private const float WarningThreshold = 0.8f;

        private readonly BudgetSettings _settings;

        // Reused across collections so a per-interval scene scan allocates only the FindObjects arrays, never
        // fresh sets (Sprint 54).
        private readonly HashSet<Material> _materials = new HashSet<Material>();
        private readonly HashSet<Mesh> _meshes = new HashSet<Mesh>();
        private readonly StringBuilder _sb = new StringBuilder(32);

        // ProfilerRecorder-backed rendering/texture counters — valid in dev players; invalid ones are omitted.
        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _setPassCalls;
        private ProfilerRecorder _batches;
        private ProfilerRecorder _triangles;
        private ProfilerRecorder _usedTextureBytes;
        private bool _recordersStarted;

        internal BudgetMetricCollector(BudgetSettings settings)
        {
            _settings = settings;
            StartRecorders();
        }

        private void StartRecorders()
        {
            try
            {
                _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                _setPassCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
                _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
                _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
                _usedTextureBytes = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Used Textures Bytes");
                _recordersStarted = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BudgetMonitor] Profiler recorders unavailable: {e.Message}");
            }
        }

        /// <summary>Releases the profiler recorders. Idempotent.</summary>
        public void Dispose()
        {
            if (!_recordersStarted) return;
            _recordersStarted = false;
            if (_drawCalls.Valid) _drawCalls.Dispose();
            if (_setPassCalls.Valid) _setPassCalls.Dispose();
            if (_batches.Valid) _batches.Dispose();
            if (_triangles.Valid) _triangles.Dispose();
            if (_usedTextureBytes.Valid) _usedTextureBytes.Dispose();
        }

        /// <summary>
        /// Populates <paramref name="metrics"/> with every metric — the frequent ones (FPS, memory,
        /// rendering) plus the expensive scene-composition scan. Kept for callers/tests that want a one-shot
        /// full collection; <see cref="BudgetMonitor"/> drives the two cadences separately.
        /// </summary>
        internal void CollectAll(Dictionary<string, BudgetMonitor.MetricData> metrics, float fps)
        {
            CollectFrequent(metrics, fps);
            CollectSceneComposition(metrics);
        }

        /// <summary>Collects the cheap, per-interval metrics: FPS, memory, and recorder-backed rendering stats.</summary>
        internal void CollectFrequent(Dictionary<string, BudgetMonitor.MetricData> metrics, float fps)
        {
            CollectFPS(metrics, fps);
            CollectMemory(metrics);
            CollectRendering(metrics);
        }

        /// <summary>Collects the expensive scene-composition metrics (full-scene scans): GameObject/material/mesh counts.</summary>
        internal void CollectSceneComposition(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
            CollectScene(metrics);
        }

        private void CollectFPS(Dictionary<string, BudgetMonitor.MetricData> metrics, float fps)
        {
            float target = _settings.MinFPS;
            metrics["FPS"] = new BudgetMonitor.MetricData
            {
                value = Pair(fps, target, "F1", "F0"),
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
            bool hasTotal = false, hasTex = false;

            try
            {
                // GetTotalAllocatedMemoryLong reports in development players and the editor.
                long totalBytes = Profiler.GetTotalAllocatedMemoryLong();
                if (totalBytes > 0) { totalMB = totalBytes / (1024f * 1024f); hasTotal = true; }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BudgetMonitor] Memory profiler unavailable: {e.Message}");
            }

            if (_usedTextureBytes.Valid)
            {
                long texBytes = _usedTextureBytes.LastValue;
                if (texBytes > 0) { texMB = texBytes / (1024f * 1024f); hasTex = true; }
            }
#if UNITY_EDITOR
            if (!hasTex)
            {
                try { texMB = UnityStats.usedTextureMemorySize / (1024f * 1024f); hasTex = texMB > 0; }
                catch { /* editor fallback only */ }
            }
#endif

            if (hasTotal)
            {
                float maxMem = _settings.MaxMemoryMB;
                metrics["Total Memory"] = new BudgetMonitor.MetricData
                {
                    value = Pair(totalMB, maxMem, "F1", "F0"),
                    currentValue = totalMB, maxValue = maxMem,
                    isWarning = totalMB > maxMem * WarningThreshold,
                    isCritical = totalMB > maxMem,
                    type = BudgetMonitor.MetricType.Memory, unit = "MB"
                };
            }
            else metrics.Remove("Total Memory");

            if (hasTex)
            {
                float maxTex = _settings.MaxTextureMemoryMB;
                metrics["Texture Memory"] = new BudgetMonitor.MetricData
                {
                    value = Pair(texMB, maxTex, "F1", "F0"),
                    currentValue = texMB, maxValue = maxTex,
                    isWarning = texMB > maxTex * WarningThreshold,
                    isCritical = texMB > maxTex,
                    type = BudgetMonitor.MetricType.Memory, unit = "MB"
                };
            }
            else metrics.Remove("Texture Memory");
        }

        private void CollectScene(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
            int goCount = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None)?.Length ?? 0;
            int maxGO = _settings.MaxGameObjects;

            metrics["GameObjects"] = MakeCountMetric(Pair(goCount, maxGO), goCount, maxGO);

            CollectMaterialsAndMeshes(metrics);
        }

        private void CollectMaterialsAndMeshes(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
            _materials.Clear();
            _meshes.Clear();

            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (renderers != null)
                foreach (var r in renderers)
                    if (r?.sharedMaterials != null)
                        foreach (var m in r.sharedMaterials)
                            if (m != null) _materials.Add(m);

            var meshFilters = UnityEngine.Object.FindObjectsByType<MeshFilter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (meshFilters != null)
                foreach (var f in meshFilters)
                    if (f?.sharedMesh != null) _meshes.Add(f.sharedMesh);

            var skinnedRenderers = UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (skinnedRenderers != null)
                foreach (var r in skinnedRenderers)
                    if (r?.sharedMesh != null) _meshes.Add(r.sharedMesh);

            int matCount = _materials.Count;
            int meshCount = _meshes.Count;
            int maxMat = _settings.MaxMaterialInstances;
            int maxMesh = _settings.MaxMeshInstances;

            metrics["Material Count"] = MakeCountMetric(Pair(matCount, maxMat), matCount, maxMat);
            metrics["Mesh Count"] = MakeCountMetric(Pair(meshCount, maxMesh), meshCount, maxMesh);
        }

        private void CollectRendering(Dictionary<string, BudgetMonitor.MetricData> metrics)
        {
            TrySetCount(metrics, "Draw Calls", _drawCalls, _settings.MaxDrawCalls);
            TrySetCount(metrics, "Batches", _batches, _settings.MaxBatches);
            TrySetCount(metrics, "SetPass Calls", _setPassCalls, _settings.MaxSetPassCalls);
            TrySetCount(metrics, "Triangles", _triangles, _settings.MaxTriangles);
        }

        /// <summary>
        /// Writes a recorder-backed count metric, falling back to <c>UnityStats</c> in the editor when the
        /// recorder is unavailable, and removing the entry when neither source can supply it.
        /// </summary>
        private void TrySetCount(Dictionary<string, BudgetMonitor.MetricData> metrics, string key, ProfilerRecorder recorder, int max)
        {
            long value = -1;
            if (recorder.Valid) value = recorder.LastValue;
#if UNITY_EDITOR
            if (value < 0) value = EditorStatFor(key);
#endif
            if (value < 0) { metrics.Remove(key); return; }

            metrics[key] = MakeCountMetric(Pair(value, max), value, max);
        }

#if UNITY_EDITOR
        private static long EditorStatFor(string key) => key switch
        {
            "Draw Calls" => UnityStats.drawCalls,
            "Batches" => UnityStats.batches,
            "SetPass Calls" => UnityStats.setPassCalls,
            "Triangles" => UnityStats.triangles,
            _ => -1
        };
#endif

        private static BudgetMonitor.MetricData MakeCountMetric(string value, float current, float max) =>
            new BudgetMonitor.MetricData
            {
                value = value, currentValue = current, maxValue = max,
                isWarning = current > max * WarningThreshold,
                isCritical = current > max,
                type = BudgetMonitor.MetricType.Count, unit = ""
            };

        /// <summary>Formats "current/max" reusing the cached <see cref="StringBuilder"/> to avoid per-call allocs.</summary>
        private string Pair(float current, float max, string currentFmt, string maxFmt)
        {
            _sb.Clear();
            _sb.Append(current.ToString(currentFmt)).Append('/').Append(max.ToString(maxFmt));
            return _sb.ToString();
        }

        private string Pair(long current, long max)
        {
            _sb.Clear();
            _sb.Append(current).Append('/').Append(max);
            return _sb.ToString();
        }
    }
}
