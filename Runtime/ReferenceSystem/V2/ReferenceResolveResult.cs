using System;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// The full result of one resolve attempt: what happened, what was asked for, and what was
    /// found.
    /// </summary>
    /// <remarks>
    /// V1's resolve returned the object or null, so "never assigned" (often fine), "the scene isn't
    /// loaded yet" (usually fine, briefly) and "two providers claim this id" (never fine) all
    /// arrived at the caller as the same <c>null</c>. Callers could not react differently because
    /// they were not told anything different.
    ///
    /// The result deliberately carries no finding code or severity. Those belong to the editor's
    /// <c>ReferenceSeverityPolicy</c>, which is overridable per project; duplicating the mapping
    /// here would create a second table to drift out of sync with the first. Editor surfaces map
    /// <see cref="Outcome"/> — the shared vocabulary — to a code themselves.
    /// </remarks>
    public readonly struct ReferenceResolveResult
    {
        /// <summary>What happened.</summary>
        public ReferenceResolveOutcome Outcome { get; }

        /// <summary>The key the resolve asked for.</summary>
        public ReferenceRuntimeKey RequestedKey { get; }

        /// <summary>The provider that was found, or null.</summary>
        public IReferenceable Provider { get; }

        /// <summary>
        /// How many providers matched. Greater than one means the request was ambiguous and
        /// deliberately refused rather than guessed.
        /// </summary>
        public int CandidateCount { get; }

        /// <summary>The type the site asked for.</summary>
        public Type ExpectedType { get; }

        /// <summary>The type actually found, when one was.</summary>
        public Type ActualType { get; }

        /// <summary>A human-readable explanation, safe to log verbatim.</summary>
        public string Summary { get; }

        /// <summary>True when a usable provider was produced.</summary>
        public bool IsResolved =>
            Provider != null &&
            (Outcome == ReferenceResolveOutcome.ResolvedExact ||
             Outcome == ReferenceResolveOutcome.ResolvedViaLegacyFallback ||
             Outcome == ReferenceResolveOutcome.WrongRefType);

        /// <summary>
        /// True when the reference resolved, but only through a compatibility path whose serialized
        /// data is stale. Worth migrating; not worth failing over.
        /// </summary>
        public bool IsStale =>
            Outcome == ReferenceResolveOutcome.ResolvedViaLegacyFallback ||
            Outcome == ReferenceResolveOutcome.WrongRefType;

        /// <summary>
        /// True when waiting longer could still succeed, so this outcome must not be reported as a
        /// failure while the owner is inside its registration window.
        /// </summary>
        public bool IsPending =>
            Outcome == ReferenceResolveOutcome.ProviderMissing ||
            Outcome == ReferenceResolveOutcome.ProviderNotLoaded ||
            Outcome == ReferenceResolveOutcome.RegistryUnavailable;

        internal ReferenceResolveResult(
            ReferenceResolveOutcome outcome,
            ReferenceRuntimeKey requestedKey,
            IReferenceable provider = null,
            int candidateCount = 0,
            Type expectedType = null,
            Type actualType = null,
            string summary = null)
        {
            Outcome = outcome;
            RequestedKey = requestedKey;
            Provider = provider;
            CandidateCount = candidateCount;
            ExpectedType = expectedType;
            ActualType = actualType;
            Summary = summary ?? DefaultSummary(outcome, requestedKey, expectedType, actualType, candidateCount);
        }

        private static string DefaultSummary(
            ReferenceResolveOutcome outcome,
            ReferenceRuntimeKey key,
            Type expected,
            Type actual,
            int candidates) => outcome switch
        {
            ReferenceResolveOutcome.ResolvedExact => $"resolved {key}",
            ReferenceResolveOutcome.ResolvedViaLegacyFallback =>
                $"resolved '{key.RefId}' by id alone; the stored type '{key.RefType}' no longer matches",
            ReferenceResolveOutcome.NotAssigned => "reference was never assigned",
            ReferenceResolveOutcome.ProviderMissing => $"no provider holds {key}",
            ReferenceResolveOutcome.ProviderNotLoaded => $"the provider for {key} is not currently loaded",
            ReferenceResolveOutcome.DuplicateProvider => $"{candidates} providers claim {key}",
            ReferenceResolveOutcome.AmbiguousFallback =>
                $"'{key.RefId}' matches {candidates} providers by id alone; refusing to guess",
            ReferenceResolveOutcome.WrongRuntimeType =>
                $"{key} resolved to {actual?.Name ?? "<unknown>"}, which is not a {expected?.Name ?? "<unknown>"}",
            ReferenceResolveOutcome.WrongRefType => $"{key} resolved, but its stored type is stale",
            ReferenceResolveOutcome.WrongScope => $"the provider for '{key.RefId}' lives in a different scope",
            ReferenceResolveOutcome.RegistryUnavailable => "the ReferenceManager subsystem is not available",
            ReferenceResolveOutcome.TimedOut => $"{key} did not register within the wait budget",
            ReferenceResolveOutcome.Cancelled => "the resolve was cancelled",
            ReferenceResolveOutcome.InvalidSerializedData => "the serialized reference is malformed",
            ReferenceResolveOutcome.CoverageIncomplete => "resolution could not see everything it needed",
            _ => outcome.ToString(),
        };

        /// <summary>The resolved provider as <typeparamref name="T"/>, or null.</summary>
        /// <typeparam name="T">The expected provider type.</typeparam>
        public T As<T>() where T : class, IReferenceable => IsResolved ? Provider as T : null;

        /// <inheritdoc/>
        public override string ToString() => $"{Outcome}: {Summary}";
    }
}
