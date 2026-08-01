using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// A serializable struct that holds the ID of an IReferenceable object
    /// that exists in a scene.
    /// </summary>
    [System.Serializable]
    public struct SceneObjectReference
    {
        [SerializeField]
        private string refId;

        [SerializeField]
        private string refType;
        
#if UNITY_EDITOR
        [SerializeField]
        private string sceneGuid;
        
        [SerializeField]
        private string cachedDisplayName;
#endif

        internal SceneObjectReference(string refId, string refType)
        {
            this.refId = refId;
            this.refType = refType;
#if UNITY_EDITOR
            this.sceneGuid = string.Empty;
            this.cachedDisplayName = string.Empty;
#endif
        }

        // --- Public Properties ---
        public string RefId => refId;
        public string RefType => refType;
        public bool IsValid => !string.IsNullOrEmpty(refId);
        
#if UNITY_EDITOR
        /// <summary>
        /// The GUID of the scene this reference belongs to. (Editor-only helper)
        /// </summary>
        public string SceneGuid => sceneGuid;
        
        /// <summary>
        /// The cached display name for use in the editor.
        /// </summary>
        public string CachedDisplayName => cachedDisplayName;
#endif

        /// <summary>Default bound (seconds) for <see cref="ResolveAsync{T}(bool,float,CancellationToken,string,string,int)"/>'s await-until-registered wait.</summary>
        public const float DefaultResolveTimeoutSeconds = 5f;

        /// <summary>
        /// Why a resolve attempt failed. Used internally to shape the diagnostic
        /// (and the <see cref="ReferenceResolutionException"/> message in required mode).
        /// </summary>
        private enum ResolveFailure
        {
            None,
            ManagerUnavailable,
            NotAssigned,
            NotRegistered,
            WrongType,
        }

        /// <summary>
        /// Resolves this reference to the actual scene object instance asynchronously,
        /// waiting until the target registers (bounded by <paramref name="timeoutSeconds"/>
        /// and <paramref name="cancellationToken"/>) rather than relying on a one-frame race.
        /// </summary>
        /// <typeparam name="T">The type (which must be IReferenceable) you expect to get.</typeparam>
        /// <param name="required">When true, throws <see cref="ReferenceResolutionException"/> instead of returning null on failure.</param>
        /// <param name="timeoutSeconds">Maximum time to wait for the target to register.</param>
        /// <param name="cancellationToken">Cancels the wait; throws <see cref="OperationCanceledException"/> when cancelled.</param>
        /// <param name="callerMember">Populated by the compiler with the calling member name. Do not supply.</param>
        /// <param name="callerFilePath">Populated by the compiler with the calling file path. Do not supply.</param>
        /// <param name="callerLine">Populated by the compiler with the calling line number. Do not supply.</param>
        /// <returns>The found object, or null if not found / wrong type and <paramref name="required"/> is false.</returns>
        /// <remarks>
        /// <para>The caller-info parameters are captured at the synchronous call site so that
        /// resolve warnings/errors name who initiated the resolve — the runtime call stack
        /// loses this across the <c>await</c> boundaries below.</para>
        ///
        /// <para>Every attempt made while the target may still legitimately register is
        /// <b>silent</b>. Only the final attempt logs, so one deferred resolve produces at most
        /// one diagnostic describing its terminal outcome. Previously the pre-wait probe logged
        /// "could not resolve" for the entirely expected case of a target that had not registered
        /// yet, which trained readers to ignore the warning that mattered.</para>
        ///
        /// <para><paramref name="cancellationToken"/> covers the <i>whole</i> operation, including
        /// the wait for <see cref="RuntimeManager"/> bootstrap. A caller passing
        /// <c>destroyCancellationToken</c> therefore stops waiting when its owner is destroyed even
        /// if bootstrap never completes.</para>
        /// </remarks>
        public async Awaitable<T> ResolveAsync<T>(
            bool required = false,
            float timeoutSeconds = DefaultResolveTimeoutSeconds,
            CancellationToken cancellationToken = default,
            [CallerMemberName] string callerMember = null,
            [CallerFilePath] string callerFilePath = null,
            [CallerLineNumber] int callerLine = 0) where T : class, IReferenceable
        {
            // 1. Validate the serialized record before waiting on anything: an unassigned
            //    reference can never become assigned by waiting.
            if (!IsValid)
            {
                if (required)
                    throw BuildException(ResolveFailure.NotAssigned, callerMember, callerFilePath, callerLine);
                return null;
            }

            // 2. Bootstrap wait, under the caller's token.
            await RuntimeManager.WaitForInitialization(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var manager = RuntimeManager.GetSubsystem<ReferenceManager>();

            // A struct's `this` cannot be captured by a lambda, so the registration matcher works
            // against a copy. The copy is safe: the serialized identity never changes mid-resolve.
            var self = this;

            if (manager != null && !self.TryResolveCore<T>(out _, out _, null, null, 0, logDiagnostics: false))
            {
                var capturedId = refId;
                var completion = new AwaitableCompletionSource<bool>();

                // Wake on any registration carrying the requested id, then re-probe the full
                // key/type contract. Completing on the id alone would finish the wait on a
                // wrong-type or wrong-Ref-Type registration and hand the caller a failure that a
                // later, correct registration would have satisfied.
                Action<IReferenceable> onRegistered = obj =>
                {
                    if (obj == null || obj.RefId != capturedId)
                        return;
                    if (self.TryResolveCore<T>(out _, out _, null, null, 0, logDiagnostics: false))
                        completion.TrySetResult(true);
                };

                manager.Registered += onRegistered;
                try
                {
                    // Re-probe after subscribing to close the window between the first attempt
                    // and the subscription.
                    if (!self.TryResolveCore<T>(out _, out _, null, null, 0, logDiagnostics: false))
                    {
                        try
                        {
                            await RuntimeManager.AwaitWithTimeout(
                                completion.Awaitable, timeoutSeconds, cancellationToken,
                                $"SceneObjectReference.ResolveAsync({refType}:{refId})");
                        }
                        catch (TimeoutException)
                        {
                            // Fall through to the final attempt, which produces the single
                            // diagnostic for this operation (and throws if required).
                        }
                    }
                }
                finally
                {
                    manager.Registered -= onRegistered;
                }
            }

            // 3. Final attempt: the only one that logs, and the one that honors `required`.
            return Resolve<T>(required, callerMember, callerFilePath, callerLine);
        }

        /// <summary>
        /// Resolves this reference to the actual scene object instance. Returns null on any failure.
        /// </summary>
        /// <typeparam name="T">The type (which must be IReferenceable) you expect to get.</typeparam>
        /// <param name="callerMember">Populated by the compiler with the calling member name. Do not supply.</param>
        /// <param name="callerFilePath">Populated by the compiler with the calling file path. Do not supply.</param>
        /// <param name="callerLine">Populated by the compiler with the calling line number. Do not supply.</param>
        /// <returns>The found object, or null if not found or of the wrong type.</returns>
        public T Resolve<T>(
            [CallerMemberName] string callerMember = null,
            [CallerFilePath] string callerFilePath = null,
            [CallerLineNumber] int callerLine = 0) where T : class, IReferenceable
        {
            return TryResolveCore<T>(out T resolved, out _, callerMember, callerFilePath, callerLine) ? resolved : null;
        }

        /// <summary>
        /// Resolves this reference to the actual scene object instance.
        /// </summary>
        /// <typeparam name="T">The type (which must be IReferenceable) you expect to get.</typeparam>
        /// <param name="required">
        /// When true, a failure throws <see cref="ReferenceResolutionException"/> (carrying the
        /// captured call site) instead of returning null. Mirrors <c>[Inject(Required = true)]</c>.
        /// </param>
        /// <param name="callerMember">Populated by the compiler with the calling member name. Do not supply.</param>
        /// <param name="callerFilePath">Populated by the compiler with the calling file path. Do not supply.</param>
        /// <param name="callerLine">Populated by the compiler with the calling line number. Do not supply.</param>
        /// <returns>The found object; or null when not found and <paramref name="required"/> is false.</returns>
        /// <exception cref="ReferenceResolutionException">Thrown when <paramref name="required"/> is true and the resolve fails.</exception>
        public T Resolve<T>(
            bool required,
            [CallerMemberName] string callerMember = null,
            [CallerFilePath] string callerFilePath = null,
            [CallerLineNumber] int callerLine = 0) where T : class, IReferenceable
        {
            if (TryResolveCore<T>(out T resolved, out var failure, callerMember, callerFilePath, callerLine))
                return resolved;

            if (required)
                throw BuildException(failure, callerMember, callerFilePath, callerLine);

            return null;
        }

        /// <summary>
        /// Attempts to resolve this reference to the actual scene object instance.
        /// </summary>
        /// <typeparam name="T">The type (which must be IReferenceable) you expect to get.</typeparam>
        /// <param name="resolvedObject">The found object, or null if not found.</param>
        /// <param name="callerMember">Populated by the compiler with the calling member name. Do not supply.</param>
        /// <param name="callerFilePath">Populated by the compiler with the calling file path. Do not supply.</param>
        /// <param name="callerLine">Populated by the compiler with the calling line number. Do not supply.</param>
        /// <returns>True if the object was found and matched the type, otherwise false.</returns>
        public bool TryResolve<T>(
            out T resolvedObject,
            [CallerMemberName] string callerMember = null,
            [CallerFilePath] string callerFilePath = null,
            [CallerLineNumber] int callerLine = 0) where T : class, IReferenceable
        {
            return TryResolveCore(out resolvedObject, out _, callerMember, callerFilePath, callerLine);
        }

        /// <summary>
        /// Core resolve path shared by every public entry point. Reports the failure category via
        /// <paramref name="failure"/> and, unless <paramref name="logDiagnostics"/> is false, emits
        /// the matching diagnostic.
        /// </summary>
        /// <param name="logDiagnostics">
        /// False for the probing attempts inside <see cref="ResolveAsync{T}"/>, where a miss is an
        /// expected intermediate state rather than a problem worth reporting. Every synchronous
        /// public entry point passes true, so their behavior is unchanged.
        /// </param>
        private bool TryResolveCore<T>(
            out T resolvedObject,
            out ResolveFailure failure,
            string callerMember,
            string callerFilePath,
            int callerLine,
            bool logDiagnostics = true) where T : class, IReferenceable
        {
            resolvedObject = null;
            failure = ResolveFailure.None;

            // 1. Check if the manager exists
            var manager = RuntimeManager.GetSubsystem<ReferenceManager>();
            if (manager == null)
            {
                failure = ResolveFailure.ManagerUnavailable;
                if (logDiagnostics)
                    Debug.LogWarning($"[SceneObjectReference] ReferenceManager is not available. Cannot resolve '{refId}'.{FormatCallSite(callerMember, callerFilePath, callerLine)}");
                return false;
            }

            // 2. Check if this reference is even set. An unset reference is a quiet
            //    (false) case when optional; the required path turns it into a throw.
            if (!IsValid)
            {
                failure = ResolveFailure.NotAssigned;
                return false;
            }

            // 3. Try to get the object from the manager (type + id)
            if (!manager.TryGet(refType, refId, out var obj) &&
                !manager.TryGetByRefIdOnly(refId, out obj))
            {
                failure = ResolveFailure.NotRegistered;
                if (logDiagnostics)
                    Debug.LogWarning($"[SceneObjectReference] Could not resolve reference: {refType} | {refId}. Object may not be registered or scene not loaded.{FormatCallSite(callerMember, callerFilePath, callerLine)}");
                return false;
            }

            // 4. The manager's lookups already reject and purge destroyed entries, so reaching here
            //    with a fake-null object would mean a registry invariant broke. Keep the guard as a
            //    belt-and-braces check rather than trusting the layer below unconditionally.
            if (obj is UnityEngine.Object uo && uo == null)
            {
                manager.PurgeIfDestroyed(obj);
                failure = ResolveFailure.NotRegistered;
                if (logDiagnostics)
                    Debug.LogWarning($"[SceneObjectReference] Reference '{refType} | {refId}' resolved to a destroyed object; purged the dead entry. Treating as not found.{FormatCallSite(callerMember, callerFilePath, callerLine)}");
                return false;
            }

            if (logDiagnostics && !string.IsNullOrEmpty(refType) && obj.RefType != refType)
            {
                Debug.LogWarning($"[SceneObjectReference] Resolved '{refId}' by id; serialized refType '{refType}' does not match current '{obj.RefType}'. Re-assign the reference in the inspector to update serialization.{FormatCallSite(callerMember, callerFilePath, callerLine)}");
            }

            if (obj is T typedObj)
            {
                resolvedObject = typedObj;
                return true;
            }

            failure = ResolveFailure.WrongType;
            if (logDiagnostics)
                Debug.LogError($"[SceneObjectReference] Resolved object for ID '{refId}' is of type '{obj.GetType().Name}' but was asked for type '{typeof(T).Name}'.{FormatCallSite(callerMember, callerFilePath, callerLine)}");
            return false;
        }

        /// <summary>
        /// Builds the <see cref="ReferenceResolutionException"/> for a failed required resolve,
        /// with a message specific to the failure category and the captured call site.
        /// </summary>
        private ReferenceResolutionException BuildException(
            ResolveFailure failure, string callerMember, string callerFilePath, int callerLine)
        {
            string reason = failure switch
            {
                ResolveFailure.NotAssigned => "reference was never assigned",
                ResolveFailure.ManagerUnavailable => "ReferenceManager is not available",
                ResolveFailure.WrongType => "resolved object was of the wrong type",
                _ => "object is not registered (scene not loaded, or destroyed)",
            };

            return new ReferenceResolutionException(
                $"[SceneObjectReference] Required resolve of '{refType} | {refId}' failed: {reason}.{FormatCallSite(callerMember, callerFilePath, callerLine)}",
                refId, refType, callerMember, callerFilePath, callerLine);
        }

        /// <summary>
        /// Formats the captured synchronous call site for appending to a resolve log message.
        /// </summary>
        private static string FormatCallSite(string callerMember, string callerFilePath, int callerLine)
        {
            if (string.IsNullOrEmpty(callerMember) && string.IsNullOrEmpty(callerFilePath))
                return string.Empty;

            string file = string.IsNullOrEmpty(callerFilePath)
                ? "<unknown>"
                : System.IO.Path.GetFileName(callerFilePath);
            return $"\n  called from {callerMember} at {file}:{callerLine}";
        }
    }
}