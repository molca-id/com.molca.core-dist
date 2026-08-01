using System;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>Where a discovered reference provider lives.</summary>
    public enum ReferenceProviderKind
    {
        /// <summary>An <c>IReferenceable</c> component in a scene — the only kind resolvable at runtime.</summary>
        SceneComponent = 0,

        /// <summary>An <c>IReferenceable</c> component inside a prefab asset (a template, not a live target).</summary>
        PrefabComponent = 1,

        /// <summary>
        /// A ScriptableObject asset implementing <c>IReferenceable</c>. Data identity only: it is never
        /// registered with the runtime <c>ReferenceManager</c> (the "SOs-out" boundary).
        /// </summary>
        ScriptableObjectAsset = 2,

        /// <summary>Supplied by a <see cref="MolcaReferenceIndexContributor"/> from another package.</summary>
        Contributed = 3,
    }

    /// <summary>
    /// One discovered target that could satisfy a reference: its identity, its type, and where it lives.
    /// </summary>
    /// <remarks>
    /// A provider record is immutable derived data. It holds no live <see cref="UnityEngine.Object"/>,
    /// so a snapshot survives a scene close or domain reload and can be compared against a later scan
    /// to detect staleness.
    /// </remarks>
    public sealed class ReferenceProviderRecord
    {
        /// <summary>Stable key for this provider within a snapshot: locator plus reference identity.</summary>
        public string ProviderKey { get; }

        /// <summary>Where this provider lives.</summary>
        public ReferenceProviderKind Kind { get; }

        /// <summary>The provider's <c>IReferenceable.RefId</c>. Empty when the provider has no id yet.</summary>
        public string RefId { get; }

        /// <summary>The provider's <c>IReferenceable.RefType</c>.</summary>
        public string RefType { get; }

        /// <summary>The provider's <c>IReferenceable.DisplayName</c>. Presentation only, never identity.</summary>
        public string DisplayName { get; }

        /// <summary>Assembly-qualified name of the provider's concrete runtime type.</summary>
        public string RuntimeTypeName { get; }

        /// <summary>Editor address of the providing object.</summary>
        public ReferenceObjectLocator Locator { get; }

        /// <summary>True when the provider lives in a package or otherwise non-writable asset.</summary>
        public bool IsReadOnly { get; }

        /// <summary>
        /// The runtime type of the provider, when its assembly is still loaded. Used for the
        /// assignability check in <see cref="ReferenceResolutionAnalyzer"/>; null after a script
        /// rename, which the analyzer reports as unsupported rather than as a type mismatch.
        /// </summary>
        internal Type RuntimeType { get; }

        /// <summary>True when this provider is registered with the runtime registry at play time.</summary>
        public bool IsRuntimeResolvable => Kind == ReferenceProviderKind.SceneComponent
                                        || Kind == ReferenceProviderKind.Contributed;

        /// <summary>
        /// Describes a provider.
        /// </summary>
        /// <param name="kind">Where the provider lives.</param>
        /// <param name="refId">The provider's Ref Id. Empty is legal and reported as <c>REF008</c>.</param>
        /// <param name="refType">The provider's Ref Type.</param>
        /// <param name="displayName">Presentation name. Never used as identity.</param>
        /// <param name="runtimeType">The provider's concrete type, for the assignability check.</param>
        /// <param name="locator">Editor address of the providing object.</param>
        /// <param name="isReadOnly">Whether the owning asset is non-writable.</param>
        /// <remarks>
        /// Public so a <see cref="MolcaReferenceIndexContributor"/> in another package can describe its own
        /// provider kinds, and so pure analysis can be unit-tested without loading a scene.
        /// </remarks>
        public ReferenceProviderRecord(
            ReferenceProviderKind kind,
            string refId,
            string refType,
            string displayName,
            Type runtimeType,
            ReferenceObjectLocator locator,
            bool isReadOnly = false)
        {
            Kind = kind;
            RefId = refId ?? string.Empty;
            RefType = refType ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            RuntimeType = runtimeType;
            RuntimeTypeName = runtimeType?.FullName ?? string.Empty;
            Locator = locator;
            IsReadOnly = isReadOnly;
            ProviderKey = $"{locator.Key}|{RefType}|{RefId}";
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"{DisplayName} ({RefType}:{RefId}) @ {Locator}";
    }
}
