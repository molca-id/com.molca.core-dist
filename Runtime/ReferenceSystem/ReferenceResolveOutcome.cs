namespace Molca.ReferenceSystem
{
    /// <summary>
    /// The single vocabulary for "what happened when a reference was resolved", shared by the
    /// runtime resolve path and every editor analysis surface (Doctor, build validation,
    /// Sequence validation, MCP).
    /// </summary>
    /// <remarks>
    /// Before this existed each surface invented its own boolean/null result, so a build could go
    /// green on an ID-only existence check while the runtime rejected the same reference as
    /// ambiguous. Any new resolution consumer must report one of these values rather than a bare
    /// <c>bool</c>, so the surfaces cannot drift apart again.
    ///
    /// Some members are reserved for the scoped reference model that lands later
    /// (<see cref="WrongScope"/>, <see cref="ProviderNotLoaded"/>) and are not produced yet; they
    /// are declared up front so consumers written today already switch exhaustively.
    /// </remarks>
    public enum ReferenceResolveOutcome
    {
        /// <summary>The stored <c>(RefType, RefId)</c> matched exactly one provider.</summary>
        ResolvedExact = 0,

        /// <summary>
        /// The stored <c>RefType</c> did not match, but exactly one provider carried the stored
        /// <c>RefId</c>. Recoverable, but the serialized data is stale.
        /// </summary>
        ResolvedViaLegacyFallback = 1,

        /// <summary>No <c>RefId</c> was stored. Legal for an optional reference.</summary>
        NotAssigned = 2,

        /// <summary>No provider in scope carries the stored <c>RefId</c>.</summary>
        ProviderMissing = 3,

        /// <summary>
        /// A provider exists in the project but is not part of the declared availability set
        /// (e.g. it lives in a scene that is not loaded). Reserved for the load-set model.
        /// </summary>
        ProviderNotLoaded = 4,

        /// <summary>
        /// More than one provider claims the same exact <c>(RefType, RefId)</c>. Runtime
        /// registration rejects the second one, so which provider wins depends on load order.
        /// </summary>
        DuplicateProvider = 5,

        /// <summary>
        /// The <c>RefType</c> did not match and more than one provider carries the stored
        /// <c>RefId</c>, so the compatibility fallback cannot pick one. Runtime refuses to guess.
        /// </summary>
        AmbiguousFallback = 6,

        /// <summary>The matched provider is not assignable to the type the site expects.</summary>
        WrongRuntimeType = 7,

        /// <summary>
        /// The matched provider's current <c>RefType</c> differs from the serialized one. The
        /// reference still resolves; the cached metadata is stale.
        /// </summary>
        WrongRefType = 8,

        /// <summary>The provider exists but in a different reference scope. Reserved.</summary>
        WrongScope = 9,

        /// <summary>The <see cref="ReferenceManager"/> subsystem was unavailable.</summary>
        RegistryUnavailable = 10,

        /// <summary>A deferred resolve exceeded its wait budget without the provider registering.</summary>
        TimedOut = 11,

        /// <summary>The resolve was cancelled by its caller or by owner teardown.</summary>
        Cancelled = 12,

        /// <summary>The serialized reference record itself is malformed.</summary>
        InvalidSerializedData = 13,

        /// <summary>
        /// The analysis could not see everything it needed, so the result is not authoritative.
        /// Never report this as success.
        /// </summary>
        CoverageIncomplete = 14,
    }
}
