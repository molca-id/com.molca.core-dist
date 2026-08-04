using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem.Repair;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// The authoring commands the References workspace issues, and the one approval gate they all pass
    /// through.
    /// </summary>
    /// <remarks>
    /// <para>Every command here either builds a <see cref="ReferenceRepairPlan"/> and runs it past the user
    /// through <see cref="PreviewAndApply"/>, or creates something that did not exist before. There is no
    /// third category, and in particular nothing that edits an existing identity without a preview: the plan
    /// is the approval, and the approved text is logged so it survives the dialog being dismissed.</para>
    ///
    /// <para><see cref="MakeSelectionReferenceable"/> is the one command with no plan behind it, for the same
    /// reason the repair planner treats assigning a missing id as automatic: nothing can reference an object
    /// that is not yet a target, so creating one cannot re-point anything. It is still a single Undo group
    /// and it still invalidates the audit.</para>
    /// </remarks>
    internal static class ReferenceHubAuthoring
    {
        /// <summary>
        /// Builds a plan, shows it in full, and applies it only on explicit approval.
        /// </summary>
        /// <param name="build">Produces the plan. Called on the main thread.</param>
        /// <param name="title">Dialog title describing the act, e.g. "Rename Target".</param>
        /// <remarks>
        /// <c>async void</c> because this is a UI command entry point; the body is a try/catch shim per the
        /// async contract.
        /// </remarks>
        internal static async void PreviewAndApply( // doctor:ignore async-void is intentional: UI command entry point wrapped in try/catch
            Func<ReferenceRepairPlan> build, string title = "Apply Reference Changes?")
        {
            try
            {
                var plan = build();

                if (plan.IsEmpty)
                {
                    // The refusal reason lives in the plan's warnings, which Preview() prints for an empty
                    // plan precisely so a refusal is never silent.
                    EditorUtility.DisplayDialog("Nothing To Apply", plan.Preview(), "OK");
                    return;
                }

                Debug.Log(plan.Preview());

                if (!EditorUtility.DisplayDialog(
                        title,
                        $"{plan.DescribeSummary()}.\n\nThe full plan is in the Console. Reversibility: "
                        + $"{plan.Reversibility}.\n\n"
                        + (plan.Warnings.Count > 0 ? "! " + string.Join("\n! ", plan.Warnings) + "\n\n" : string.Empty)
                        + "Apply it?",
                        "Apply", "Cancel"))
                    return;

                var result = await ReferenceRepairExecutor.ApplyAsync(plan);

                if (result.Introduced.Count > 0)
                    Debug.LogError(result.Describe());
                else if (result.WasRejected || result.Skipped.Count > 0)
                    Debug.LogWarning(result.Describe());
                else
                    Debug.Log(result.Describe());
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceSystem] Reference authoring failed: {e}");
            }
        }

        /// <summary>
        /// The scope a provider's component currently declares, or null when it declares none.
        /// </summary>
        /// <param name="locator">Address of the providing object.</param>
        /// <returns>
        /// The declared scope, or null when the object is unreachable or has no <c>scopeMode</c> field —
        /// which is the normal case for an implementer that predates scoped references.
        /// </returns>
        /// <remarks>
        /// Read from the live object rather than from the snapshot because a provider record carries no
        /// scope: only sites do. Offering a scope editor for a component that has no scope field would be
        /// offering a control that cannot write anywhere.
        /// </remarks>
        internal static ReferenceScopeKind? TryReadScope(ReferenceObjectLocator locator)
        {
            var target = locator.TryResolve();
            if (target == null)
                return null;

            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(ReferenceProviderFieldLocator.ScopeModeFieldName);
            return property is { propertyType: SerializedPropertyType.Enum }
                ? (ReferenceScopeKind)property.intValue
                : null;
        }

        /// <summary>
        /// The provider in <paramref name="snapshot"/> that the current hierarchy selection is, if any.
        /// </summary>
        /// <param name="snapshot">The audit to search.</param>
        /// <returns>A provider key, or empty when the selection is not a discovered target.</returns>
        /// <remarks>
        /// Matched on <see cref="ReferenceObjectLocator.Key"/>, not on name or id: two objects can share
        /// both, and pointing forty references at the wrong one of them is precisely the failure this whole
        /// system exists to prevent.
        /// </remarks>
        internal static string SelectionProviderKey(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null || Selection.activeObject == null)
                return string.Empty;

            var keys = SelectionLocatorKeys();
            if (keys.Count == 0)
                return string.Empty;

            return snapshot.Providers
                .Where(p => keys.Contains(p.Locator.Key))
                .OrderByDescending(p => p.IsRuntimeResolvable)
                .Select(p => p.ProviderKey)
                .FirstOrDefault() ?? string.Empty;
        }

        /// <summary>A short description of the current selection, for a button label.</summary>
        internal static string DescribeSelection()
        {
            var active = Selection.activeObject;
            if (active == null)
                return string.Empty;

            var extra = Selection.objects.Length - 1;
            return extra > 0 ? $"{active.name} +{extra}" : active.name;
        }

        /// <summary>
        /// Adds a <see cref="ReferenceableComponent"/> to every selected GameObject that has no
        /// <see cref="IReferenceable"/> yet, with a readable id derived from its name.
        /// </summary>
        /// <param name="snapshot">The audit, used to keep the generated ids free.</param>
        /// <param name="refType">The Ref Type to author. Empty falls back to the component default.</param>
        /// <returns>How many objects became targets.</returns>
        internal static int MakeSelectionReferenceable(ReferenceAuditSnapshot snapshot, string refType)
        {
            var candidates = Selection.gameObjects
                .Where(go => go != null && go.GetComponent<IReferenceable>() == null)
                .ToList();

            if (candidates.Count == 0)
                return 0;

            var type = string.IsNullOrWhiteSpace(refType) ? "Referenceable" : refType.Trim();

            var taken = new HashSet<string>(
                (snapshot?.Providers ?? Array.Empty<ReferenceProviderRecord>())
                .Where(p => string.Equals(p.RefType, type, StringComparison.Ordinal))
                .Select(p => p.RefId)
                .Where(id => !string.IsNullOrEmpty(id)),
                StringComparer.Ordinal);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Make {candidates.Count} object(s) referenceable");
            var group = Undo.GetCurrentGroup();

            foreach (var gameObject in candidates)
            {
                var component = Undo.AddComponent<ReferenceableComponent>(gameObject);

                // OnValidate has already put a generated ref_<guid> in place; replacing it with the slug is
                // the whole point of authoring the target here rather than by dropping the component.
                var id = ReferenceIdSuggestion.Suggest(gameObject.name, type, taken);
                taken.Add(id);

                var serialized = new SerializedObject(component);
                serialized.FindProperty("refId").stringValue = id;
                serialized.FindProperty(ReferenceProviderFieldLocator.RefTypeFieldName).stringValue = type;
                serialized.ApplyModifiedProperties();
            }

            Undo.CollapseUndoOperations(group);
            ReferenceAuditService.Invalidate($"{candidates.Count} object(s) were made referenceable");
            return candidates.Count;
        }

        /// <summary>Every locator key the current selection covers, components included.</summary>
        /// <remarks>
        /// A GameObject is selected but a <see cref="ReferenceableComponent"/> is what provides, so matching
        /// only the selected object itself would never find anything.
        /// </remarks>
        private static HashSet<string> SelectionLocatorKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var selected in Selection.objects)
            {
                if (selected == null)
                    continue;

                keys.Add(ReferenceObjectLocator.For(selected).Key);

                if (selected is GameObject gameObject)
                {
                    foreach (var referenceable in gameObject.GetComponents<Component>()
                                 .Where(c => c is IReferenceable))
                        keys.Add(ReferenceObjectLocator.For(referenceable).Key);
                }
            }

            return keys;
        }
    }
}
