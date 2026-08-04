using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem;
using Molca.ReferenceSystem;
using UnityEditor;

namespace Molca.Editor.ReferenceSystem.Repair
{
    /// <summary>What kind of change a mutation makes, and therefore how it must be authorized.</summary>
    public enum ReferenceRepairKind
    {
        /// <summary>
        /// Give a Ref Id to a provider that has none. Safe: nothing can reference an id that does not exist.
        /// </summary>
        AssignMissingProviderId = 0,

        /// <summary>
        /// Refresh a reference's cached Ref Type and display name from the provider it already resolves to.
        /// Safe: the identity — the Ref Id — is untouched.
        /// </summary>
        RefreshStaleMetadata = 1,

        /// <summary>
        /// Re-key one of several providers colliding on a <c>(RefType, RefId)</c> that nothing references.
        /// Safe only under that condition: with an inbound reference, re-keying silently re-points it.
        /// </summary>
        RekeyUnreferencedDuplicate = 2,

        /// <summary>
        /// Point a reference at a specific provider. Requires a user choice: which provider was meant is
        /// not recoverable from the data.
        /// </summary>
        RedirectReference = 3,

        /// <summary>
        /// Clear a reference. Requires a user choice: clearing to make validation green destroys intent.
        /// </summary>
        ClearReference = 4,

        /// <summary>
        /// Give a provider a Ref Id the author chose. Never automatic, and never emitted without the
        /// <see cref="FollowProviderRename"/> mutations that carry every inbound reference with it.
        /// </summary>
        RenameProviderId = 5,

        /// <summary>
        /// Move one inbound reference onto a provider's new identity. The target does not change — only
        /// the name it is known by — which is what separates this from <see cref="RedirectReference"/>.
        /// </summary>
        FollowProviderRename = 6,

        /// <summary>Change a provider's Ref Type, carrying its inbound references with it.</summary>
        RetypeProvider = 7,

        /// <summary>
        /// Change which space a provider's id must be unique in. Scope is part of identity, so this is
        /// the one metadata change that can strand every reference to the target.
        /// </summary>
        ChangeProviderScope = 8,
    }

    /// <summary>Whether a repair may be applied without asking, or needs an explicit decision.</summary>
    public enum ReferenceRepairApproval
    {
        /// <summary>The outcome is unambiguous, so it can be batched.</summary>
        Automatic = 0,

        /// <summary>The outcome depends on intent the data does not record.</summary>
        RequiresUserChoice = 1,
    }

    /// <summary>
    /// One exact, previewable change to one object.
    /// </summary>
    /// <remarks>
    /// A mutation records both the value it expects to find and the value it will write, and re-checks the
    /// former at apply time. That is what makes a plan safe to show a user and then apply: if anything moved
    /// between preview and apply, the mutation is skipped with a reason rather than overwriting a value the
    /// user never saw.
    /// </remarks>
    public abstract class ReferenceRepairMutation
    {
        /// <summary>What kind of change this is.</summary>
        public ReferenceRepairKind Kind { get; }

        /// <summary>Whether this change may be applied without an explicit decision.</summary>
        public ReferenceRepairApproval Approval { get; }

        /// <summary>Editor address of the object being changed.</summary>
        public ReferenceObjectLocator Target { get; }

        /// <summary>Why this change is proposed, in the user's terms.</summary>
        public string Reason { get; }

        /// <summary>Project-relative path of the asset being changed. Empty for an unsaved scene object.</summary>
        public string AssetPath => Target.AssetPath;

        /// <summary>False when the owning asset is a package or otherwise non-writable.</summary>
        public bool IsTargetWritable { get; }

        /// <summary>
        /// True when Unity Undo will cover this change.
        /// </summary>
        /// <remarks>
        /// Undo covers changes to objects loaded in memory — scene objects and loaded assets. It does not
        /// cover the subsequent asset <i>save</i>, which is why <see cref="RequiresSave"/> changes are listed
        /// separately before a plan is applied: Ctrl+Z restores the in-memory value, but the file on disk has
        /// already changed and only version control can restore that.
        /// </remarks>
        public bool IsUndoCovered { get; }

        /// <summary>True when applying this change needs an explicit asset save to persist.</summary>
        public bool RequiresSave { get; }

        protected ReferenceRepairMutation(
            ReferenceRepairKind kind,
            ReferenceRepairApproval approval,
            ReferenceObjectLocator target,
            string reason,
            bool isTargetWritable,
            bool requiresSave)
        {
            Kind = kind;
            Approval = approval;
            Target = target;
            Reason = reason ?? string.Empty;
            IsTargetWritable = isTargetWritable;
            RequiresSave = requiresSave;
            IsUndoCovered = true;
        }

        /// <summary>Human-readable before/after description for the plan preview.</summary>
        public abstract string Describe();

        /// <summary>
        /// Confirms the object still holds the values this mutation was planned against.
        /// </summary>
        /// <param name="target">The resolved live object.</param>
        /// <param name="failure">Why the precondition does not hold.</param>
        /// <returns>True when it is still safe to apply.</returns>
        internal abstract bool VerifyPrecondition(UnityEngine.Object target, out string failure);

        /// <summary>
        /// Applies the change. The caller has already recorded <paramref name="target"/> with Undo.
        /// </summary>
        /// <param name="target">The resolved live object.</param>
        /// <param name="failure">Why the change could not be applied.</param>
        /// <returns>True when the change was applied.</returns>
        internal abstract bool TryApply(UnityEngine.Object target, out string failure);

        /// <inheritdoc/>
        public override string ToString() => Describe();
    }

    /// <summary>
    /// Rewrites the serialized fields of one <see cref="SceneObjectReference"/> site.
    /// </summary>
    /// <remarks>
    /// Applied through <see cref="SerializedObject"/> rather than by assigning the struct, so the change
    /// participates in Undo and in prefab-override tracking exactly as an Inspector edit would.
    /// </remarks>
    public sealed class ReferenceSitePropertyMutation : ReferenceRepairMutation
    {
        /// <summary>Serialized path of the reference field.</summary>
        public string PropertyPath { get; }

        /// <summary>Relative field name to expected current value.</summary>
        public IReadOnlyDictionary<string, string> PreviousValues { get; }

        /// <summary>Relative field name to the value that will be written.</summary>
        public IReadOnlyDictionary<string, string> NewValues { get; }

        internal ReferenceSitePropertyMutation(
            ReferenceRepairKind kind,
            ReferenceRepairApproval approval,
            ReferenceSiteRecord site,
            IReadOnlyDictionary<string, string> previousValues,
            IReadOnlyDictionary<string, string> newValues,
            string reason)
            : base(kind, approval, site.OwnerLocator, reason, !site.IsReadOnly,
                   requiresSave: site.SourceKind != ReferenceSiteSourceKind.Scene)
        {
            PropertyPath = site.PropertyPath;
            PreviousValues = previousValues;
            NewValues = newValues;
        }

        /// <inheritdoc/>
        public override string Describe()
        {
            var changes = NewValues
                .Where(kv => !string.Equals(PreviousValues.GetValueOrDefault(kv.Key), kv.Value, StringComparison.Ordinal))
                .Select(kv => $"{kv.Key}: \"{PreviousValues.GetValueOrDefault(kv.Key)}\" → \"{kv.Value}\"");

            return $"{Target} → {PropertyPath}  [{string.Join(", ", changes)}]";
        }

        /// <inheritdoc/>
        internal override bool VerifyPrecondition(UnityEngine.Object target, out string failure)
        {
            failure = null;

            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(PropertyPath);
            if (property == null)
            {
                failure = $"the property '{PropertyPath}' no longer exists on {Target}";
                return false;
            }

            foreach (var expected in PreviousValues)
            {
                var field = property.FindPropertyRelative(expected.Key);
                if (field == null)
                {
                    failure = $"the field '{expected.Key}' no longer exists at {PropertyPath}";
                    return false;
                }

                if (!string.Equals(field.stringValue, expected.Value, StringComparison.Ordinal))
                {
                    failure =
                        $"'{PropertyPath}.{expected.Key}' now holds \"{field.stringValue}\" but the plan was "
                        + $"built against \"{expected.Value}\"; re-run the audit and rebuild the plan";
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc/>
        internal override bool TryApply(UnityEngine.Object target, out string failure)
        {
            failure = null;

            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(PropertyPath);
            if (property == null)
            {
                failure = $"the property '{PropertyPath}' no longer exists on {Target}";
                return false;
            }

            foreach (var change in NewValues)
            {
                var field = property.FindPropertyRelative(change.Key);
                if (field == null)
                {
                    failure = $"the field '{change.Key}' no longer exists at {PropertyPath}";
                    return false;
                }

                field.stringValue = change.Value;
            }

            serialized.ApplyModifiedProperties();
            return true;
        }
    }

    /// <summary>
    /// Assigns a new Ref Id to a provider.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="IReferenceable.RefId"/> rather than a serialized property path, because the
    /// backing field name is the implementer's business — <c>ReferenceableComponent</c> and <c>Step</c> call
    /// it <c>refId</c>, <c>SequenceController</c> calls it <c>sequenceId</c>, and a custom implementer may
    /// call it anything. The interface is the contract; the field name is not.
    /// </remarks>
    public sealed class ReferenceProviderIdMutation : ReferenceRepairMutation
    {
        /// <summary>The Ref Id the provider is expected to currently hold.</summary>
        public string PreviousRefId { get; }

        /// <summary>The Ref Id that will be assigned.</summary>
        public string NewRefId { get; }

        /// <summary>The provider's Ref Type, for display.</summary>
        public string RefType { get; }

        internal ReferenceProviderIdMutation(
            ReferenceRepairKind kind,
            ReferenceRepairApproval approval,
            ReferenceProviderRecord provider,
            string newRefId,
            string reason)
            : base(kind, approval, provider.Locator, reason, !provider.IsReadOnly,
                   requiresSave: provider.Kind != ReferenceProviderKind.SceneComponent)
        {
            PreviousRefId = provider.RefId;
            NewRefId = newRefId;
            RefType = provider.RefType;
        }

        /// <inheritdoc/>
        public override string Describe() =>
            $"{Target}  Ref Id \"{PreviousRefId}\" → \"{NewRefId}\" (type \"{RefType}\")";

        /// <inheritdoc/>
        internal override bool VerifyPrecondition(UnityEngine.Object target, out string failure)
        {
            failure = null;

            if (target is not IReferenceable referenceable)
            {
                failure = $"{Target} is no longer an IReferenceable";
                return false;
            }

            if (!string.Equals(referenceable.RefId ?? string.Empty, PreviousRefId, StringComparison.Ordinal))
            {
                failure =
                    $"{Target} now has Ref Id \"{referenceable.RefId}\" but the plan was built against "
                    + $"\"{PreviousRefId}\"; re-run the audit and rebuild the plan";
                return false;
            }

            return true;
        }

        /// <inheritdoc/>
        internal override bool TryApply(UnityEngine.Object target, out string failure)
        {
            failure = null;

            if (target is not IReferenceable referenceable)
            {
                failure = $"{Target} is no longer an IReferenceable";
                return false;
            }

            try
            {
                referenceable.RefId = NewRefId;
                EditorUtility.SetDirty(target);
                return true;
            }
            catch (Exception e)
            {
                failure = $"assigning a Ref Id to {Target} threw: {e.Message}";
                return false;
            }
        }
    }
}
