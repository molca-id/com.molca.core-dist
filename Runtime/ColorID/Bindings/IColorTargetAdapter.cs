using UnityEngine;

namespace Molca.ColorID
{
    /// <summary>
    /// Canonical channel names understood by the built-in colour target adapters.
    /// </summary>
    /// <remarks>
    /// A channel identifies <i>which</i> colour on a component is being written, for components that
    /// have more than one. Strings rather than an enum so an SDK or fork can introduce a channel for
    /// its own component type without editing Core — the same reason the adapter seam exists.
    /// <para/>
    /// An empty or <c>null</c> channel means "this component's primary colour", which is what almost
    /// every binding wants and what keeps simple authoring simple.
    /// </remarks>
    public static class ColorChannels
    {
        /// <summary>The component's primary colour. Equivalent to leaving the channel blank.</summary>
        public const string Color = "color";

        /// <summary>The start colour of a gradient-based renderer (line, trail).</summary>
        public const string StartColor = "startColor";

        /// <summary>The end colour of a gradient-based renderer (line, trail).</summary>
        public const string EndColor = "endColor";

        /// <summary>
        /// A renderer's material colour property, named by
        /// <see cref="ColorBindingContext.MaterialPropertyName"/>.
        /// </summary>
        public const string MaterialProperty = "materialProperty";

        /// <summary>Whether <paramref name="channel"/> means "the primary colour".</summary>
        /// <param name="channel">The channel name to test.</param>
        public static bool IsDefault(string channel) =>
            string.IsNullOrEmpty(channel) || channel == Color;
    }

    /// <summary>Outcome of applying a resolved colour to one component target.</summary>
    /// <remarks>
    /// Application failures are values rather than exceptions or silent no-ops. Public because external
    /// <see cref="IColorTargetAdapter"/> implementations return it through <see cref="ColorApplyResult"/>.
    /// </remarks>
    public enum ColorApplyOutcome
    {
        /// <summary>The colour was written to the target's channel.</summary>
        Applied = 0,

        /// <summary>The target component or required material was missing.</summary>
        TargetMissing = 1,

        /// <summary>No adapter handles this component type and channel.</summary>
        UnsupportedTarget = 2,

        /// <summary>The renderer's material has no usable colour shader property.</summary>
        MissingShaderProperty = 3,

        /// <summary>The operation was skipped because its write is only valid at runtime.</summary>
        SkippedInEditMode = 4
    }

    /// <summary>How a binding decides the alpha of the colour it writes.</summary>
    public enum ColorAlphaPolicy
    {
        /// <summary>Use the token's own alpha. The default, and what a themed alpha means.</summary>
        UseTokenAlpha = 0,

        /// <summary>
        /// Keep whatever alpha the target already has, taking only RGB from the token.
        /// </summary>
        /// <remarks>
        /// For targets whose transparency is animated or driven by another system — a fade, a
        /// <c>CanvasGroup</c>-like manual alpha — where a theme switch must not stamp over it.
        /// </remarks>
        PreserveTarget = 1,

        /// <summary>Use <see cref="ColorBindingContext.ExplicitAlpha"/>, clamped to [0, 1].</summary>
        Explicit = 2
    }

    /// <summary>
    /// Per-application inputs an adapter may need beyond the component, channel and colour.
    /// </summary>
    /// <remarks>
    /// A struct passed by value: adapters are called once per binding per theme change, and an
    /// allocation here would scale with binding count.
    /// </remarks>
    public readonly struct ColorBindingContext
    {
        /// <summary>
        /// Shader property to write for <see cref="ColorChannels.MaterialProperty"/>, for example
        /// <c>_BaseColor</c>. Ignored by other channels.
        /// </summary>
        /// <remarks>
        /// Required and explicit rather than guessed. The material path never picks a property by
        /// convention alone at the binding's request — if this is blank the adapter falls back to
        /// probing <c>_BaseColor</c> then <c>_Color</c>, and reports
        /// <see cref="ColorApplyOutcome.MissingShaderProperty"/> when neither exists.
        /// </remarks>
        public string MaterialPropertyName { get; }

        /// <summary>
        /// Whether the generic material path may write outside play mode.
        /// </summary>
        /// <remarks>
        /// Normally <c>false</c>: a <see cref="MaterialPropertyBlock"/> written in edit mode is not
        /// serialized, so applying one would dirty the scene and then silently vanish. Editor preview
        /// sets it deliberately.
        /// </remarks>
        public bool AllowEditModeMaterialWrite { get; }

        /// <summary>Alpha used when the policy is <see cref="ColorAlphaPolicy.Explicit"/>.</summary>
        public float ExplicitAlpha { get; }

        /// <summary>Creates a context.</summary>
        /// <param name="materialPropertyName">Shader property for the material channel.</param>
        /// <param name="allowEditModeMaterialWrite">Whether edit-mode material writes are allowed.</param>
        /// <param name="explicitAlpha">Alpha for <see cref="ColorAlphaPolicy.Explicit"/>.</param>
        public ColorBindingContext(string materialPropertyName = null,
            bool allowEditModeMaterialWrite = false, float explicitAlpha = 1f)
        {
            MaterialPropertyName = materialPropertyName;
            AllowEditModeMaterialWrite = allowEditModeMaterialWrite;
            ExplicitAlpha = explicitAlpha;
        }
    }

    /// <summary>The outcome of one adapter application, with enough detail to act on a failure.</summary>
    public readonly struct ColorApplyResult
    {
        /// <summary>What happened.</summary>
        public ColorApplyOutcome Outcome { get; }

        /// <summary>
        /// Author-facing explanation for a non-<see cref="ColorApplyOutcome.Applied"/> outcome, or
        /// <c>null</c>.
        /// </summary>
        public string Detail { get; }

        /// <summary>Whether the colour reached the target.</summary>
        public bool Applied => Outcome == ColorApplyOutcome.Applied;

        /// <summary>
        /// Whether this outcome represents a problem an author should fix.
        /// </summary>
        /// <remarks>
        /// <see cref="ColorApplyOutcome.SkippedInEditMode"/> is a deliberate no-op, not a fault, so it
        /// is excluded — reporting it would produce constant noise in the editor for correct setups.
        /// </remarks>
        public bool IsProblem => Outcome != ColorApplyOutcome.Applied
                                 && Outcome != ColorApplyOutcome.SkippedInEditMode;

        /// <summary>Creates a result.</summary>
        /// <param name="outcome">What happened.</param>
        /// <param name="detail">Author-facing explanation, for failures.</param>
        public ColorApplyResult(ColorApplyOutcome outcome, string detail = null)
        {
            Outcome = outcome;
            Detail = detail;
        }

        /// <summary>The success result.</summary>
        public static ColorApplyResult Success => new ColorApplyResult(ColorApplyOutcome.Applied);

        /// <summary>A failure result.</summary>
        /// <param name="outcome">The failure kind.</param>
        /// <param name="detail">Author-facing explanation.</param>
        public static ColorApplyResult Fail(ColorApplyOutcome outcome, string detail = null) =>
            new ColorApplyResult(outcome, detail);

        /// <inheritdoc/>
        public override string ToString() =>
            string.IsNullOrEmpty(Detail) ? Outcome.ToString() : $"{Outcome}: {Detail}";
    }

    /// <summary>
    /// Extension seam for colouring a component type Core does not know about.
    /// </summary>
    /// <remarks>
    /// An SDK layer or fork implements this and registers it with
    /// <see cref="ColorTargetAdapterRegistry"/> during subsystem initialization, so a fork-specific
    /// target needs no change to Core and Core needs no dependency on the fork.
    /// <para/>
    /// Registration is explicit. There is deliberately no scene reflection or attribute scan: adapter
    /// order decides which one claims a component, and a discovery mechanism whose order depends on
    /// assembly load order is not something a project can reason about.
    /// <para/>
    /// Implementations must be stateless and main-thread only, and must not allocate per call —
    /// <see cref="Apply"/> runs once per binding on every theme change.
    /// </remarks>
    public interface IColorTargetAdapter
    {
        /// <summary>Whether this adapter can write <paramref name="channel"/> on <paramref name="target"/>.</summary>
        /// <param name="target">The candidate component. Never <c>null</c> when called by the registry.</param>
        /// <param name="channel">The requested channel; may be <c>null</c> or empty for the default.</param>
        bool CanHandle(Component target, string channel);

        /// <summary>Writes <paramref name="value"/> to the target's channel.</summary>
        /// <param name="target">The component to colour.</param>
        /// <param name="channel">The channel to write; may be <c>null</c> or empty for the default.</param>
        /// <param name="value">The final colour, with alpha policy already applied.</param>
        /// <param name="context">Additional inputs such as the material property name.</param>
        /// <returns>What happened, for caller-side diagnostics.</returns>
        ColorApplyResult Apply(Component target, string channel, Color value,
            ColorBindingContext context);
    }
}
