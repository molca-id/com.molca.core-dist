using System;
using System.Collections.Generic;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// The single place that decides how much each finding code matters.
    /// </summary>
    /// <remarks>
    /// Severity is centralized so Doctor, the build gate and the Hub cannot label the same code
    /// differently. Some codes are deliberately not lowerable: a duplicate provider, an ambiguous
    /// fallback, or a target of the wrong type is a runtime failure, and letting a project configure it
    /// down to a warning would reintroduce exactly the build-green/runtime-broken gap this system exists
    /// to close. <see cref="Relaxed"/> exists for development builds and lowers only the codes that are
    /// safe to defer.
    /// </remarks>
    public sealed class ReferenceSeverityPolicy
    {
        /// <summary>
        /// Codes whose severity is fixed at <see cref="ReferenceFindingSeverity.Error"/>. Attempting to
        /// override one is ignored rather than honoured.
        /// </summary>
        private static readonly HashSet<ReferenceFindingCode> NonLowerable = new()
        {
            ReferenceFindingCode.DuplicateProvider,
            ReferenceFindingCode.AmbiguousLegacyFallback,
            ReferenceFindingCode.WrongRuntimeType,

            // Both are authored declarations that cannot work as written: a Required field with no target
            // throws at runtime, and a prefab-local reference with no scope root has its registration
            // refused outright. Neither is hygiene an iteration build can defer.
            ReferenceFindingCode.RequiredReferenceUnset,
            ReferenceFindingCode.ScopeRootMissing,
        };

        private static readonly Dictionary<ReferenceFindingCode, ReferenceFindingSeverity> Baseline = new()
        {
            [ReferenceFindingCode.MissingProvider] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.DuplicateProvider] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.AmbiguousLegacyFallback] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.WrongRuntimeType] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.WrongRefTypeMetadata] = ReferenceFindingSeverity.Warning,
            [ReferenceFindingCode.ProviderIdMissing] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.RequiredReferenceUnset] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.ScopeRootMissing] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.SceneUnavailable] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.StaleEditorMetadata] = ReferenceFindingSeverity.Info,
            [ReferenceFindingCode.UnsupportedReferenceShape] = ReferenceFindingSeverity.Warning,
            [ReferenceFindingCode.AssetScanFailed] = ReferenceFindingSeverity.Error,
            [ReferenceFindingCode.CoveragePartial] = ReferenceFindingSeverity.Warning,
        };

        private readonly Dictionary<ReferenceFindingCode, ReferenceFindingSeverity> _overrides;

        private ReferenceSeverityPolicy(Dictionary<ReferenceFindingCode, ReferenceFindingSeverity> overrides)
        {
            _overrides = overrides ?? new Dictionary<ReferenceFindingCode, ReferenceFindingSeverity>();
        }

        /// <summary>The production policy. Every real breakage is an error.</summary>
        public static ReferenceSeverityPolicy Default { get; } =
            new ReferenceSeverityPolicy(null);

        /// <summary>
        /// Development policy: lowers the codes that only describe stale or incomplete state so an
        /// iteration build is not blocked by them. The non-lowerable codes stay errors.
        /// </summary>
        public static ReferenceSeverityPolicy Relaxed { get; } =
            new ReferenceSeverityPolicy(new Dictionary<ReferenceFindingCode, ReferenceFindingSeverity>
            {
                [ReferenceFindingCode.CoveragePartial] = ReferenceFindingSeverity.Info,
                [ReferenceFindingCode.AssetScanFailed] = ReferenceFindingSeverity.Warning,
                [ReferenceFindingCode.ProviderIdMissing] = ReferenceFindingSeverity.Warning,

                // Scene availability depends on the declared load sets, which an iteration build may
                // legitimately not match — unlike the codes above it, this one describes configuration
                // rather than a reference that cannot work.
                [ReferenceFindingCode.SceneUnavailable] = ReferenceFindingSeverity.Warning,
            });

        /// <summary>The severity for <paramref name="code"/> under this policy.</summary>
        public ReferenceFindingSeverity SeverityFor(ReferenceFindingCode code)
        {
            var baseline = Baseline.TryGetValue(code, out var s) ? s : ReferenceFindingSeverity.Warning;

            if (NonLowerable.Contains(code))
                return baseline;

            return _overrides.TryGetValue(code, out var overridden) ? overridden : baseline;
        }

        /// <summary>True when <paramref name="code"/> can never be configured below error.</summary>
        public static bool IsNonLowerable(ReferenceFindingCode code) => NonLowerable.Contains(code);

        /// <summary>
        /// Builds a policy from explicit overrides, ignoring any attempt to lower a non-lowerable code.
        /// </summary>
        /// <param name="overrides">Code-to-severity overrides. Null means <see cref="Default"/>.</param>
        public static ReferenceSeverityPolicy With(
            IReadOnlyDictionary<ReferenceFindingCode, ReferenceFindingSeverity> overrides)
        {
            if (overrides == null || overrides.Count == 0)
                return Default;

            var copy = new Dictionary<ReferenceFindingCode, ReferenceFindingSeverity>();
            foreach (var kv in overrides)
            {
                if (!NonLowerable.Contains(kv.Key))
                    copy[kv.Key] = kv.Value;
            }

            return new ReferenceSeverityPolicy(copy);
        }
    }
}
