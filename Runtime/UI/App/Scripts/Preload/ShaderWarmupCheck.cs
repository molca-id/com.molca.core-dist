using System.Linq;
using UnityEngine;

namespace Molca.App.Preload
{
    /// <summary>
    /// Preload check that warms up shader variants one-per-frame (or N-per-frame) from a
    /// <see cref="ShaderVariantManifest"/>, providing real per-variant progress feedback.
    /// </summary>
    /// <remarks>
    /// Add this component to the preload scene and register it in
    /// <c>PreloadCheck.customChecks</c>. Subclass and override <see cref="OnWarmupStarted"/>,
    /// <see cref="OnProgress"/>, and <see cref="OnWarmupComplete"/> to integrate with a loading UI.
    /// <para>
    /// Warmup is skipped between launches when the manifest content hash in PlayerPrefs matches
    /// the current manifest. It re-runs when the manifest changes, the app version changes, or
    /// PlayerPrefs are cleared (reinstall). Set <c>_forceWarmup</c> to always run (testing).
    /// </para>
    /// </remarks>
    public class ShaderWarmupCheck : MonoBehaviour, IPreloadCheck
    {
        internal const string CacheKey = "Molca.ShaderWarmupHash";

        [SerializeField] private ShaderVariantManifest _manifest;
        [SerializeField] private bool _forceWarmup = false;

        [Tooltip("How many variants to submit per frame. Higher values reduce total frames but " +
                 "give coarser progress granularity.")]
        [SerializeField, Min(1)] private int _variantsPerFrame = 1;

        [SerializeField] private string _warmingMessage = "Warming up shaders...";
        [SerializeField] private string _completeMessage = "Shaders ready.";

        /// <inheritdoc cref="IPreloadCheck.RunCheck"/>
        public async Awaitable RunCheck()
        {
            if (_manifest == null || _manifest.collections.Count == 0)
            {
                Debug.LogWarning("[ShaderWarmupCheck] Manifest is null or empty — skipping warmup.");
                return;
            }

            var currentHash = ComputeHash(_manifest);
            if (!_forceWarmup && PlayerPrefs.GetString(CacheKey, string.Empty) == currentHash)
            {
                Debug.Log("[ShaderWarmupCheck] Cache hit — skipping warmup.");
                return;
            }

            int total = _manifest.collections.Count;
            int perFrame = Mathf.Max(1, _variantsPerFrame);
            OnWarmupStarted(_warmingMessage);

            for (int i = 0; i < total;)
            {
                int batchEnd = Mathf.Min(i + perFrame, total);
                for (int j = i; j < batchEnd; j++)
                {
                    if (_manifest.collections[j] == null)
                    {
                        Debug.LogWarning($"[ShaderWarmupCheck] Null collection at index {j} in " +
                                         $"manifest '{_manifest.name}' — skipping.");
                        continue;
                    }
                    _manifest.collections[j].WarmUp();
                }
                i = batchEnd;
                OnProgress(i / (float)total, $"{_warmingMessage} ({i}/{total})");
                await Awaitable.NextFrameAsync();
            }

            // Saved after loop: a mid-run crash triggers a full re-run on next launch.
            PlayerPrefs.SetString(CacheKey, currentHash);
            PlayerPrefs.Save();

            await OnWarmupComplete(_completeMessage);
            Debug.Log($"[ShaderWarmupCheck] Warmup complete — {total} variants submitted.");
        }

        /// <summary>
        /// Computes a stable hash from the names of all collections in <paramref name="manifest"/>.
        /// Changing, reordering, or replacing any collection invalidates the cache.
        /// </summary>
        internal static string ComputeHash(ShaderVariantManifest manifest)
        {
            var key = string.Join(",", manifest.collections.Select(c => c != null ? c.name : "null"))
                      + "_" + Application.version;
            return key.GetHashCode().ToString();
        }

        /// <summary>Called once before the warmup loop starts. Override to show your loading UI.</summary>
        protected virtual void OnWarmupStarted(string message) { }

        /// <summary>
        /// Called after each frame batch. <paramref name="progress"/> is 0..1.
        /// Override to update your loading UI.
        /// </summary>
        protected virtual void OnProgress(float progress, string status) { }

        /// <summary>
        /// Called after the loop completes and PlayerPrefs are saved. Override to close your loading UI.
        /// Awaited by <see cref="RunCheck"/>; safe to perform async work (e.g. fade-out) here.
        /// </summary>
#pragma warning disable CS1998 // base impl is intentionally a no-op; subclasses add async work
        protected virtual async Awaitable OnWarmupComplete(string message) { }
#pragma warning restore CS1998
    }
}
