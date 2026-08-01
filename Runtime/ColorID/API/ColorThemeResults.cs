using System;
using System.Collections.Generic;

namespace Molca.ColorID
{
    /// <summary>The outcome of attempting to activate a theme variant.</summary>
    /// <remarks>
    /// Typed rather than a bool so a caller can distinguish "you asked for something that does not
    /// exist" from "the theme itself is broken" from "it was already active" — three situations that
    /// need three different responses, and which V1 collapsed into a log line.
    /// </remarks>
    public enum ColorThemeActivation
    {
        /// <summary>The variant was activated and a new snapshot published.</summary>
        Activated = 0,

        /// <summary>The requested variant was already active; no snapshot was published.</summary>
        AlreadyActive = 1,

        /// <summary>The theme set does not declare the requested variant.</summary>
        UnknownVariant = 2,

        /// <summary>The theme set failed structural validation.</summary>
        InvalidThemeSet = 3,

        /// <summary>A required token had no resolvable value in the requested variant.</summary>
        MissingRequiredToken = 4,

        /// <summary>Alias resolution found a cycle, or a chain deeper than the documented maximum.</summary>
        AliasCycle = 5,

        /// <summary>No theme settings module or no theme set is configured.</summary>
        SettingsUnavailable = 6,

        /// <summary>The variant activated, but the preference could not be persisted.</summary>
        PersistenceFailed = 7,

        /// <summary>Activation was cancelled, typically because the application is shutting down.</summary>
        Cancelled = 8
    }

    /// <summary>The result of an activation attempt, including why it failed.</summary>
    public readonly struct ColorThemeActivationResult
    {
        /// <summary>What happened.</summary>
        public ColorThemeActivation Outcome { get; }

        /// <summary>
        /// The variant active after the attempt. On failure this is the variant that <i>remains</i>
        /// active — the last known good — not the one that was requested.
        /// </summary>
        public string ActiveVariantId { get; }

        /// <summary>Author-facing explanations of what went wrong. Empty on success.</summary>
        public IReadOnlyList<string> Diagnostics { get; }

        /// <summary>Whether a new snapshot was published.</summary>
        public bool Published => Outcome == ColorThemeActivation.Activated;

        /// <summary>
        /// Whether the theme is usable after this attempt, whether or not the request succeeded.
        /// </summary>
        /// <remarks>
        /// <see cref="ColorThemeActivation.AlreadyActive"/> and a failure that preserved the previous
        /// snapshot both leave a working theme — a caller reacting to "did my request go through"
        /// wants <see cref="Published"/>, one reacting to "can I render" wants this.
        /// </remarks>
        public bool HasUsableTheme => Outcome == ColorThemeActivation.Activated
                                      || Outcome == ColorThemeActivation.AlreadyActive
                                      || Outcome == ColorThemeActivation.PersistenceFailed
                                      || !string.IsNullOrEmpty(ActiveVariantId);

        /// <summary>Creates a result.</summary>
        /// <param name="outcome">What happened.</param>
        /// <param name="activeVariantId">The variant active after the attempt.</param>
        /// <param name="diagnostics">Author-facing explanations, if any.</param>
        public ColorThemeActivationResult(ColorThemeActivation outcome, string activeVariantId,
            IReadOnlyList<string> diagnostics = null)
        {
            Outcome = outcome;
            ActiveVariantId = activeVariantId;
            Diagnostics = diagnostics ?? Array.Empty<string>();
        }

        /// <summary>A one-line summary for logs and validation reports.</summary>
        public override string ToString()
        {
            string summary = $"{Outcome} (active: {ActiveVariantId ?? "<none>"})";
            return Diagnostics.Count == 0
                ? summary
                : $"{summary}\n  {string.Join("\n  ", Diagnostics)}";
        }
    }

    /// <summary>Why the active theme changed.</summary>
    public enum ColorThemeChangeReason
    {
        /// <summary>The first activation during bootstrap.</summary>
        Initialized = 0,

        /// <summary>An explicit request to change variant.</summary>
        VariantChanged = 1,

        /// <summary>A refresh with no variant change, typically an editor-driven reapply.</summary>
        Refreshed = 2,

        /// <summary>Activation failed and the last known good snapshot was restored.</summary>
        RestoredLastKnownGood = 3
    }

    /// <summary>
    /// The immutable payload published when the active theme changes.
    /// </summary>
    /// <remarks>
    /// Carries the resolved snapshot directly so a subscriber never has to call back into the service
    /// to find out what it should apply — which also means a subscriber cannot accidentally read a
    /// <i>newer</i> snapshot than the one the notification was about.
    /// </remarks>
    public readonly struct ColorThemeChanged
    {
        /// <summary>The variant that was active before, or <c>null</c> on first activation.</summary>
        public string PreviousVariantId { get; }

        /// <summary>The variant that is active now.</summary>
        public string ActiveVariantId { get; }

        /// <summary>The snapshot bindings should apply.</summary>
        public ResolvedColorTheme Theme { get; }

        /// <summary>Convenience accessor for <see cref="ResolvedColorTheme.Generation"/>.</summary>
        public int Generation => Theme?.Generation ?? 0;

        /// <summary>Why the change happened.</summary>
        public ColorThemeChangeReason Reason { get; }

        /// <summary>Creates a change payload.</summary>
        /// <param name="previousVariantId">The previously active variant, or <c>null</c>.</param>
        /// <param name="theme">The newly published snapshot.</param>
        /// <param name="reason">Why the change happened.</param>
        public ColorThemeChanged(string previousVariantId, ResolvedColorTheme theme,
            ColorThemeChangeReason reason)
        {
            PreviousVariantId = previousVariantId;
            Theme = theme;
            ActiveVariantId = theme?.VariantId;
            Reason = reason;
        }
    }
}
