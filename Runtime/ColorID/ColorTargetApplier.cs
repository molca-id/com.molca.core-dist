using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Outcome of applying a resolved colour to a single component target.
    /// </summary>
    /// <remarks>
    /// Application failures are values, not exceptions or silent no-ops: callers decide whether to log.
    /// Public because it appears in <see cref="ColorApplyResult"/>, which external
    /// <see cref="IColorTargetAdapter"/> implementations return.
    /// </remarks>
    public enum ColorApplyOutcome
    {
        /// <summary>The colour was written to the target's colour channel.</summary>
        Applied,

        /// <summary>The target component (or its shared material) was null or destroyed.</summary>
        TargetMissing,

        /// <summary>No adapter handles this component type and channel.</summary>
        UnsupportedTarget,

        /// <summary>The renderer's shared material has no usable colour shader property.</summary>
        MissingShaderProperty,

        /// <summary>
        /// Application was skipped because it is only valid at runtime — the generic renderer path
        /// writes a <see cref="MaterialPropertyBlock"/>, which edit mode does not persist.
        /// </summary>
        SkippedInEditMode
    }

    /// <summary>
    /// Legacy-facing façade over <see cref="ColorTargetAdapterRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Introduced in the V1 correctness pass as the single subtype-safe application point for
    /// <see cref="ColorID"/> and <see cref="ColorUtility"/>. It now <b>delegates entirely to the
    /// adapter registry</b>, which is the point: legacy <see cref="ColorID"/> components and V2
    /// <see cref="ColorThemeBinding"/> components apply colour through exactly the same adapters, so a
    /// migrated object cannot start rendering differently from an unmigrated one — and a fix or a new
    /// adapter reaches both at once.
    /// <para/>
    /// The behavioural rules (never read <see cref="Renderer.material"/>; specialised renderer subtypes
    /// before the generic material path) now live in the registry and its built-in adapters.
    /// </remarks>
    internal static class ColorTargetApplier
    {
        /// <summary>
        /// Applies <paramref name="color"/> to <paramref name="component"/>, choosing the channel from
        /// the component's most specific type.
        /// </summary>
        /// <param name="component">The component to colour. May be <c>null</c>.</param>
        /// <param name="color">The resolved colour to write.</param>
        /// <param name="allowEditModeMaterialWrite">
        /// When <c>false</c> (the default) the generic renderer path is skipped outside play mode,
        /// because a <see cref="MaterialPropertyBlock"/> written in edit mode is not serialized.
        /// Serialized channels (sprite tint, gradients) always apply.
        /// </param>
        /// <returns>What actually happened, for caller-side diagnostics.</returns>
        internal static ColorApplyOutcome Apply(Component component, Color color,
            bool allowEditModeMaterialWrite = false)
        {
            var context = new ColorBindingContext(
                materialPropertyName: null,
                allowEditModeMaterialWrite: allowEditModeMaterialWrite);

            return ColorTargetAdapterRegistry.Apply(component, ColorChannels.Color, color, context)
                .Outcome;
        }

        /// <summary>
        /// Applies <paramref name="color"/> to a renderer through its specialised colour channel,
        /// falling back to a <see cref="MaterialPropertyBlock"/> colour override.
        /// </summary>
        /// <param name="renderer">The renderer to colour. May be <c>null</c>.</param>
        /// <param name="color">The resolved colour to write.</param>
        /// <param name="allowEditModeMaterialWrite">
        /// See <see cref="Apply"/>. Only gates the material-property path.
        /// </param>
        /// <returns>What actually happened, for caller-side diagnostics.</returns>
        internal static ColorApplyOutcome ApplyToRenderer(Renderer renderer, Color color,
            bool allowEditModeMaterialWrite = false) =>
            Apply(renderer, color, allowEditModeMaterialWrite);

        /// <summary>
        /// Whether <paramref name="renderer"/> is a subtype whose colour is owned by a dedicated
        /// target type, and which must therefore not also be collected as a generic renderer.
        /// </summary>
        /// <remarks>
        /// <see cref="ParticleSystemRenderer"/> is included because particle colour is configured
        /// through the <see cref="ParticleSystem"/> main module, not the renderer.
        /// </remarks>
        internal static bool IsSpecializedRenderer(Renderer renderer) => renderer is SpriteRenderer
            || renderer is LineRenderer
            || renderer is TrailRenderer
            || renderer is ParticleSystemRenderer;
    }
}
