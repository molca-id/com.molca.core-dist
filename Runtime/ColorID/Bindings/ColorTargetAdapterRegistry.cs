using System;
using System.Collections.Generic;
using Molca.ColorID.BuiltIn;
using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// The single place that decides which adapter colours a given component and channel.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/ColorID/Bindings/</c>.
    /// <b>Shape:</b> static registry. <b>Registration:</b> built-ins register themselves
    /// deterministically on first use; external adapters are added explicitly via
    /// <see cref="Register"/>, normally during subsystem initialization.
    /// <para/>
    /// <b>Resolution order is registration order, and built-ins always come first.</b> That has two
    /// consequences worth stating plainly:
    /// <list type="bullet">
    /// <item><description>
    /// Specialised built-ins are ordered before general ones — <see cref="TmpTextAdapter"/> before
    /// <see cref="GraphicAdapter"/>, and every specialised renderer before
    /// <see cref="MaterialPropertyAdapter"/> — which is what stops a sprite or a line renderer from
    /// being colouring through the material path. This ordering *is* the fix for the V1 defect where
    /// matching <c>is Renderer</c> first made the specialised branches unreachable.
    /// </description></item>
    /// <item><description>
    /// An external adapter therefore <i>extends</i> to new component types and cannot silently hijack a
    /// built-in one. To take over a type Core already handles, claim a distinct channel name — the
    /// override is then visible in the binding's authored data rather than hidden in load order.
    /// </description></item>
    /// </list>
    /// Main thread only.
    /// </remarks>
    public static class ColorTargetAdapterRegistry
    {
        // Built-ins in claim order: most specific first. This list is the contract described above.
        private static readonly IColorTargetAdapter[] BuiltInAdapters =
        {
            new TmpTextAdapter(),
            new GraphicAdapter(),
            new SpriteRendererAdapter(),
            new LineRendererAdapter(),
            new TrailRendererAdapter(),
            new ParticleSystemAdapter(),
            // Last: claims a Renderer only if nothing above did.
            new MaterialPropertyAdapter()
        };

        private static readonly List<IColorTargetAdapter> ExternalAdapters =
            new List<IColorTargetAdapter>();

        /// <summary>
        /// Clears external registrations before each play session.
        /// </summary>
        /// <remarks>
        /// With domain reload disabled, statics survive between play sessions, so adapters registered
        /// by the previous session's subsystems would still be present — and could be instances whose
        /// owning objects are destroyed. Built-ins are stateless and are never cleared.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetExternalAdapters() => ExternalAdapters.Clear();

        /// <summary>Number of registered external adapters.</summary>
        public static int ExternalAdapterCount => ExternalAdapters.Count;

        /// <summary>Registers an adapter for component types Core does not handle.</summary>
        /// <param name="adapter">The adapter to add. Ignored if <c>null</c> or already registered.</param>
        /// <remarks>
        /// Call during subsystem initialization. Registering the same instance twice is a no-op rather
        /// than an error, so a subsystem that re-initializes cannot accumulate duplicates.
        /// </remarks>
        public static void Register(IColorTargetAdapter adapter)
        {
            if (adapter == null)
            {
                Debug.LogError("[ColorTheme] Cannot register a null colour target adapter.");
                return;
            }

            if (ExternalAdapters.Contains(adapter)) return;
            ExternalAdapters.Add(adapter);
        }

        /// <summary>Removes a previously registered external adapter.</summary>
        /// <param name="adapter">The adapter to remove.</param>
        /// <returns><c>true</c> when it was registered and has been removed.</returns>
        public static bool Unregister(IColorTargetAdapter adapter) =>
            adapter != null && ExternalAdapters.Remove(adapter);

        /// <summary>Finds the adapter that would handle a component and channel.</summary>
        /// <param name="target">The component to colour.</param>
        /// <param name="channel">The channel; <c>null</c> or empty for the default.</param>
        /// <param name="adapter">The winning adapter, or <c>null</c>.</param>
        /// <returns><c>false</c> when nothing claims this combination.</returns>
        public static bool TryGetAdapter(Component target, string channel,
            out IColorTargetAdapter adapter)
        {
            adapter = null;
            if (target == null) return false;

            foreach (var candidate in BuiltInAdapters)
            {
                if (candidate.CanHandle(target, channel))
                {
                    adapter = candidate;
                    return true;
                }
            }

            // Indexed rather than foreach: an adapter registering another adapter from inside
            // CanHandle would invalidate an enumerator, and a registry is exactly the kind of static
            // that gets called from unexpected places.
            for (int i = 0; i < ExternalAdapters.Count; i++)
            {
                if (ExternalAdapters[i].CanHandle(target, channel))
                {
                    adapter = ExternalAdapters[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>Applies a colour to a component through the adapter that claims it.</summary>
        /// <param name="target">The component to colour. May be <c>null</c>.</param>
        /// <param name="channel">The channel; <c>null</c> or empty for the default.</param>
        /// <param name="value">The final colour, with alpha policy already applied.</param>
        /// <param name="context">Additional inputs such as the material property name.</param>
        /// <returns>What happened. An unclaimed combination is a typed result, never a silent no-op.</returns>
        public static ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context = default)
        {
            if (target == null)
            {
                return ColorApplyResult.Fail(ColorApplyOutcome.TargetMissing,
                    "The binding's target component is missing or has been destroyed.");
            }

            if (!TryGetAdapter(target, channel, out var adapter))
            {
                return ColorApplyResult.Fail(ColorApplyOutcome.UnsupportedTarget,
                    $"No adapter handles channel '{channel ?? ColorChannels.Color}' on "
                    + $"'{target.GetType().Name}'. Register an IColorTargetAdapter for it, or bind a "
                    + "supported component.");
            }

            try
            {
                return adapter.Apply(target, channel, value, context);
            }
            catch (Exception exception)
            {
                // A third-party adapter must not be able to abort the theme switch for every other
                // binding in the scene.
                Debug.LogException(exception, target);
                return ColorApplyResult.Fail(ColorApplyOutcome.UnsupportedTarget,
                    $"Adapter '{adapter.GetType().Name}' threw: {exception.Message}");
            }
        }

        /// <summary>
        /// Applies <paramref name="policy"/> to produce the final colour written to a target.
        /// </summary>
        /// <param name="resolved">The token's resolved colour.</param>
        /// <param name="policy">The binding's alpha policy.</param>
        /// <param name="target">The component, read for its current alpha under
        /// <see cref="ColorAlphaPolicy.PreserveTarget"/>.</param>
        /// <param name="channel">The channel being written.</param>
        /// <param name="explicitAlpha">Alpha for <see cref="ColorAlphaPolicy.Explicit"/>.</param>
        /// <returns>The colour to hand to the adapter.</returns>
        /// <remarks>
        /// Alpha is resolved here, before the adapter runs, so every adapter receives a final colour
        /// and none has to reimplement the policy. <see cref="ColorAlphaPolicy.PreserveTarget"/> needs
        /// to read the target's current alpha, which is why <see cref="TryReadCurrentColor"/> exists.
        /// </remarks>
        public static Color ApplyAlphaPolicy(Color resolved, ColorAlphaPolicy policy, Component target,
            string channel, float explicitAlpha)
        {
            switch (policy)
            {
                case ColorAlphaPolicy.Explicit:
                    resolved.a = Mathf.Clamp01(explicitAlpha);
                    return resolved;

                case ColorAlphaPolicy.PreserveTarget:
                    if (TryReadCurrentColor(target, channel, out Color current)) resolved.a = current.a;
                    return resolved;

                default:
                    return resolved;
            }
        }

        /// <summary>Reads a target's current colour on a channel, where that is possible.</summary>
        /// <param name="target">The component to read.</param>
        /// <param name="channel">The channel to read.</param>
        /// <param name="color">The current colour, when readable.</param>
        /// <returns><c>false</c> for targets whose current colour cannot be read cheaply.</returns>
        /// <remarks>
        /// The generic material path is intentionally not readable here: getting a property block value
        /// back requires the property to have been set already, so a first application would read a
        /// default rather than the material's authored alpha and "preserve" the wrong number. Those
        /// targets keep the token's alpha instead, which is at least predictable.
        /// </remarks>
        public static bool TryReadCurrentColor(Component target, string channel, out Color color)
        {
            switch (target)
            {
                case TMPro.TMP_Text tmpText:
                    color = tmpText.color;
                    return true;
                case UnityEngine.UI.Graphic graphic:
                    color = graphic.color;
                    return true;
                case SpriteRenderer sprite:
                    color = sprite.color;
                    return true;
                case LineRenderer line:
                    color = channel == ColorChannels.EndColor ? line.endColor : line.startColor;
                    return true;
                case TrailRenderer trail:
                    color = channel == ColorChannels.EndColor ? trail.endColor : trail.startColor;
                    return true;
                case ParticleSystem particles:
                    color = particles.main.startColor.color;
                    return true;
                default:
                    color = Color.white;
                    return false;
            }
        }

        /// <summary>Test seam: clears external registrations between tests.</summary>
        internal static void ResetForTests() => ExternalAdapters.Clear();
    }
}
