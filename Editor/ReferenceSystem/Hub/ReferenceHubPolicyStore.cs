using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// The authored severity policy for <i>editor</i> reference audits, persisted per project.
    /// </summary>
    /// <remarks>
    /// <para><b>This policy never reaches a build.</b> The build gate always uses
    /// <see cref="ReferenceSeverityPolicy.Default"/>. That is not an oversight: these overrides live in
    /// <see cref="MolcaEditorPrefs"/>, which is per-user machine state and is not committed, so letting them
    /// decide whether a build fails would make the same commit pass on one machine and fail on another. What
    /// they are for is triage — muting a class of finding you have already decided about while you work
    /// through the rest — and that is an editor concern.</para>
    ///
    /// <para>Codes the policy refuses to lower (<see cref="ReferenceSeverityPolicy.IsNonLowerable"/>) cannot
    /// be authored down here either; the store drops such an override rather than storing one that will be
    /// silently ignored, so the UI and the effective policy always agree.</para>
    ///
    /// <para>The built policy instance is cached and only replaced when the overrides change, because
    /// <see cref="ReferenceAuditScope"/> compares policies by reference when deciding whether a cached
    /// snapshot still answers — handing out a fresh equal instance per call would turn every audit request
    /// into a full rescan.</para>
    /// </remarks>
    public static class ReferenceHubPolicyStore
    {
        private const string OverridesKey = "Molca.References.SeverityOverrides";

        private static Dictionary<ReferenceFindingCode, ReferenceFindingSeverity> _overrides;
        private static ReferenceSeverityPolicy _policy;

        /// <summary>Raised when the authored overrides change.</summary>
        public static event Action Changed;

        /// <summary>
        /// The policy to use for editor-initiated audits: <see cref="ReferenceSeverityPolicy.Default"/> when
        /// nothing is overridden, otherwise the authored one. Cached; stable by reference between edits.
        /// </summary>
        public static ReferenceSeverityPolicy Policy =>
            _policy ??= ReferenceSeverityPolicy.With(Overrides);

        /// <summary>The authored overrides, keyed by finding code. Never null.</summary>
        public static IReadOnlyDictionary<ReferenceFindingCode, ReferenceFindingSeverity> Overrides =>
            _overrides ??= Load();

        /// <summary>True when the project has authored at least one override.</summary>
        public static bool HasOverrides => Overrides.Count > 0;

        /// <summary>Every code the policy can express, in numeric order.</summary>
        public static IReadOnlyList<ReferenceFindingCode> AllCodes { get; } =
            Enum.GetValues(typeof(ReferenceFindingCode))
                .Cast<ReferenceFindingCode>()
                .OrderBy(c => (int)c)
                .ToList();

        /// <summary>
        /// The severity <paramref name="code"/> currently resolves to under the authored policy.
        /// </summary>
        /// <param name="code">The finding code.</param>
        public static ReferenceFindingSeverity Effective(ReferenceFindingCode code) => Policy.SeverityFor(code);

        /// <summary>
        /// The severity <paramref name="code"/> has with no override, so the UI can show what an override
        /// is departing from.
        /// </summary>
        /// <param name="code">The finding code.</param>
        public static ReferenceFindingSeverity Baseline(ReferenceFindingCode code) =>
            ReferenceSeverityPolicy.Default.SeverityFor(code);

        /// <summary>
        /// Sets or clears the override for one code.
        /// </summary>
        /// <param name="code">The finding code to configure.</param>
        /// <param name="severity">
        /// The severity to report it at, or null to fall back to the baseline. Ignored for a non-lowerable
        /// code.
        /// </param>
        /// <returns>True when the stored overrides changed.</returns>
        public static bool SetOverride(ReferenceFindingCode code, ReferenceFindingSeverity? severity)
        {
            if (ReferenceSeverityPolicy.IsNonLowerable(code))
                return false;

            var current = new Dictionary<ReferenceFindingCode, ReferenceFindingSeverity>(Overrides);
            var hadOverride = current.TryGetValue(code, out var existing);

            // Setting a code back to its baseline is a clear, not an override: storing it would report the
            // code as "configured" in the UI while changing nothing.
            if (severity == null || severity.Value == Baseline(code))
            {
                if (!hadOverride)
                    return false;
                current.Remove(code);
            }
            else
            {
                if (hadOverride && existing == severity.Value)
                    return false;
                current[code] = severity.Value;
            }

            Store(current);
            return true;
        }

        /// <summary>Clears every authored override.</summary>
        /// <returns>True when something was cleared.</returns>
        public static bool ClearOverrides()
        {
            if (!HasOverrides)
                return false;

            Store(new Dictionary<ReferenceFindingCode, ReferenceFindingSeverity>());
            return true;
        }

        /// <summary>
        /// Human-readable summary of the authored departures from the baseline, for the policy card.
        /// </summary>
        public static string Describe()
        {
            if (!HasOverrides)
                return "production severities (nothing overridden)";

            return string.Join(", ", Overrides
                .OrderBy(kv => (int)kv.Key)
                .Select(kv => $"REF{(int)kv.Key:D3} → {kv.Value}"));
        }

        private static void Store(Dictionary<ReferenceFindingCode, ReferenceFindingSeverity> overrides)
        {
            _overrides = overrides;
            _policy = ReferenceSeverityPolicy.With(overrides);

            MolcaEditorPrefs.SetString(OverridesKey, string.Join(";",
                overrides.OrderBy(kv => (int)kv.Key).Select(kv => $"{(int)kv.Key}={(int)kv.Value}")));

            // The cached snapshot was analysed under the previous policy, so its severities — and therefore
            // the header state derived from them — no longer describe the configured rules.
            ReferenceAuditService.Invalidate("the reference severity policy changed");
            Changed?.Invoke();
        }

        private static Dictionary<ReferenceFindingCode, ReferenceFindingSeverity> Load()
        {
            var result = new Dictionary<ReferenceFindingCode, ReferenceFindingSeverity>();
            var raw = MolcaEditorPrefs.GetString(OverridesKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return result;

            foreach (var entry in raw.Split(';'))
            {
                var parts = entry.Split('=');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out var code)
                    || !int.TryParse(parts[1], out var severity))
                    continue;

                var findingCode = (ReferenceFindingCode)code;

                // Drop anything the policy would ignore anyway — a stored override for a non-lowerable code
                // (or for a code a later release removed) would otherwise show in the UI as configured while
                // having no effect.
                if (!Enum.IsDefined(typeof(ReferenceFindingCode), findingCode)
                    || !Enum.IsDefined(typeof(ReferenceFindingSeverity), severity)
                    || ReferenceSeverityPolicy.IsNonLowerable(findingCode))
                    continue;

                result[findingCode] = (ReferenceFindingSeverity)severity;
            }

            return result;
        }
    }
}
