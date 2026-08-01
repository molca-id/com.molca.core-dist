using System;
using System.Collections.Generic;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Discovers reference providers and reference sites on a single <see cref="UnityEngine.Object"/>
    /// through <see cref="SerializedObject"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Read-only.</b> Nothing here calls <c>SetDirty</c>, applies a modified property, saves an
    /// asset, or generates an id. Discovery that mutates is how a scan could previously redirect a
    /// reference to the wrong duplicate.</para>
    ///
    /// <para><b>Serialized, not reflected.</b> Sites are found by walking serialized properties rather
    /// than reflecting over runtime field values, so array elements, nested serializable structs, and
    /// <c>[SerializeReference]</c> graphs are all covered by the same walk — and a field that merely
    /// happens to contain strings named <c>refId</c> and <c>refType</c> is rejected, because the
    /// property's actual boxed type is checked.</para>
    /// </remarks>
    public static class ReferenceSerializedScanner
    {
        private const string RefIdField = "refId";
        private const string RefTypeField = "refType";

        // SceneObjectReferenceV2's identity fields. Named differently from v1's on purpose, so a walk
        // can tell the two shapes apart from the property tree alone.
        private const string TargetIdField = "targetId";
        private const string ExpectedRefTypeField = "expectedRefType";
        private const string ScopeKindField = "scopeKind";
        private const string ScopeIdField = "scopeId";
        private const string RequirednessField = "requiredness";
        private const string AvailabilityField = "availability";

        /// <summary>
        /// The exact child set of the reference struct in an editor build. Used as the fallback identity
        /// test when <see cref="SerializedProperty.boxedValue"/> is unavailable for a property.
        /// </summary>
        private static readonly HashSet<string> ReferenceStructFields = new(StringComparer.Ordinal)
        {
            RefIdField, RefTypeField, "sceneGuid", "cachedDisplayName",
        };

        /// <summary>What a reference site declares about its scope and requiredness.</summary>
        /// <remarks>
        /// A v1 field declares nothing, which reads as <see cref="ReferenceScopeKind.LegacyGlobal"/>,
        /// <see cref="ReferenceRequiredness.Optional"/> and
        /// <see cref="ReferenceAvailabilityPolicy.Deferred"/> — not as a default, but as a faithful
        /// description of what v1 actually did.
        /// </remarks>
        private readonly struct SiteDeclaration
        {
            public readonly ReferenceScopeKind ScopeKind;
            public readonly string ScopeId;
            public readonly ReferenceRequiredness Requiredness;
            public readonly ReferenceAvailabilityPolicy Availability;

            public SiteDeclaration(
                ReferenceScopeKind scopeKind,
                string scopeId,
                ReferenceRequiredness requiredness,
                ReferenceAvailabilityPolicy availability)
            {
                ScopeKind = scopeKind;
                ScopeId = scopeId;
                Requiredness = requiredness;
                Availability = availability;
            }

            public static SiteDeclaration Legacy => new SiteDeclaration(
                ReferenceScopeKind.LegacyGlobal, null,
                ReferenceRequiredness.Optional, ReferenceAvailabilityPolicy.Deferred);
        }

        /// <summary>
        /// Describes <paramref name="candidate"/> as a provider when it implements
        /// <see cref="IReferenceable"/>.
        /// </summary>
        /// <param name="candidate">The component or asset to describe.</param>
        /// <param name="kind">Which provider category the caller is scanning.</param>
        /// <param name="assetPathHint">Asset path to record when Unity cannot report one.</param>
        /// <returns>The provider record, or null when the object is not referenceable.</returns>
        public static ReferenceProviderRecord TryDescribeProvider(
            UnityEngine.Object candidate, ReferenceProviderKind kind, string assetPathHint = null)
        {
            if (candidate == null || candidate is not IReferenceable referenceable)
                return null;

            var locator = ReferenceObjectLocator.For(candidate, assetPathHint);

            // A faulting IReferenceable implementation must degrade to an unusable provider record, not
            // abort the surrounding scan: the audit's job is to report the project, including its bugs.
            string refId, refType, displayName;
            try
            {
                refId = referenceable.RefId;
                refType = referenceable.RefType;
                displayName = referenceable.DisplayName;
            }
            catch (Exception)
            {
                refId = null;
                refType = null;
                displayName = candidate.name;
            }

            return new ReferenceProviderRecord(
                kind, refId, refType, displayName, candidate.GetType(), locator,
                ReferenceAssetPolicy.IsReadOnly(locator.AssetPath));
        }

        /// <summary>
        /// Appends every reference site declared by <paramref name="owner"/> to <paramref name="into"/>.
        /// </summary>
        /// <param name="owner">The component or asset whose serialized data is walked.</param>
        /// <param name="sourceKind">Which asset category owns the sites.</param>
        /// <param name="into">Destination list; not cleared.</param>
        /// <param name="assetPathHint">Asset path to record when Unity cannot report one.</param>
        /// <param name="onScanError">
        /// Invoked with a human-readable reason when the walk of this object fails. Callers turn it into
        /// a <see cref="ReferenceFindingCode.AssetScanFailed"/> finding so the gap is visible rather than
        /// silently reported as clean.
        /// </param>
        public static void CollectSites(
            UnityEngine.Object owner,
            ReferenceSiteSourceKind sourceKind,
            List<ReferenceSiteRecord> into,
            string assetPathHint = null,
            Action<string> onScanError = null)
        {
            if (owner == null || into == null)
                return;

            // A component whose script is missing deserializes to a null-ish MonoBehaviour; there is
            // nothing to walk and the caller reports it as a coverage error instead.
            ReferenceObjectLocator locator;
            SerializedObject serialized;
            try
            {
                locator = ReferenceObjectLocator.For(owner, assetPathHint);
                serialized = new SerializedObject(owner);
            }
            catch (Exception e)
            {
                onScanError?.Invoke($"{assetPathHint ?? "<unknown asset>"}: could not open for scanning ({e.Message})");
                return;
            }

            var isReadOnly = ReferenceAssetPolicy.IsReadOnly(locator.AssetPath);

            // Resolved once per owner rather than per site: the analyzer is pure and cannot walk a
            // hierarchy, so a prefab-local reference with no enclosing scope root can only be reported as
            // the authoring mistake it is if the scan records what it saw.
            var scopeRootId = NearestScopeRootId(owner);

            using (serialized)
            {
                var property = serialized.GetIterator();
                var enterChildren = true;
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = property.Next(enterChildren);
                    }
                    catch (Exception e)
                    {
                        // A corrupt or partially-deserialized property aborts this object's walk only.
                        onScanError?.Invoke($"{locator}: serialized walk failed ({e.Message})");
                        return;
                    }

                    if (!moved)
                        break;

                    enterChildren = true;

                    if (!TryReadReference(
                            property, out var storedRefId, out var storedRefType, out var expectedType,
                            out var declaration))
                        continue;

                    // A reference struct is a leaf as far as this walk is concerned; descending into its
                    // own identity strings would just rediscover the same site.
                    enterChildren = false;

                    into.Add(new ReferenceSiteRecord(
                        locator, property.propertyPath, storedRefId, storedRefType,
                        expectedType, sourceKind, isReadOnly,
                        declaration.ScopeKind, declaration.ScopeId,
                        declaration.Requiredness, declaration.Availability,
                        scopeRootId));
                }
            }
        }

        /// <summary>
        /// Decides whether <paramref name="property"/> really is a Molca scene-object reference and, if
        /// so, reads its stored identity and the target type it expects.
        /// </summary>
        /// <remarks>
        /// The cheap structural pre-filter runs first (a Generic property with string <c>refId</c> and
        /// <c>refType</c> children); the boxed CLR type then confirms it. Confirming by actual type is
        /// what keeps an unrelated serializable class that happens to carry those two field names out of
        /// the results — the earlier name-only test reported such a class as a broken reference.
        /// </remarks>
        private static bool TryReadReference(
            SerializedProperty property,
            out string storedRefId,
            out string storedRefType,
            out Type expectedType,
            out SiteDeclaration declaration)
        {
            storedRefId = null;
            storedRefType = null;
            expectedType = null;
            declaration = SiteDeclaration.Legacy;

            if (property.propertyType != SerializedPropertyType.Generic || property.isArray)
                return false;

            // v2 first: it is identified by its own field names, so it can never be mistaken for the v1
            // shape and its declared scope is never silently dropped.
            if (TryReadV2Reference(property, out storedRefId, out storedRefType, out declaration))
                return true;

            var refIdProperty = property.FindPropertyRelative(RefIdField);
            if (refIdProperty == null || refIdProperty.propertyType != SerializedPropertyType.String)
                return false;

            var refTypeProperty = property.FindPropertyRelative(RefTypeField);
            if (refTypeProperty == null || refTypeProperty.propertyType != SerializedPropertyType.String)
                return false;

            var boxedType = TryGetBoxedType(property);
            if (boxedType != null)
            {
                if (!IsReferenceStruct(boxedType, out expectedType))
                    return false;
            }
            else if (!HasExactlyReferenceStructFields(property))
            {
                // Neither the boxed type nor the child set identifies this as a reference struct.
                return false;
            }

            storedRefId = refIdProperty.stringValue;
            storedRefType = refTypeProperty.stringValue;
            return true;
        }

        /// <summary>
        /// Reads a <see cref="SceneObjectReferenceV2"/> property, including everything it declares.
        /// </summary>
        /// <remarks>
        /// Identified structurally by <c>targetId</c> + <c>expectedRefType</c> + <c>scopeKind</c>. Unlike
        /// v1 that needs no boxed-type confirmation: those three names together in one serializable struct
        /// are specific enough that a false positive would have to be a deliberate imitation, and reading
        /// the enums by index means a future scope kind is read rather than silently dropped.
        /// </remarks>
        private static bool TryReadV2Reference(
            SerializedProperty property,
            out string storedRefId,
            out string storedRefType,
            out SiteDeclaration declaration)
        {
            storedRefId = null;
            storedRefType = null;
            declaration = SiteDeclaration.Legacy;

            var targetId = property.FindPropertyRelative(TargetIdField);
            if (targetId == null || targetId.propertyType != SerializedPropertyType.String)
                return false;

            var expectedRefType = property.FindPropertyRelative(ExpectedRefTypeField);
            if (expectedRefType == null || expectedRefType.propertyType != SerializedPropertyType.String)
                return false;

            var scopeKind = property.FindPropertyRelative(ScopeKindField);
            if (scopeKind == null || scopeKind.propertyType != SerializedPropertyType.Enum)
                return false;

            storedRefId = targetId.stringValue;
            storedRefType = expectedRefType.stringValue;

            declaration = new SiteDeclaration(
                Enum<ReferenceScopeKind>(scopeKind, ReferenceScopeKind.LegacyGlobal),
                property.FindPropertyRelative(ScopeIdField)?.stringValue,
                Enum<ReferenceRequiredness>(
                    property.FindPropertyRelative(RequirednessField), ReferenceRequiredness.Optional),
                Enum<ReferenceAvailabilityPolicy>(
                    property.FindPropertyRelative(AvailabilityField), ReferenceAvailabilityPolicy.Deferred));

            return true;
        }

        /// <summary>
        /// Reads an enum-valued property by index, falling back when the stored index is out of range.
        /// </summary>
        /// <remarks>
        /// <see cref="SerializedProperty.enumValueIndex"/> is a position in the declaration order, not the
        /// underlying value, so an index written by a newer version of the enum can be out of range here.
        /// Falling back beats throwing during a scan whose whole job is to survive bad data.
        /// </remarks>
        private static T Enum<T>(SerializedProperty property, T fallback) where T : struct, Enum
        {
            if (property == null || property.propertyType != SerializedPropertyType.Enum)
                return fallback;

            var values = (T[])System.Enum.GetValues(typeof(T));
            int index = property.enumValueIndex;
            return index >= 0 && index < values.Length ? values[index] : fallback;
        }

        /// <summary>
        /// The scope template id of the nearest <c>ReferenceScopeRoot</c> above <paramref name="owner"/>,
        /// or empty when there is none — or when the owner is not a component at all.
        /// </summary>
        private static string NearestScopeRootId(UnityEngine.Object owner)
        {
            if (owner is not Component component)
                return string.Empty;

            try
            {
                return ReferenceScopeRoot.FindNearest(component)?.ScopeTemplateId ?? string.Empty;
            }
            catch (Exception)
            {
                // A destroyed or partially-loaded hierarchy must not abort the scan; an unknown scope root
                // is reported as absent, which is the conservative reading.
                return string.Empty;
            }
        }

        /// <summary>The property's boxed CLR type, or null when Unity cannot box it.</summary>
        private static Type TryGetBoxedType(SerializedProperty property)
        {
            try
            {
                return property.boxedValue?.GetType();
            }
            catch (Exception)
            {
                // boxedValue throws for some managed-reference and multi-edit shapes; the structural
                // fallback covers those.
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="type"/> is <see cref="SceneObjectReference"/> or
        /// <see cref="SceneObjectReference{T}"/>, reporting the promised target type for the generic form.
        /// </summary>
        private static bool IsReferenceStruct(Type type, out Type expectedTargetType)
        {
            expectedTargetType = null;

            if (type == typeof(SceneObjectReference))
                return true;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SceneObjectReference<>))
            {
                expectedTargetType = type.GetGenericArguments()[0];
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when the property's immediate children are exactly the reference struct's fields — the
        /// strict fallback used when the boxed type is unavailable.
        /// </summary>
        private static bool HasExactlyReferenceStructFields(SerializedProperty property)
        {
            var child = property.Copy();
            var end = property.GetEndProperty();
            if (!child.NextVisible(true))
                return false;

            var seen = 0;
            while (!SerializedProperty.EqualContents(child, end))
            {
                if (child.propertyType != SerializedPropertyType.String || !ReferenceStructFields.Contains(child.name))
                    return false;

                seen++;
                if (!child.NextVisible(false))
                    break;
            }

            // refId and refType are always present; the two editor-only fields may be stripped.
            return seen >= 2;
        }
    }
}
