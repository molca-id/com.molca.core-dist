using System;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>What kind of asset owns a reference site.</summary>
    public enum ReferenceSiteSourceKind
    {
        /// <summary>A component in a scene.</summary>
        Scene = 0,

        /// <summary>A component inside a prefab asset.</summary>
        PrefabAsset = 1,

        /// <summary>
        /// A ScriptableObject asset. An SO cannot be a runtime <i>target</i>, but it may absolutely own
        /// an outbound reference that resolves a loaded scene object — the case earlier tooling skipped.
        /// </summary>
        ScriptableObjectAsset = 2,

        /// <summary>Supplied by a <see cref="MolcaReferenceIndexContributor"/>.</summary>
        Contributed = 3,
    }

    /// <summary>
    /// One discovered serialized field that requests a provider: which object owns it, which property
    /// it is, what it stores, and what type it expects back.
    /// </summary>
    /// <remarks>
    /// <see cref="SiteKey"/> is asset GUID plus local file id plus serialized property path, which is
    /// stable enough to carry a selection across a rescan and to serve as a repair precondition.
    /// </remarks>
    public sealed class ReferenceSiteRecord
    {
        /// <summary>Stable key for this site within a snapshot.</summary>
        public string SiteKey { get; }

        /// <summary>Editor address of the object declaring the field.</summary>
        public ReferenceObjectLocator OwnerLocator { get; }

        /// <summary>Serialized property path of the reference field, e.g. <c>targets.Array.data[2]</c>.</summary>
        public string PropertyPath { get; }

        /// <summary>The serialized <c>refId</c>. Empty when the reference is unset.</summary>
        public string StoredRefId { get; }

        /// <summary>The serialized <c>refType</c>.</summary>
        public string StoredRefType { get; }

        /// <summary>What kind of asset owns this site.</summary>
        public ReferenceSiteSourceKind SourceKind { get; }

        /// <summary>
        /// The type the site expects back — the <c>T</c> of a <c>SceneObjectReference&lt;T&gt;</c> field.
        /// Null for the untyped <c>SceneObjectReference</c>, which imposes no compile-time expectation.
        /// </summary>
        public string ExpectedRuntimeTypeName { get; }

        /// <summary>True when the owning asset is in a package or otherwise non-writable.</summary>
        public bool IsReadOnly { get; }

        /// <summary>True when a reference id is stored.</summary>
        public bool IsAssigned => !string.IsNullOrEmpty(StoredRefId);

        /// <summary>The space the stored id is meaningful in.</summary>
        /// <remarks>
        /// <see cref="ReferenceScopeKind.LegacyGlobal"/> for a v1 field, which declared no scope. That is
        /// the truthful reading, not a default: v1 ids really were required to be unique project-wide.
        /// </remarks>
        public ReferenceScopeKind ScopeKind { get; }

        /// <summary>The authored scope id: a scene path, or a prefab's scope template. Empty when global.</summary>
        public string ScopeId { get; }

        /// <summary>How much the owner depends on this reference resolving.</summary>
        public ReferenceRequiredness Requiredness { get; }

        /// <summary>When the target is expected to be available.</summary>
        public ReferenceAvailabilityPolicy Availability { get; }

        /// <summary>
        /// Scope template id of the nearest enclosing <c>ReferenceScopeRoot</c>, or empty when the owner
        /// is not inside one.
        /// </summary>
        /// <remarks>
        /// Recorded by the scanner because the analyzer is pure and cannot walk a hierarchy. It is what
        /// lets a prefab-local reference with no scope root be reported as the authoring mistake it is,
        /// rather than as a mysterious runtime registration refusal.
        /// </remarks>
        public string ScopeRootId { get; }

        /// <summary>True when this site declared a scope, i.e. it is a v2 field.</summary>
        public bool IsScoped => ScopeKind != ReferenceScopeKind.LegacyGlobal;

        /// <summary>
        /// True when leaving this reference unset is a defect rather than a legal authoring choice.
        /// </summary>
        public bool RequiresTarget =>
            Requiredness == ReferenceRequiredness.Required ||
            Requiredness == ReferenceRequiredness.DeferredRequired;

        /// <summary>
        /// Resolved expected type when its assembly is loaded; used for the assignability check.
        /// </summary>
        internal Type ExpectedRuntimeType { get; }

        /// <summary>
        /// Describes a reference site.
        /// </summary>
        /// <param name="ownerLocator">Editor address of the object declaring the field.</param>
        /// <param name="propertyPath">Serialized property path of the reference field.</param>
        /// <param name="storedRefId">The serialized Ref Id. Empty means unset.</param>
        /// <param name="storedRefType">The serialized Ref Type.</param>
        /// <param name="expectedRuntimeType">
        /// The type the field promises, or null when it promises nothing (the untyped struct).
        /// </param>
        /// <param name="sourceKind">Which asset category owns the site.</param>
        /// <param name="isReadOnly">Whether the owning asset is non-writable.</param>
        /// <param name="scopeKind">
        /// The space the stored id is meaningful in. Defaults to
        /// <see cref="ReferenceScopeKind.LegacyGlobal"/>, which is what a v1 field means.
        /// </param>
        /// <param name="scopeId">The authored scope id; ignored for the global kinds.</param>
        /// <param name="requiredness">How much the owner depends on this resolving.</param>
        /// <param name="availability">When the target is expected to be available.</param>
        /// <param name="scopeRootId">
        /// Scope template id of the nearest enclosing scope root, or empty. The analyzer cannot walk a
        /// hierarchy, so whoever discovered the site has to report this.
        /// </param>
        /// <remarks>
        /// Public so a <see cref="MolcaReferenceIndexContributor"/> in another package can describe sites
        /// Core's scanner cannot reach, and so pure analysis can be unit-tested without loading a scene.
        /// The scoped parameters are optional so every existing caller keeps compiling and keeps meaning
        /// exactly what it meant.
        /// </remarks>
        public ReferenceSiteRecord(
            ReferenceObjectLocator ownerLocator,
            string propertyPath,
            string storedRefId,
            string storedRefType,
            Type expectedRuntimeType,
            ReferenceSiteSourceKind sourceKind,
            bool isReadOnly = false,
            ReferenceScopeKind scopeKind = ReferenceScopeKind.LegacyGlobal,
            string scopeId = null,
            ReferenceRequiredness requiredness = ReferenceRequiredness.Optional,
            ReferenceAvailabilityPolicy availability = ReferenceAvailabilityPolicy.Deferred,
            string scopeRootId = null)
        {
            ScopeKind = scopeKind;
            ScopeId = scopeKind == ReferenceScopeKind.Global || scopeKind == ReferenceScopeKind.LegacyGlobal
                ? string.Empty
                : scopeId ?? string.Empty;
            Requiredness = requiredness;
            Availability = availability;
            ScopeRootId = scopeRootId ?? string.Empty;

            OwnerLocator = ownerLocator;
            PropertyPath = propertyPath ?? string.Empty;
            StoredRefId = storedRefId ?? string.Empty;
            StoredRefType = storedRefType ?? string.Empty;
            ExpectedRuntimeType = expectedRuntimeType;
            ExpectedRuntimeTypeName = expectedRuntimeType?.FullName ?? string.Empty;
            SourceKind = sourceKind;
            IsReadOnly = isReadOnly;
            SiteKey = $"{ownerLocator.Key}|{PropertyPath}";
        }

        /// <summary>Human-readable "owner.property" description for a finding message.</summary>
        public string Describe() => $"{OwnerLocator} → {PropertyPath}";

        /// <inheritdoc/>
        public override string ToString() => Describe();
    }
}
