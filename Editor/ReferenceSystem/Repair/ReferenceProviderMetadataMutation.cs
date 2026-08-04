using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;
using UnityEditor;

namespace Molca.Editor.ReferenceSystem.Repair
{
    /// <summary>
    /// Finds the serialized field behind a provider's Ref Type or scope mode, at apply time.
    /// </summary>
    /// <remarks>
    /// <para><see cref="IReferenceable"/> exposes <c>RefId</c> as settable and <c>RefType</c> as read-only,
    /// so a Ref Id can be written through the contract and a Ref Type cannot. The backing field name is the
    /// implementer's business — <c>ReferenceableComponent</c> calls it <c>refType</c>, another implementer may
    /// call it anything — which is exactly why this locates the field rather than assuming one.</para>
    ///
    /// <para>Discovery happens against the live object during <c>VerifyPrecondition</c>, not during planning.
    /// Planning stays pure over the snapshot, and a provider whose field cannot be identified is skipped with
    /// a reason instead of being written by guess. It never descends into nested structs: a
    /// <c>SceneObjectReference</c> field on the same component also has a <c>refType</c>, and writing that one
    /// would re-point an unrelated reference.</para>
    /// </remarks>
    internal static class ReferenceProviderFieldLocator
    {
        /// <summary>Conventional name of the Ref Type backing field.</summary>
        internal const string RefTypeFieldName = "refType";

        /// <summary>Conventional name of the scope-mode backing field.</summary>
        internal const string ScopeModeFieldName = "scopeMode";

        /// <summary>
        /// Locates the top-level string field holding <paramref name="expectedValue"/>.
        /// </summary>
        /// <param name="serialized">The live object's serialized form.</param>
        /// <param name="expectedValue">The value the field is expected to hold.</param>
        /// <param name="preferredName">Field name to prefer when several fields hold the value.</param>
        /// <param name="allowEmptyOnPreferred">
        /// Accept an empty preferred field. <c>ReferenceableComponent.RefType</c> substitutes a default when
        /// the serialized string is empty, so the record's value and the field's value legitimately differ.
        /// </param>
        /// <param name="propertyPath">The located property path.</param>
        /// <param name="failure">Why no single field could be identified.</param>
        /// <returns>True when exactly one field was identified.</returns>
        internal static bool TryFindStringField(
            SerializedObject serialized,
            string expectedValue,
            string preferredName,
            bool allowEmptyOnPreferred,
            out string propertyPath,
            out string failure)
        {
            propertyPath = null;
            failure = null;

            var matches = new List<string>();
            string preferredPath = null;
            string preferredValue = null;

            // NextVisible(false) after the first step keeps the walk at depth 1. Descending would also match
            // the refType inside every SceneObjectReference field on the same component.
            var iterator = serialized.GetIterator();
            for (var enter = true; iterator.NextVisible(enter); enter = false)
            {
                if (iterator.propertyType != SerializedPropertyType.String)
                    continue;

                if (string.Equals(iterator.name, preferredName, StringComparison.Ordinal))
                {
                    preferredPath = iterator.propertyPath;
                    preferredValue = iterator.stringValue;
                }

                if (string.Equals(iterator.stringValue, expectedValue, StringComparison.Ordinal))
                    matches.Add(iterator.propertyPath);
            }

            if (preferredPath != null
                && (string.Equals(preferredValue, expectedValue, StringComparison.Ordinal)
                    || (allowEmptyOnPreferred && string.IsNullOrEmpty(preferredValue))))
            {
                propertyPath = preferredPath;
                return true;
            }

            if (matches.Count == 1)
            {
                propertyPath = matches[0];
                return true;
            }

            failure = matches.Count == 0
                ? $"no serialized string field on this object holds \"{expectedValue}\", so the field backing "
                  + $"its Ref Type could not be identified; rename it to '{preferredName}' or change it in the "
                  + "Inspector instead"
                : $"{matches.Count} serialized fields hold \"{expectedValue}\" ({string.Join(", ", matches)}) "
                  + $"and none is named '{preferredName}', so which one backs the Ref Type is a guess";
            return false;
        }

        /// <summary>Locates the top-level enum field named <paramref name="name"/>.</summary>
        /// <param name="serialized">The live object's serialized form.</param>
        /// <param name="name">The field name to look for.</param>
        /// <param name="propertyPath">The located property path.</param>
        /// <param name="failure">Why the field was not found.</param>
        /// <returns>True when the field exists and is an enum.</returns>
        internal static bool TryFindEnumField(
            SerializedObject serialized, string name, out string propertyPath, out string failure)
        {
            propertyPath = null;
            failure = null;

            var property = serialized.FindProperty(name);
            if (property == null)
            {
                failure = $"this object has no '{name}' field, so its scope is not authored on the component";
                return false;
            }

            if (property.propertyType != SerializedPropertyType.Enum)
            {
                failure = $"'{name}' is a {property.propertyType}, not an enum";
                return false;
            }

            propertyPath = property.propertyPath;
            return true;
        }
    }

    /// <summary>
    /// Rewrites a provider's Ref Type.
    /// </summary>
    /// <remarks>
    /// Half of a retype: this moves the provider, and one
    /// <see cref="ReferenceSitePropertyMutation"/> per inbound site moves the references that name it. The
    /// planner emits them together in a single plan, because either half applied alone breaks every
    /// reference to this target.
    /// </remarks>
    public sealed class ReferenceProviderTypeMutation : ReferenceRepairMutation
    {
        /// <summary>The Ref Type the provider is expected to currently report.</summary>
        public string PreviousRefType { get; }

        /// <summary>The Ref Type that will be written.</summary>
        public string NewRefType { get; }

        /// <summary>The provider's Ref Id, which this mutation leaves alone.</summary>
        public string RefId { get; }

        internal ReferenceProviderTypeMutation(
            ReferenceProviderRecord provider, string newRefType, string reason)
            : base(ReferenceRepairKind.RetypeProvider, ReferenceRepairApproval.RequiresUserChoice,
                   provider.Locator, reason, !provider.IsReadOnly,
                   requiresSave: provider.Kind != ReferenceProviderKind.SceneComponent)
        {
            PreviousRefType = provider.RefType;
            NewRefType = newRefType ?? string.Empty;
            RefId = provider.RefId;
        }

        /// <inheritdoc/>
        public override string Describe() =>
            $"{Target}  Ref Type \"{PreviousRefType}\" → \"{NewRefType}\" (id \"{RefId}\")";

        /// <inheritdoc/>
        internal override bool VerifyPrecondition(UnityEngine.Object target, out string failure)
        {
            failure = null;

            if (target is not IReferenceable referenceable)
            {
                failure = $"{Target} is no longer an IReferenceable";
                return false;
            }

            // The interface is authoritative about the current type; the field is only where it is stored.
            if (!string.Equals(referenceable.RefType ?? string.Empty, PreviousRefType, StringComparison.Ordinal))
            {
                failure =
                    $"{Target} now reports Ref Type \"{referenceable.RefType}\" but the plan was built against "
                    + $"\"{PreviousRefType}\"; re-run the audit and rebuild the plan";
                return false;
            }

            return ReferenceProviderFieldLocator.TryFindStringField(
                new SerializedObject(target), PreviousRefType,
                ReferenceProviderFieldLocator.RefTypeFieldName,
                allowEmptyOnPreferred: true, out _, out failure);
        }

        /// <inheritdoc/>
        internal override bool TryApply(UnityEngine.Object target, out string failure)
        {
            var serialized = new SerializedObject(target);
            if (!ReferenceProviderFieldLocator.TryFindStringField(
                    serialized, PreviousRefType, ReferenceProviderFieldLocator.RefTypeFieldName,
                    allowEmptyOnPreferred: true, out var path, out failure))
                return false;

            serialized.FindProperty(path).stringValue = NewRefType;
            serialized.ApplyModifiedProperties();
            return true;
        }
    }

    /// <summary>
    /// Changes which space a provider's id is required to be unique in.
    /// </summary>
    /// <remarks>
    /// The most consequential single-field change in the system, and the reason it is never batched: scope is
    /// part of identity (<see cref="ReferenceRuntimeKey"/>), so moving a provider between scopes changes what
    /// it registers as, and every reference authored against the old scope stops reaching it. The planner
    /// attaches that as a warning naming the affected references rather than leaving it to be discovered at
    /// run time.
    /// </remarks>
    public sealed class ReferenceProviderScopeMutation : ReferenceRepairMutation
    {
        /// <summary>The scope the provider is expected to currently declare.</summary>
        public ReferenceScopeKind PreviousScope { get; }

        /// <summary>The scope that will be written.</summary>
        public ReferenceScopeKind NewScope { get; }

        internal ReferenceProviderScopeMutation(
            ReferenceProviderRecord provider,
            ReferenceScopeKind previousScope,
            ReferenceScopeKind newScope,
            string reason)
            : base(ReferenceRepairKind.ChangeProviderScope, ReferenceRepairApproval.RequiresUserChoice,
                   provider.Locator, reason, !provider.IsReadOnly,
                   requiresSave: provider.Kind != ReferenceProviderKind.SceneComponent)
        {
            PreviousScope = previousScope;
            NewScope = newScope;
        }

        /// <inheritdoc/>
        public override string Describe() => $"{Target}  scope {PreviousScope} → {NewScope}";

        /// <inheritdoc/>
        internal override bool VerifyPrecondition(UnityEngine.Object target, out string failure)
        {
            var serialized = new SerializedObject(target);
            if (!ReferenceProviderFieldLocator.TryFindEnumField(
                    serialized, ReferenceProviderFieldLocator.ScopeModeFieldName, out var path, out failure))
                return false;

            var current = (ReferenceScopeKind)serialized.FindProperty(path).intValue;
            if (current != PreviousScope)
            {
                failure =
                    $"{Target} now declares scope {current} but the plan was built against {PreviousScope}; "
                    + "re-run the audit and rebuild the plan";
                return false;
            }

            return true;
        }

        /// <inheritdoc/>
        internal override bool TryApply(UnityEngine.Object target, out string failure)
        {
            var serialized = new SerializedObject(target);
            if (!ReferenceProviderFieldLocator.TryFindEnumField(
                    serialized, ReferenceProviderFieldLocator.ScopeModeFieldName, out var path, out failure))
                return false;

            serialized.FindProperty(path).intValue = (int)NewScope;
            serialized.ApplyModifiedProperties();
            return true;
        }
    }

    /// <summary>
    /// Turns a display name into a Ref Id that follows the framework's kebab-case convention.
    /// </summary>
    /// <remarks>
    /// <c>ReferenceGenerator</c> emits <c>ref_&lt;guid&gt;</c>, which is collision-safe and unreadable, while
    /// the naming convention asks for <c>main-valve</c>. Both are legal ids; the difference is that only one
    /// of them can be recognised in a diff, a log line, or a duplicate report. This produces the readable
    /// form and guarantees it is free, so choosing it costs the author nothing.
    /// </remarks>
    public static class ReferenceIdSuggestion
    {
        /// <summary>Lower-case kebab form of <paramref name="text"/>, or empty when nothing survives.</summary>
        /// <param name="text">Arbitrary author-supplied text, typically a GameObject name.</param>
        public static string ToSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var builder = new System.Text.StringBuilder(text.Length);
            var previousWasSeparator = true;

            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character))
                {
                    // An interior capital starts a new word, so "MainValve" reads as "main-valve" rather
                    // than as one opaque token.
                    if (char.IsUpper(character) && !previousWasSeparator && builder.Length > 0
                        && char.IsLower(builder[builder.Length - 1]))
                        builder.Append('-');

                    builder.Append(char.ToLowerInvariant(character));
                    previousWasSeparator = false;
                }
                else if (!previousWasSeparator)
                {
                    builder.Append('-');
                    previousWasSeparator = true;
                }
            }

            return builder.ToString().Trim('-');
        }

        /// <summary>
        /// A readable Ref Id derived from <paramref name="displayName"/> that nothing in
        /// <paramref name="taken"/> already holds.
        /// </summary>
        /// <param name="displayName">The provider's display name.</param>
        /// <param name="refType">Fallback stem when the display name slugs to nothing.</param>
        /// <param name="taken">Ids already in use under the same Ref Type.</param>
        /// <returns>A free id, suffixed <c>-2</c>, <c>-3</c>… only when the plain slug is taken.</returns>
        public static string Suggest(string displayName, string refType, IEnumerable<string> taken)
        {
            var used = new HashSet<string>(
                (taken ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);

            var stem = ToSlug(displayName);
            if (string.IsNullOrEmpty(stem))
                stem = ToSlug(refType);
            if (string.IsNullOrEmpty(stem))
                stem = "referenceable";

            if (!used.Contains(stem))
                return stem;

            for (var suffix = 2; suffix < 1000; suffix++)
            {
                var candidate = $"{stem}-{suffix}";
                if (!used.Contains(candidate))
                    return candidate;
            }

            // A thousand same-named targets is not a naming problem this helper should paper over, so it
            // falls back to the generator rather than inventing a longer suffix.
            return ReferenceGenerator.GenerateUniqueId(
                string.IsNullOrEmpty(refType) ? "Referenceable" : refType);
        }
    }
}
