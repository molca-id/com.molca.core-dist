using System;
using System.Threading;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Type-safe variant of <see cref="SceneObjectReference"/> that constrains the referenced object
    /// to <typeparamref name="T"/>. The Inspector picker will only show objects of that type.
    /// </summary>
    /// <remarks>
    /// Serializes identically to <see cref="SceneObjectReference"/> and can be implicitly converted to it.
    /// Use <see cref="Resolve"/> instead of <c>Resolve&lt;T&gt;</c> — the type parameter is already known.
    /// </remarks>
    /// <typeparam name="T">The expected <see cref="IReferenceable"/> type.</typeparam>
    [System.Serializable]
    public struct SceneObjectReference<T> where T : class, IReferenceable
    {
        [SerializeField] private string refId;
        [SerializeField] private string refType;

#if UNITY_EDITOR
        [SerializeField] private string sceneGuid;
        [SerializeField] private string cachedDisplayName;
#endif

        /// <summary>The stored reference ID.</summary>
        public string RefId => refId;

        /// <summary>The stored reference type string.</summary>
        public string RefType => refType;

        /// <summary>True when a reference ID has been assigned.</summary>
        public bool IsValid => !string.IsNullOrEmpty(refId);

        /// <summary>
        /// Resolves the reference to a <typeparamref name="T"/> instance synchronously.
        /// </summary>
        /// <returns>The resolved object, or <c>null</c> if not found or not loaded.</returns>
        public T Resolve() => ((SceneObjectReference)this).Resolve<T>();

        /// <summary>
        /// Resolves the reference to a <typeparamref name="T"/> instance synchronously.
        /// </summary>
        /// <param name="required">
        /// When true, a failure throws <see cref="ReferenceResolutionException"/> instead of
        /// returning null. Mirrors <c>[Inject(Required = true)]</c>.
        /// </param>
        /// <returns>The resolved object; or null when not found and <paramref name="required"/> is false.</returns>
        /// <exception cref="ReferenceResolutionException">Thrown when <paramref name="required"/> is true and the resolve fails.</exception>
        public T Resolve(bool required) => ((SceneObjectReference)this).Resolve<T>(required);

        /// <summary>
        /// Attempts to resolve the reference to a <typeparamref name="T"/> instance synchronously.
        /// </summary>
        /// <param name="resolved">The resolved object, or <c>null</c> if not found.</param>
        /// <returns>True if the object was found and matched the type.</returns>
        public bool TryResolve(out T resolved) => ((SceneObjectReference)this).TryResolve(out resolved);

        /// <summary>
        /// Resolves the reference asynchronously, waiting until the target registers
        /// (bounded by <paramref name="timeoutSeconds"/> and <paramref name="cancellationToken"/>).
        /// </summary>
        /// <param name="required">When true, throws <see cref="ReferenceResolutionException"/> instead of returning null on failure.</param>
        /// <param name="timeoutSeconds">Maximum time to wait for the target to register.</param>
        /// <param name="cancellationToken">Cancels the wait; throws <see cref="OperationCanceledException"/> when cancelled.</param>
        public async Awaitable<T> ResolveAsync(
            bool required = false,
            float timeoutSeconds = SceneObjectReference.DefaultResolveTimeoutSeconds,
            CancellationToken cancellationToken = default)
            => await ((SceneObjectReference)this).ResolveAsync<T>(required, timeoutSeconds, cancellationToken);

        /// <summary>
        /// Implicit conversion to the non-generic <see cref="SceneObjectReference"/>.
        /// Useful when passing to APIs that accept the untyped version.
        /// </summary>
        public static implicit operator SceneObjectReference(SceneObjectReference<T> src)
            => new SceneObjectReference(src.refId, src.refType);
    }
}
