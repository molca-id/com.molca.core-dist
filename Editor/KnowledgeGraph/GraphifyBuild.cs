using System;
using UnityEngine;

namespace Molca.Editor.KnowledgeGraph
{
    /// <summary>
    /// Drives a knowledge-graph build from the editor UI without blocking: refreshes the Unity facts
    /// corpus (<see cref="UnityFactsExporter"/>) and runs graphify over <c>Assets/</c> + the corpus via
    /// <see cref="GraphifyCli"/>. Exposes simple <see cref="IsBuilding"/>/<see cref="Status"/> state and a
    /// <see cref="Changed"/> event so an inspector can repaint as the build progresses.
    /// </summary>
    public static class GraphifyBuild
    {
        /// <summary>True while a build is in progress.</summary>
        public static bool IsBuilding { get; private set; }

        /// <summary>Last status line (e.g. "Building graph…", "Build complete.").</summary>
        public static string Status { get; private set; } = string.Empty;

        /// <summary>Raised on the main thread whenever status changes, so UIs can repaint.</summary>
        public static event Action Changed;

        /// <summary>
        /// Starts a build. <paramref name="full"/> forces a from-scratch rebuild instead of an incremental
        /// <c>--update</c>. No-op if a build is already running. async void is intentional: this is a
        /// UI event-handler entry point, and the body is fully guarded.
        /// </summary>
        public static async void Run(bool full)
        {
            if (IsBuilding) return;
            IsBuilding = true;
            Set("Exporting Unity facts…");

            try
            {
                try { UnityFactsExporter.ExportAll(); }
                catch (Exception ex) { Debug.LogWarning($"[Molca KG] Facts export failed (continuing): {ex.Message}"); }

                Set("Building graph with graphify… (this can take a while)");

                var cmd = GraphifyCli.BuildIndexArgs(full);

                var result = await GraphifyCli.RunAsync(cmd, default, timeoutMs: 600_000);
                Set(result.NotFound
                        ? "graphify CLI not found on PATH — install it (graphify.net)."
                        : result.Ok
                            ? "Build complete."
                            : $"Build failed (exit {result.ExitCode}). See the console.");

                if (!result.NotFound && !result.Ok && !string.IsNullOrEmpty(result.StdErr))
                    Debug.LogError($"[Molca KG] graphify build failed: {result.StdErr}");
            }
            catch (Exception ex)
            {
                Set("Build error: " + ex.Message);
                Debug.LogException(ex);
            }
            finally
            {
                IsBuilding = false;
                Changed?.Invoke();
            }
        }

        private static void Set(string status)
        {
            Status = status;
            Changed?.Invoke();
        }
    }
}
