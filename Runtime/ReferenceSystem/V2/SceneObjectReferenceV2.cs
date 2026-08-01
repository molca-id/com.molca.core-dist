using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// A serialized reference to a provider, carrying the scope its id is meaningful in and how much
    /// the owner depends on it.
    /// </summary>
    /// <remarks>
    /// The successor to <see cref="SceneObjectReference"/>, which stored only <c>(RefType, RefId)</c>
    /// and therefore could not express "the step inside <i>this</i> prefab instance". Both types
    /// coexist: v1 data keeps working through <see cref="ReferenceScopeKind.LegacyGlobal"/>, and a
    /// v1 field is only rewritten by an explicit, previewable migration.
    ///
    /// <para><b>Prefab-local resolution needs a context.</b> The serialized scope id of a
    /// prefab-local reference names the prefab <i>template</i>, which every instance shares; the live
    /// key needs the instance's own scope id. So the resolve entry points take the owning
    /// <see cref="Component"/> and find its nearest <see cref="ReferenceScopeRoot"/>. Resolving a
    /// prefab-local reference without a context cannot be made to work, and reports
    /// <see cref="ReferenceResolveOutcome.WrongScope"/> rather than silently reaching into whichever
    /// instance happens to have registered first.</para>
    /// </remarks>
    [Serializable]
    public struct SceneObjectReferenceV2
    {
        [SerializeField] private string targetId;
        [SerializeField] private ReferenceScopeKind scopeKind;
        [SerializeField] private string scopeId;
        [SerializeField] private string expectedRefType;
        [SerializeField] private ReferenceRequiredness requiredness;
        [SerializeField] private ReferenceAvailabilityPolicy availability;

#if UNITY_EDITOR
        [SerializeField] private string targetAssetGuid;
        [SerializeField] private long targetLocalFileId;
        [SerializeField] private string cachedDisplayName;
#endif

        /// <summary>Default bound (seconds) for the await-until-registered wait.</summary>
        public const float DefaultResolveTimeoutSeconds = 5f;

        /// <summary>The target's id, unique within <see cref="ScopeId"/>.</summary>
        public string TargetId => targetId ?? string.Empty;

        /// <summary>The space the target's id is meaningful in.</summary>
        public ReferenceScopeKind ScopeKind => scopeKind;

        /// <summary>
        /// The authored scope: a scene identity for <see cref="ReferenceScopeKind.Scene"/>, and the
        /// prefab's scope <i>template</i> id for <see cref="ReferenceScopeKind.PrefabLocal"/> — not
        /// the live instance, which only exists at runtime.
        /// </summary>
        public string ScopeId => scopeId ?? string.Empty;

        /// <summary>The target's type category.</summary>
        public string ExpectedRefType => expectedRefType ?? string.Empty;

        /// <summary>How much the owner depends on this resolving.</summary>
        public ReferenceRequiredness Requiredness => requiredness;

        /// <summary>When the target is expected to be available.</summary>
        public ReferenceAvailabilityPolicy Availability => availability;

        /// <summary>True when a target has been assigned.</summary>
        public bool IsAssigned => !string.IsNullOrEmpty(targetId);

        /// <summary>Kept for parity with <see cref="SceneObjectReference.IsValid"/>.</summary>
        public bool IsValid => IsAssigned;

        /// <summary>True when this reference still carries v1's implicit project-wide scope.</summary>
        public bool IsLegacy => scopeKind == ReferenceScopeKind.LegacyGlobal;

#if UNITY_EDITOR
        /// <summary>Asset GUID of the target, for editor navigation. Not present in a build.</summary>
        public string TargetAssetGuid => targetAssetGuid ?? string.Empty;

        /// <summary>Local file id of the target, for editor navigation. Not present in a build.</summary>
        public long TargetLocalFileId => targetLocalFileId;

        /// <summary>Cached label for the inspector. Presentation only — never an identity.</summary>
        public string CachedDisplayName => cachedDisplayName ?? string.Empty;
#endif

        /// <summary>Creates a reference to an explicit scoped target.</summary>
        /// <param name="targetId">The target's id.</param>
        /// <param name="expectedRefType">The target's type category.</param>
        /// <param name="scopeKind">The scope the id is meaningful in.</param>
        /// <param name="scopeId">The authored scope id; ignored for the global kinds.</param>
        /// <param name="requiredness">How much the owner depends on this resolving.</param>
        /// <param name="availability">When the target is expected to be available.</param>
        public SceneObjectReferenceV2(
            string targetId,
            string expectedRefType,
            ReferenceScopeKind scopeKind = ReferenceScopeKind.LegacyGlobal,
            string scopeId = null,
            ReferenceRequiredness requiredness = ReferenceRequiredness.Optional,
            ReferenceAvailabilityPolicy availability = ReferenceAvailabilityPolicy.Deferred)
        {
            this.targetId = targetId ?? string.Empty;
            this.expectedRefType = expectedRefType ?? string.Empty;
            this.scopeKind = scopeKind;
            this.scopeId = IsGlobalKind(scopeKind) ? string.Empty : (scopeId ?? string.Empty);
            this.requiredness = requiredness;
            this.availability = availability;
#if UNITY_EDITOR
            this.targetAssetGuid = string.Empty;
            this.targetLocalFileId = 0;
            this.cachedDisplayName = string.Empty;
#endif
        }

        private static bool IsGlobalKind(ReferenceScopeKind kind) =>
            kind == ReferenceScopeKind.Global || kind == ReferenceScopeKind.LegacyGlobal;

        /// <summary>
        /// The v2 form of an existing v1 reference, preserving its exact semantics.
        /// </summary>
        /// <param name="legacy">The v1 reference to represent.</param>
        /// <remarks>
        /// Deliberately lossy in one direction only: the result is
        /// <see cref="ReferenceScopeKind.LegacyGlobal"/> and
        /// <see cref="ReferenceAvailabilityPolicy.Deferred"/>, because that is what v1 actually did —
        /// project-wide ids and an unconditional wait. Guessing a narrower scope here would change
        /// behavior during a mechanical conversion, so narrowing is left to the audit, which can see
        /// the whole project and can show its work.
        /// </remarks>
        public static SceneObjectReferenceV2 FromLegacy(SceneObjectReference legacy) =>
            new SceneObjectReferenceV2(
                legacy.RefId,
                legacy.RefType,
                ReferenceScopeKind.LegacyGlobal,
                null,
                ReferenceRequiredness.Optional,
                ReferenceAvailabilityPolicy.Deferred);

        /// <summary>
        /// The same reference re-homed into another scope, for migration.
        /// </summary>
        /// <param name="kind">The scope kind to move to.</param>
        /// <param name="newScopeId">The authored scope id; ignored for the global kinds.</param>
        public SceneObjectReferenceV2 WithScope(ReferenceScopeKind kind, string newScopeId = null)
        {
            var copy = this;
            copy.scopeKind = kind;
            copy.scopeId = IsGlobalKind(kind) ? string.Empty : (newScopeId ?? string.Empty);
            return copy;
        }

        /// <summary>The same reference with a different requiredness declaration.</summary>
        /// <param name="value">The requiredness to declare.</param>
        public SceneObjectReferenceV2 WithRequiredness(ReferenceRequiredness value)
        {
            var copy = this;
            copy.requiredness = value;
            return copy;
        }

        /// <summary>The same reference with a different availability policy.</summary>
        /// <param name="value">The availability policy to declare.</param>
        public SceneObjectReferenceV2 WithAvailability(ReferenceAvailabilityPolicy value)
        {
            var copy = this;
            copy.availability = value;
            return copy;
        }

        #region Key construction

        /// <summary>
        /// Builds the live runtime key this reference resolves against, given the component that
        /// owns it.
        /// </summary>
        /// <param name="context">
        /// The owning component, used to find the nearest <see cref="ReferenceScopeRoot"/> for a
        /// prefab-local reference. May be null for the global scopes, which need no context.
        /// </param>
        /// <param name="key">The live key, when this returns true.</param>
        /// <param name="failure">Why no key could be built, when this returns false.</param>
        /// <returns>True when a complete live key was produced.</returns>
        public bool TryBuildRuntimeKey(
            Component context, out ReferenceRuntimeKey key, out ReferenceResolveOutcome failure)
        {
            key = default;

            if (!IsAssigned)
            {
                failure = ReferenceResolveOutcome.NotAssigned;
                return false;
            }

            if (string.IsNullOrEmpty(expectedRefType))
            {
                failure = ReferenceResolveOutcome.InvalidSerializedData;
                return false;
            }

            switch (scopeKind)
            {
                case ReferenceScopeKind.LegacyGlobal:
                    key = ReferenceRuntimeKey.Legacy(expectedRefType, targetId);
                    break;

                case ReferenceScopeKind.Global:
                    key = ReferenceRuntimeKey.Global(expectedRefType, targetId);
                    break;

                case ReferenceScopeKind.Scene:
                    if (string.IsNullOrEmpty(scopeId))
                    {
                        failure = ReferenceResolveOutcome.InvalidSerializedData;
                        return false;
                    }

                    key = ReferenceRuntimeKey.Scene(scopeId, expectedRefType, targetId);
                    break;

                case ReferenceScopeKind.PrefabLocal:
                    // The authored scope id names the template; the live key needs this instance's.
                    var root = ReferenceScopeRoot.FindNearest(context);
                    if (root == null || string.IsNullOrEmpty(root.ScopeInstanceId))
                    {
                        failure = ReferenceResolveOutcome.WrongScope;
                        return false;
                    }

                    key = root.KeyFor(expectedRefType, targetId);
                    break;

                default:
                    failure = ReferenceResolveOutcome.InvalidSerializedData;
                    return false;
            }

            if (!key.IsValid)
            {
                failure = ReferenceResolveOutcome.InvalidSerializedData;
                return false;
            }

            failure = ReferenceResolveOutcome.ResolvedExact;
            return true;
        }

        #endregion

        #region Resolution

        /// <summary>
        /// Resolves this reference and reports exactly what happened, without logging or throwing.
        /// </summary>
        /// <typeparam name="T">The type the site expects.</typeparam>
        /// <param name="context">The owning component; required for a prefab-local reference.</param>
        /// <returns>The typed outcome, including the provider when one was found.</returns>
        public ReferenceResolveResult TryResolve<T>(Component context) where T : class, IReferenceable
        {
            if (!TryBuildRuntimeKey(context, out var key, out var keyFailure))
                return new ReferenceResolveResult(keyFailure, key, expectedType: typeof(T));

            var manager = RuntimeManager.GetSubsystem<ReferenceManager>();
            if (manager == null)
                return new ReferenceResolveResult(ReferenceResolveOutcome.RegistryUnavailable, key, expectedType: typeof(T));

            // Exact first, always. The compatibility path below can only ever run for data that
            // never declared a scope.
            if (manager.TryGet(key, out var provider))
                return Typed(key, provider, ReferenceResolveOutcome.ResolvedExact, typeof(T));

            if (!key.IsLegacy)
                return new ReferenceResolveResult(ReferenceResolveOutcome.ProviderMissing, key, expectedType: typeof(T));

            // v1 compatibility only: the stored RefType may be stale, so fall back to the id alone.
            // It refuses when more than one live provider carries the id.
            if (manager.TryGetByRefIdOnly(targetId, out provider))
                return Typed(key, provider, ReferenceResolveOutcome.ResolvedViaLegacyFallback, typeof(T));

            return new ReferenceResolveResult(ReferenceResolveOutcome.ProviderMissing, key, expectedType: typeof(T));
        }

        /// <summary>Applies the expected-type check to a found provider.</summary>
        private static ReferenceResolveResult Typed(
            ReferenceRuntimeKey key, IReferenceable provider, ReferenceResolveOutcome success, Type expected)
        {
            if (provider is null)
                return new ReferenceResolveResult(ReferenceResolveOutcome.ProviderMissing, key, expectedType: expected);

            var actual = provider.GetType();
            if (!expected.IsAssignableFrom(actual))
            {
                return new ReferenceResolveResult(
                    ReferenceResolveOutcome.WrongRuntimeType, key, provider, 1, expected, actual);
            }

            return new ReferenceResolveResult(success, key, provider, 1, expected, actual);
        }

        /// <summary>
        /// Resolves this reference, honoring <see cref="Requiredness"/>.
        /// </summary>
        /// <typeparam name="T">The type the site expects.</typeparam>
        /// <param name="context">The owning component; required for a prefab-local reference.</param>
        /// <param name="callerMember">Populated by the compiler. Do not supply.</param>
        /// <param name="callerFilePath">Populated by the compiler. Do not supply.</param>
        /// <param name="callerLine">Populated by the compiler. Do not supply.</param>
        /// <returns>The provider, or null when it did not resolve and the site is optional.</returns>
        /// <exception cref="ReferenceResolutionException">
        /// Thrown when <see cref="Requiredness"/> is <see cref="ReferenceRequiredness.Required"/> and
        /// the resolve failed.
        /// </exception>
        public T Resolve<T>(
            Component context,
            [CallerMemberName] string callerMember = null,
            [CallerFilePath] string callerFilePath = null,
            [CallerLineNumber] int callerLine = 0) where T : class, IReferenceable
        {
            var result = TryResolve<T>(context);
            Report(result, callerMember, callerFilePath, callerLine);
            return result.As<T>();
        }

        /// <summary>
        /// Resolves this reference, waiting until the target registers.
        /// </summary>
        /// <typeparam name="T">The type the site expects.</typeparam>
        /// <param name="context">The owning component; required for a prefab-local reference.</param>
        /// <param name="timeoutSeconds">Maximum time to wait for the target to register.</param>
        /// <param name="cancellationToken">Cancels the whole operation, including bootstrap.</param>
        /// <param name="callerMember">Populated by the compiler. Do not supply.</param>
        /// <param name="callerFilePath">Populated by the compiler. Do not supply.</param>
        /// <param name="callerLine">Populated by the compiler. Do not supply.</param>
        /// <returns>The terminal result of the operation.</returns>
        /// <remarks>
        /// Every attempt made while the target could still legitimately register is silent. Only the
        /// terminal outcome is reported, so one deferred resolve produces at most one diagnostic —
        /// the property that stops readers from learning to ignore the warning that mattered.
        /// </remarks>
        public async Awaitable<ReferenceResolveResult> ResolveAsync<T>(
            Component context,
            float timeoutSeconds = DefaultResolveTimeoutSeconds,
            CancellationToken cancellationToken = default,
            [CallerMemberName] string callerMember = null,
            [CallerFilePath] string callerFilePath = null,
            [CallerLineNumber] int callerLine = 0) where T : class, IReferenceable
        {
            // 1. Validate the serialized record before waiting on anything. An unassigned reference
            //    can never become assigned by waiting, and a malformed one never becomes well-formed.
            if (!IsAssigned)
            {
                var unassigned = new ReferenceResolveResult(
                    ReferenceResolveOutcome.NotAssigned, default, expectedType: typeof(T));
                Report(unassigned, callerMember, callerFilePath, callerLine);
                return unassigned;
            }

            // 2. Bootstrap wait, under the caller's token.
            try
            {
                await RuntimeManager.WaitForInitialization(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return new ReferenceResolveResult(
                    ReferenceResolveOutcome.Cancelled, default, expectedType: typeof(T));
            }

            var manager = RuntimeManager.GetSubsystem<ReferenceManager>();

            // A struct's `this` cannot be captured by a lambda, so the matcher works against a copy.
            // Safe: the serialized identity never changes mid-resolve.
            var self = this;
            var owner = context;

            // 3. Attempt exact resolution.
            var result = self.TryResolve<T>(owner);
            bool waited = false;

            if (manager != null && !result.IsResolved && result.IsPending)
            {
                var completion = new AwaitableCompletionSource<bool>();

                // Re-probe the full contract on every arrival rather than completing on the key
                // alone, so a wrong-type registration cannot end a wait that a later, correct one
                // would have satisfied.
                Action<ReferenceRuntimeKey, IReferenceable> onRegistered = (_, __) =>
                {
                    if (self.TryResolve<T>(owner).IsResolved)
                        completion.TrySetResult(true);
                };

                // 4/5. Subscribe, then re-probe, closing the window between the first attempt and
                //      the subscription.
                manager.KeyRegistered += onRegistered;
                try
                {
                    result = self.TryResolve<T>(owner);
                    if (!result.IsResolved && result.IsPending)
                    {
                        waited = true;
                        try
                        {
                            // 6. Await the timeout or cancellation.
                            await RuntimeManager.AwaitWithTimeout(
                                completion.Awaitable, timeoutSeconds, cancellationToken,
                                $"SceneObjectReferenceV2.ResolveAsync({result.RequestedKey})");
                        }
                        catch (TimeoutException)
                        {
                            // Fall through to the final attempt, which produces this operation's
                            // single diagnostic.
                        }
                        catch (OperationCanceledException)
                        {
                            var cancelled = new ReferenceResolveResult(
                                ReferenceResolveOutcome.Cancelled, result.RequestedKey, expectedType: typeof(T));
                            manager.Diagnostics.Record(
                                ReferenceDiagnosticKind.Cancelled, cancelled.RequestedKey);
                            return cancelled;
                        }
                    }
                }
                finally
                {
                    // 7. Unsubscribe unconditionally.
                    manager.KeyRegistered -= onRegistered;
                }

                // 8. One final attempt, whose outcome is the operation's.
                result = self.TryResolve<T>(owner);

                if (result.IsPending && waited)
                {
                    result = new ReferenceResolveResult(
                        ReferenceResolveOutcome.TimedOut, result.RequestedKey, expectedType: typeof(T));
                }
                else if (result.IsResolved && waited)
                {
                    manager.Diagnostics.Record(ReferenceDiagnosticKind.LateSuccess, result.RequestedKey);
                }
            }

            // 9. Report once, and throw only per the documented requiredness contract.
            Report(result, callerMember, callerFilePath, callerLine);
            return result;
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Emits this operation's single diagnostic and applies the requiredness contract.
        /// </summary>
        /// <remarks>
        /// An optional reference that did not resolve is silent by design. Logging it was v1's
        /// behavior and it trained everyone to filter the category out, which then hid the ambiguous
        /// and wrong-type cases that are always defects.
        /// </remarks>
        private void Report(
            ReferenceResolveResult result, string callerMember, string callerFilePath, int callerLine)
        {
            var manager = RuntimeManager.GetSubsystem<ReferenceManager>();

            switch (result.Outcome)
            {
                case ReferenceResolveOutcome.ResolvedExact:
                    manager?.Diagnostics.Record(ReferenceDiagnosticKind.ResolvedExact, result.RequestedKey);
                    return;

                case ReferenceResolveOutcome.ResolvedViaLegacyFallback:
                case ReferenceResolveOutcome.WrongRefType:
                    manager?.Diagnostics.Record(
                        ReferenceDiagnosticKind.ResolvedViaLegacyFallback, result.RequestedKey, null, result.Summary);
                    return;

                case ReferenceResolveOutcome.AmbiguousFallback:
                    manager?.Diagnostics.Record(
                        ReferenceDiagnosticKind.AmbiguousFallback, result.RequestedKey, null, result.Summary);
                    break;

                case ReferenceResolveOutcome.WrongRuntimeType:
                case ReferenceResolveOutcome.WrongScope:
                    manager?.Diagnostics.Record(
                        ReferenceDiagnosticKind.WrongTypeOrScope, result.RequestedKey, null, result.Summary);
                    break;

                case ReferenceResolveOutcome.TimedOut:
                    manager?.Diagnostics.Record(ReferenceDiagnosticKind.TimedOut, result.RequestedKey);
                    break;
            }

            bool mustResolve =
                requiredness == ReferenceRequiredness.Required ||
                requiredness == ReferenceRequiredness.DeferredRequired;

            if (!mustResolve)
            {
                // A wrong-type or ambiguous result is a defect even on an optional site: the author
                // wired something, and what they wired cannot work.
                if (result.Outcome == ReferenceResolveOutcome.WrongRuntimeType ||
                    result.Outcome == ReferenceResolveOutcome.AmbiguousFallback)
                {
                    Debug.LogError($"[SceneObjectReferenceV2] {result.Summary}.{FormatCallSite(callerMember, callerFilePath, callerLine)}");
                }

                return;
            }

            throw new ReferenceResolutionException(
                $"[SceneObjectReferenceV2] Required resolve of '{result.RequestedKey}' failed: {result.Summary}.{FormatCallSite(callerMember, callerFilePath, callerLine)}",
                targetId, expectedRefType, callerMember, callerFilePath, callerLine);
        }

        /// <summary>Formats the captured synchronous call site for a log message.</summary>
        private static string FormatCallSite(string callerMember, string callerFilePath, int callerLine)
        {
            if (string.IsNullOrEmpty(callerMember) && string.IsNullOrEmpty(callerFilePath))
                return string.Empty;

            string file = string.IsNullOrEmpty(callerFilePath)
                ? "<unknown>"
                : System.IO.Path.GetFileName(callerFilePath);
            return $"\n  called from {callerMember} at {file}:{callerLine}";
        }

        #endregion

        /// <inheritdoc/>
        public override string ToString()
        {
            if (!IsAssigned)
                return "SceneObjectReferenceV2(unassigned)";

            return IsGlobalKind(scopeKind)
                ? $"{scopeKind}|{expectedRefType}:{targetId}"
                : $"{scopeKind}/{scopeId}|{expectedRefType}:{targetId}";
        }
    }
}
