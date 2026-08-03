using System.Collections.Generic;
using UnityEngine;

namespace Molca.App.Preload
{
    /// <summary>
    /// Ordered list of single-variant <see cref="ShaderVariantCollection"/> assets produced by
    /// <c>ShaderVariantSplitTool</c>. Pure data — no logic.
    /// </summary>
    /// <remarks>
    /// Add this asset to <see cref="ShaderWarmupCheck.manifest"/> in the Inspector.
    /// Commit the asset and the referenced collections to version control; missing entries
    /// produce null slots that are skipped (with a warning) at warmup time.
    /// </remarks>
    [CreateAssetMenu(menuName = "Molca/SDK/Shader Variant Manifest", order = 120)]
    public class ShaderVariantManifest : ScriptableObject
    {
        /// <summary>
        /// Ordered list of single-variant collections produced by ShaderVariantSplitTool.
        /// Each entry holds exactly one shader variant.
        /// </summary>
        public List<ShaderVariantCollection> collections = new();
    }
}
