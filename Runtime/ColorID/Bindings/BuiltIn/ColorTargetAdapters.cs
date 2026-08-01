using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Molca.ColorID.BuiltIn
{
    /// <summary>
    /// Colours <see cref="TMP_Text"/> through its own <c>color</c> setter.
    /// </summary>
    /// <remarks>
    /// Must be registered before <see cref="GraphicAdapter"/>. <see cref="TMP_Text"/> derives from
    /// <see cref="MaskableGraphic"/> but shadows <see cref="Graphic.color"/> with a setter that also
    /// flags the text mesh for regeneration; writing through the base property changes the value
    /// without redrawing.
    /// </remarks>
    internal sealed class TmpTextAdapter : IColorTargetAdapter
    {
        public bool CanHandle(Component target, string channel) =>
            target is TMP_Text && ColorChannels.IsDefault(channel);

        public ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context)
        {
            ((TMP_Text)target).color = value;
            return ColorApplyResult.Success;
        }
    }

    /// <summary>
    /// Colours any uGUI <see cref="Graphic"/> — <see cref="Image"/>, <see cref="RawImage"/>, legacy
    /// <see cref="Text"/> and anything else deriving from it.
    /// </summary>
    /// <remarks>
    /// One adapter rather than one per concrete type: they all colour through
    /// <see cref="Graphic.color"/>, which marks the canvas vertices dirty. Separate adapters would be
    /// four copies of one line with four chances to diverge.
    /// </remarks>
    internal sealed class GraphicAdapter : IColorTargetAdapter
    {
        public bool CanHandle(Component target, string channel) =>
            target is Graphic && ColorChannels.IsDefault(channel);

        public ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context)
        {
            ((Graphic)target).color = value;
            return ColorApplyResult.Success;
        }
    }

    /// <summary>Colours a <see cref="SpriteRenderer"/>'s tint.</summary>
    /// <remarks>
    /// A sprite's tint is a component property, not a material colour property. V1 matched
    /// <c>is Renderer</c> first and wrote <c>renderer.material.color</c>, which was both the wrong
    /// channel and a material instantiation.
    /// </remarks>
    internal sealed class SpriteRendererAdapter : IColorTargetAdapter
    {
        public bool CanHandle(Component target, string channel) =>
            target is SpriteRenderer && ColorChannels.IsDefault(channel);

        public ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context)
        {
            ((SpriteRenderer)target).color = value;
            return ColorApplyResult.Success;
        }
    }

    /// <summary>Colours a <see cref="LineRenderer"/>'s gradient endpoints.</summary>
    /// <remarks>
    /// The default channel writes both ends, which is what a single-colour binding means. Binding the
    /// two ends to different tokens is done with two bindings using
    /// <see cref="ColorChannels.StartColor"/> and <see cref="ColorChannels.EndColor"/>.
    /// </remarks>
    internal sealed class LineRendererAdapter : IColorTargetAdapter
    {
        public bool CanHandle(Component target, string channel) =>
            target is LineRenderer
            && (ColorChannels.IsDefault(channel)
                || channel == ColorChannels.StartColor
                || channel == ColorChannels.EndColor);

        public ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context)
        {
            var line = (LineRenderer)target;
            if (channel == ColorChannels.StartColor) line.startColor = value;
            else if (channel == ColorChannels.EndColor) line.endColor = value;
            else
            {
                line.startColor = value;
                line.endColor = value;
            }
            return ColorApplyResult.Success;
        }
    }

    /// <summary>Colours a <see cref="TrailRenderer"/>'s gradient endpoints.</summary>
    internal sealed class TrailRendererAdapter : IColorTargetAdapter
    {
        public bool CanHandle(Component target, string channel) =>
            target is TrailRenderer
            && (ColorChannels.IsDefault(channel)
                || channel == ColorChannels.StartColor
                || channel == ColorChannels.EndColor);

        public ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context)
        {
            var trail = (TrailRenderer)target;
            if (channel == ColorChannels.StartColor) trail.startColor = value;
            else if (channel == ColorChannels.EndColor) trail.endColor = value;
            else
            {
                trail.startColor = value;
                trail.endColor = value;
            }
            return ColorApplyResult.Success;
        }
    }

    /// <summary>Colours a <see cref="ParticleSystem"/>'s main-module start colour.</summary>
    internal sealed class ParticleSystemAdapter : IColorTargetAdapter
    {
        public bool CanHandle(Component target, string channel) =>
            target is ParticleSystem && ColorChannels.IsDefault(channel);

        public ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context)
        {
            // MainModule is a struct view over the system; assigning to the local applies it.
            var main = ((ParticleSystem)target).main;
            main.startColor = value;
            return ColorApplyResult.Success;
        }
    }

    /// <summary>
    /// Colours a generic <see cref="Renderer"/> through a <see cref="MaterialPropertyBlock"/>.
    /// </summary>
    /// <remarks>
    /// Registered last among the built-ins, and it claims a renderer only when no specialised adapter
    /// did — so <see cref="SpriteRenderer"/>, line, trail and particle renderers never reach it.
    /// <para/>
    /// Two invariants:
    /// <list type="bullet">
    /// <item><description>
    /// <b><see cref="Renderer.material"/> is never read.</b> Reading it instantiates a per-renderer
    /// copy of the shared material, leaking one material per themed object and breaking batching. The
    /// shared material is read only to probe for a property.
    /// </description></item>
    /// <item><description>
    /// <b>The existing property block is preserved.</b> <c>SetPropertyBlock</c> replaces the block
    /// wholesale, so it is read first and only the colour property is changed — a renderer may already
    /// carry unrelated per-instance overrides from another system.
    /// </description></item>
    /// </list>
    /// </remarks>
    internal sealed class MaterialPropertyAdapter : IColorTargetAdapter
    {
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");

        // Reused so theme switching allocates nothing. Safe: application is synchronous and
        // main-thread only, and the block's contents are overwritten on every use.
        private static readonly MaterialPropertyBlock SharedBlock = new MaterialPropertyBlock();

        public bool CanHandle(Component target, string channel) =>
            target is Renderer
            && (ColorChannels.IsDefault(channel) || channel == ColorChannels.MaterialProperty);

        public ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context)
        {
            var renderer = (Renderer)target;

            if (!context.AllowEditModeMaterialWrite && !Application.isPlaying)
            {
                return ColorApplyResult.Fail(ColorApplyOutcome.SkippedInEditMode,
                    "A MaterialPropertyBlock written outside play mode is not serialized.");
            }

            var material = renderer.sharedMaterial;
            if (material == null)
            {
                return ColorApplyResult.Fail(ColorApplyOutcome.TargetMissing,
                    $"Renderer '{renderer.name}' has no shared material.");
            }

            if (!TryResolvePropertyId(material, context.MaterialPropertyName, out int propertyId,
                    out string error))
            {
                return ColorApplyResult.Fail(ColorApplyOutcome.MissingShaderProperty, error);
            }

            renderer.GetPropertyBlock(SharedBlock);
            SharedBlock.SetColor(propertyId, value);
            renderer.SetPropertyBlock(SharedBlock);
            return ColorApplyResult.Success;
        }

        /// <summary>
        /// Picks the shader property to write: the explicitly named one, or <c>_BaseColor</c> then
        /// <c>_Color</c> by probe.
        /// </summary>
        /// <remarks>
        /// An explicitly named property that does not exist is an error rather than a silent fall back
        /// to the probe order — the author stated an intent, and quietly writing a different property
        /// would produce a colour on the wrong channel.
        /// </remarks>
        private static bool TryResolvePropertyId(Material material, string requestedName,
            out int propertyId, out string error)
        {
            if (!string.IsNullOrEmpty(requestedName))
            {
                propertyId = Shader.PropertyToID(requestedName);
                if (material.HasProperty(propertyId))
                {
                    error = null;
                    return true;
                }

                error = $"Shader '{material.shader?.name}' has no colour property '{requestedName}'.";
                return false;
            }

            // URP/HDRP expose _BaseColor; the built-in pipeline and most custom shaders expose _Color.
            if (material.HasProperty(BaseColorPropertyId))
            {
                propertyId = BaseColorPropertyId;
                error = null;
                return true;
            }

            if (material.HasProperty(ColorPropertyId))
            {
                propertyId = ColorPropertyId;
                error = null;
                return true;
            }

            propertyId = 0;
            error = $"Shader '{material.shader?.name}' has neither '_BaseColor' nor '_Color'. "
                    + "Name the colour property explicitly on the binding.";
            return false;
        }
    }
}
