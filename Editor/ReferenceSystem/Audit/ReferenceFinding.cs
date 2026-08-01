using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Machine-readable finding codes. The numeric value is the stable contract: it appears in Doctor
    /// output, build failure messages, and MCP payloads, so a code is never renumbered or reused.
    /// </summary>
    public enum ReferenceFindingCode
    {
        /// <summary>REF001 — no provider in scope carries the stored Ref Id.</summary>
        MissingProvider = 1,

        /// <summary>REF002 — two or more providers claim the same exact <c>(RefType, RefId)</c>.</summary>
        DuplicateProvider = 2,

        /// <summary>REF003 — the Ref Type is stale and more than one provider carries the Ref Id.</summary>
        AmbiguousLegacyFallback = 3,

        /// <summary>REF004 — the matched provider is not assignable to the type the site expects.</summary>
        WrongRuntimeType = 4,

        /// <summary>REF005 — the reference resolves, but the serialized Ref Type is out of date.</summary>
        WrongRefTypeMetadata = 5,

        /// <summary>
        /// REF006 — a reference declared <c>Required</c> or <c>DeferredRequired</c> has no target.
        /// </summary>
        /// <remarks>
        /// Undetectable before authored requiredness existed: every unset reference was equally legal, so
        /// a field the author forgot to wire looked exactly like one deliberately left empty. It surfaced
        /// as a null at runtime instead of as an error in the editor.
        /// </remarks>
        RequiredReferenceUnset = 6,

        /// <summary>
        /// REF007 — a prefab-local reference or provider has no enclosing <c>ReferenceScopeRoot</c>.
        /// </summary>
        /// <remarks>
        /// Reported here rather than left to the runtime, where it appears only as a registration refusal
        /// or a <c>WrongScope</c> resolve with no indication of what is actually missing.
        /// </remarks>
        ScopeRootMissing = 7,

        /// <summary>REF008 — a provider carries no Ref Id, so nothing can reference it.</summary>
        ProviderIdMissing = 8,

        /// <summary>
        /// REF009 — the target's scene is never loaded alongside the owner's under any declared load set.
        /// </summary>
        /// <remarks>
        /// Only produced when the project supplies load sets. Without them nothing is known about
        /// concurrency, and asserting unavailability from a guess would be worse than staying silent.
        /// </remarks>
        SceneUnavailable = 9,

        /// <summary>REF013 — the cached display name in the serialized reference is stale.</summary>
        StaleEditorMetadata = 13,

        /// <summary>REF014 — a reference-shaped field the scanner cannot analyse.</summary>
        UnsupportedReferenceShape = 14,

        /// <summary>REF015 — an asset could not be scanned; the result for it is unknown, not clean.</summary>
        AssetScanFailed = 15,

        /// <summary>REF016 — the audit did not cover everything, so "no findings" is not "clean".</summary>
        CoveragePartial = 16,
    }

    /// <summary>How much a finding matters.</summary>
    public enum ReferenceFindingSeverity
    {
        /// <summary>Informational; no action required.</summary>
        Info = 0,

        /// <summary>Works today but is fragile or stale.</summary>
        Warning = 1,

        /// <summary>Will fail, or already fails, at runtime.</summary>
        Error = 2,
    }

    /// <summary>
    /// One problem the audit found, addressed to a specific serialized property and carrying every
    /// candidate provider that contributed to it.
    /// </summary>
    /// <remarks>
    /// Findings are the single currency between the audit engine and its consumers. Doctor, build
    /// validation, Sequence validation and MCP all project these rather than re-deriving their own —
    /// which is what let their duplicate and ambiguity rules disagree previously.
    /// </remarks>
    public sealed class ReferenceFinding
    {
        /// <summary>The machine-readable code.</summary>
        public ReferenceFindingCode Code { get; }

        /// <summary>Severity assigned by <see cref="ReferenceSeverityPolicy"/>.</summary>
        public ReferenceFindingSeverity Severity { get; }

        /// <summary>Short title, suitable for a table row.</summary>
        public string Title { get; }

        /// <summary>Full explanation including identities and locations.</summary>
        public string Summary { get; }

        /// <summary>Asset path the finding is anchored to, for navigation. May be empty.</summary>
        public string AssetPath { get; }

        /// <summary><see cref="ReferenceSiteRecord.SiteKey"/> of the offending site, or empty for a provider-only finding.</summary>
        public string SourceSiteKey { get; }

        /// <summary>Provider keys that matched, or collided. Never null.</summary>
        public IReadOnlyList<string> CandidateProviderKeys { get; }

        /// <summary>The resolution outcome that produced this finding, when there was one.</summary>
        public Molca.ReferenceSystem.ReferenceResolveOutcome? Outcome { get; }

        /// <summary>The <c>REFnnn</c> string form of <see cref="Code"/>.</summary>
        public string CodeString => $"REF{(int)Code:D3}";

        internal ReferenceFinding(
            ReferenceFindingCode code,
            ReferenceFindingSeverity severity,
            string title,
            string summary,
            string assetPath = null,
            string sourceSiteKey = null,
            IReadOnlyList<string> candidateProviderKeys = null,
            Molca.ReferenceSystem.ReferenceResolveOutcome? outcome = null)
        {
            Code = code;
            Severity = severity;
            Title = title ?? string.Empty;
            Summary = summary ?? string.Empty;
            AssetPath = assetPath ?? string.Empty;
            SourceSiteKey = sourceSiteKey ?? string.Empty;
            CandidateProviderKeys = candidateProviderKeys ?? Array.Empty<string>();
            Outcome = outcome;
        }

        /// <summary>One-line form used by Doctor, build errors, and logs: <c>REF001 title — summary</c>.</summary>
        public string ToMessage() => $"{CodeString} {Title} — {Summary}";

        /// <inheritdoc/>
        public override string ToString() => ToMessage();

        /// <summary>
        /// Deterministic ordering so two runs over the same project emit findings in the same order —
        /// a precondition for diffing audit results and for stable test assertions.
        /// </summary>
        internal static IEnumerable<ReferenceFinding> InStableOrder(IEnumerable<ReferenceFinding> findings) =>
            findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Code)
                .ThenBy(f => f.AssetPath, StringComparer.Ordinal)
                .ThenBy(f => f.SourceSiteKey, StringComparer.Ordinal)
                .ThenBy(f => f.Summary, StringComparer.Ordinal);
    }
}
