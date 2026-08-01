using System;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// The full identity of a reference target: a scope, plus the <c>(RefType, RefId)</c> pair that
    /// must be unique <i>within</i> that scope.
    /// </summary>
    /// <remarks>
    /// This is the authoritative key of the runtime registry. V1's <see cref="ReferenceId"/> is the
    /// same pair with the scope left implicit and global, which is exactly why two placements of one
    /// referenceable prefab could not coexist. A <see cref="ReferenceScopeKind.LegacyGlobal"/> key
    /// carries that v1 meaning explicitly, so old data keeps working while new data can say what it
    /// actually means.
    ///
    /// The key is a value: it is captured at registration time and never re-read from the provider.
    /// A provider whose <see cref="IReferenceable.RefId"/> is mutated after registering therefore
    /// cannot desynchronise the registry from the entry it actually holds.
    /// </remarks>
    [Serializable]
    public readonly struct ReferenceRuntimeKey : IEquatable<ReferenceRuntimeKey>
    {
        [SerializeField] private readonly ReferenceScopeKind scopeKind;
        [SerializeField] private readonly string scopeId;
        [SerializeField] private readonly string refType;
        [SerializeField] private readonly string refId;

        /// <summary>The space this id is unique within.</summary>
        public ReferenceScopeKind ScopeKind => scopeKind;

        /// <summary>
        /// Which instance of <see cref="ScopeKind"/> this key belongs to: a scene identity for
        /// <see cref="ReferenceScopeKind.Scene"/>, a runtime scope instance id for
        /// <see cref="ReferenceScopeKind.PrefabLocal"/>, and empty for the two global kinds.
        /// </summary>
        public string ScopeId => scopeId ?? string.Empty;

        /// <summary>The provider's type category.</summary>
        public string RefType => refType ?? string.Empty;

        /// <summary>The provider's id, unique within <see cref="ScopeId"/>.</summary>
        public string RefId => refId ?? string.Empty;

        /// <summary>
        /// True when this key is complete enough to register or look up.
        /// </summary>
        /// <remarks>
        /// A scoped kind without a scope id is <i>not</i> valid, and is not quietly promoted to a
        /// global key. Treating "prefab-local, scope unknown" as "global" is how a local id would
        /// escape its instance and collide with every other copy — the failure this whole model
        /// exists to prevent, so it fails closed instead.
        /// </remarks>
        public bool IsValid =>
            !string.IsNullOrEmpty(refId) &&
            !string.IsNullOrEmpty(refType) &&
            (IsGlobalKind(scopeKind) == string.IsNullOrEmpty(scopeId));

        /// <summary>True for the scope kinds that take no scope id.</summary>
        private static bool IsGlobalKind(ReferenceScopeKind kind) =>
            kind == ReferenceScopeKind.Global || kind == ReferenceScopeKind.LegacyGlobal;

        /// <summary>True when this key uses the v1 compatibility scope.</summary>
        public bool IsLegacy => scopeKind == ReferenceScopeKind.LegacyGlobal;

        private ReferenceRuntimeKey(ReferenceScopeKind scopeKind, string scopeId, string refType, string refId)
        {
            this.scopeKind = scopeKind;
            this.scopeId = string.IsNullOrEmpty(scopeId) ? string.Empty : scopeId;
            this.refType = refType ?? string.Empty;
            this.refId = refId ?? string.Empty;
        }

        /// <summary>A key unique across every simultaneously loaded provider.</summary>
        /// <param name="refType">The provider's type category.</param>
        /// <param name="refId">The provider's id.</param>
        public static ReferenceRuntimeKey Global(string refType, string refId) =>
            new ReferenceRuntimeKey(ReferenceScopeKind.Global, null, refType, refId);

        /// <summary>A key unique within one authored scene.</summary>
        /// <param name="sceneId">Identity of the owning scene.</param>
        /// <param name="refType">The provider's type category.</param>
        /// <param name="refId">The provider's id.</param>
        public static ReferenceRuntimeKey Scene(string sceneId, string refType, string refId) =>
            new ReferenceRuntimeKey(ReferenceScopeKind.Scene, sceneId, refType, refId);

        /// <summary>A key unique within one runtime prefab instance.</summary>
        /// <param name="scopeInstanceId">The runtime id of the owning <see cref="ReferenceScopeRoot"/>.</param>
        /// <param name="refType">The provider's type category.</param>
        /// <param name="refId">The provider's local id within the prefab.</param>
        public static ReferenceRuntimeKey PrefabLocal(string scopeInstanceId, string refType, string refId) =>
            new ReferenceRuntimeKey(ReferenceScopeKind.PrefabLocal, scopeInstanceId, refType, refId);

        /// <summary>A key carrying v1's implicit project-wide scope.</summary>
        /// <param name="refType">The provider's type category.</param>
        /// <param name="refId">The provider's id.</param>
        public static ReferenceRuntimeKey Legacy(string refType, string refId) =>
            new ReferenceRuntimeKey(ReferenceScopeKind.LegacyGlobal, null, refType, refId);

        /// <summary>A key carrying v1's implicit project-wide scope.</summary>
        /// <param name="referenceId">An existing v1 reference id.</param>
        public static ReferenceRuntimeKey Legacy(ReferenceId referenceId) =>
            Legacy(referenceId.Type, referenceId.Id);

        /// <summary>
        /// The v1 <see cref="ReferenceId"/> this key corresponds to, for the compatibility index.
        /// </summary>
        /// <remarks>
        /// Only the two global kinds have a meaningful v1 equivalent: a scoped id is not unique
        /// project-wide, so exposing one as a bare <c>(RefType, RefId)</c> would let a v1 lookup
        /// reach into a scope it has no way to name.
        /// </remarks>
        /// <param name="referenceId">The equivalent v1 id when this returns true.</param>
        /// <returns>True when this key has a v1 equivalent.</returns>
        public bool TryToLegacyId(out ReferenceId referenceId)
        {
            if (!IsValid || !IsGlobalKind(scopeKind))
            {
                referenceId = ReferenceId.Invalid;
                return false;
            }

            referenceId = new ReferenceId(refId, refType);
            return true;
        }

        /// <summary>
        /// The same identity re-homed into <paramref name="kind"/>/<paramref name="newScopeId"/>,
        /// used when an authored reference is migrated from one scope to another.
        /// </summary>
        /// <param name="kind">The scope kind to move to.</param>
        /// <param name="newScopeId">The scope id, ignored for the global kinds.</param>
        public ReferenceRuntimeKey WithScope(ReferenceScopeKind kind, string newScopeId = null) =>
            new ReferenceRuntimeKey(kind, IsGlobalKind(kind) ? null : newScopeId, refType, refId);

        /// <summary>
        /// Round-trippable text form: <c>Kind/ScopeId|RefType:RefId</c>, e.g.
        /// <c>PrefabLocal/inst-7|Step:step-a</c> or <c>LegacyGlobal|Step:step-a</c>.
        /// </summary>
        public override string ToString()
        {
            if (!IsValid)
                return "InvalidReferenceRuntimeKey";

            return IsGlobalKind(scopeKind)
                ? $"{scopeKind}|{refType}:{refId}"
                : $"{scopeKind}/{scopeId}|{refType}:{refId}";
        }

        /// <summary>
        /// Parses the form produced by <see cref="ToString"/>.
        /// </summary>
        /// <param name="text">The text to parse.</param>
        /// <param name="key">The parsed key when this returns true.</param>
        /// <returns>True when <paramref name="text"/> was a well-formed, valid key.</returns>
        public static bool TryParse(string text, out ReferenceRuntimeKey key)
        {
            key = default;
            if (string.IsNullOrEmpty(text))
                return false;

            int bar = text.IndexOf('|');
            if (bar <= 0 || bar >= text.Length - 1)
                return false;

            string scopePart = text.Substring(0, bar);
            string idPart = text.Substring(bar + 1);

            // The id half is split on the FIRST colon: a RefType never contains one, but an id
            // generated from a path or a GUID may.
            int colon = idPart.IndexOf(':');
            if (colon <= 0 || colon >= idPart.Length - 1)
                return false;

            string parsedScopeId = null;
            int slash = scopePart.IndexOf('/');
            if (slash >= 0)
            {
                parsedScopeId = scopePart.Substring(slash + 1);
                scopePart = scopePart.Substring(0, slash);
            }

            if (!Enum.TryParse(scopePart, out ReferenceScopeKind parsedKind))
                return false;

            var candidate = new ReferenceRuntimeKey(
                parsedKind, parsedScopeId, idPart.Substring(0, colon), idPart.Substring(colon + 1));

            if (!candidate.IsValid)
                return false;

            key = candidate;
            return true;
        }

        /// <inheritdoc/>
        public bool Equals(ReferenceRuntimeKey other) =>
            scopeKind == other.scopeKind &&
            string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal) &&
            string.Equals(RefType, other.RefType, StringComparison.Ordinal) &&
            string.Equals(RefId, other.RefId, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ReferenceRuntimeKey other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)scopeKind;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(ScopeId);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(RefType);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(RefId);
                return hash;
            }
        }

        /// <summary>Equality operator.</summary>
        public static bool operator ==(ReferenceRuntimeKey left, ReferenceRuntimeKey right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(ReferenceRuntimeKey left, ReferenceRuntimeKey right) => !left.Equals(right);
    }
}
