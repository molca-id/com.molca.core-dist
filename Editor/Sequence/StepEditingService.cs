using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;
using Molca.Sequence;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Describes one step to create in a <see cref="StepEditingService.AddSteps"/> batch.
    /// </summary>
    public readonly struct StepCreationRequest
    {
        /// <summary>Concrete <see cref="Step"/> type to add.</summary>
        public Type StepType { get; }

        /// <summary>Parent step, or <c>null</c> to create under the controller root.</summary>
        public Step Parent { get; }

        /// <summary>GameObject name, or <c>null</c> to derive from the type name.</summary>
        public string Name { get; }

        /// <param name="stepType">Concrete <see cref="Step"/> type to add.</param>
        /// <param name="parent">Parent step, or <c>null</c> for the controller root.</param>
        /// <param name="name">GameObject name, or <c>null</c> to derive from the type name.</param>
        public StepCreationRequest(Type stepType, Step parent = null, string name = null)
        {
            StepType = stepType;
            Parent = parent;
            Name = name;
        }
    }

    /// <summary>
    /// Single Undo-grouped CRUD path for sequence steps — shared by the visualizer,
    /// the graph editor, <see cref="StepEditor"/>, and the CSV step importer.
    /// </summary>
    /// <remarks>
    /// Every public operation collapses to exactly one undo group, including the bulk
    /// overloads (one group per batch). Edit mode only; no GUI dependencies. Callers are
    /// responsible for refreshing their own caches/selection after a call.
    /// </remarks>
    public static class StepEditingService
    {
        /// <summary>
        /// Creates a new step GameObject under <paramref name="parent"/> (or the controller root)
        /// as a single undo operation.
        /// </summary>
        /// <param name="controller">Owning controller; must not be <c>null</c>.</param>
        /// <param name="stepType">Concrete <see cref="Step"/> type to add.</param>
        /// <param name="parent">Parent step, or <c>null</c> to add at the controller root.</param>
        /// <param name="name">GameObject name, or <c>null</c> to derive from the type name.</param>
        /// <param name="siblingIndex">Insertion index among the parent's children, or -1 to append.</param>
        /// <returns>The created step, or <c>null</c> on invalid arguments.</returns>
        public static Step AddStep(SequenceController controller, Type stepType, Step parent = null, string name = null, int siblingIndex = -1)
        {
            if (controller == null || !IsConcreteStepType(stepType)) return null;

            int group = BeginGroup("Create Step");
            var step = AddStepInternal(controller, stepType, parent, name, siblingIndex);
            Undo.CollapseUndoOperations(group);
            return step;
        }

        /// <summary>
        /// Creates a batch of steps as one undo group (bulk path for the CSV importer and graph editor).
        /// </summary>
        /// <param name="controller">Owning controller; must not be <c>null</c>.</param>
        /// <param name="requests">Steps to create, processed in order so earlier entries can parent later ones.</param>
        /// <returns>The created steps in request order; invalid requests are skipped.</returns>
        public static List<Step> AddSteps(SequenceController controller, IEnumerable<StepCreationRequest> requests)
        {
            var created = new List<Step>();
            if (controller == null || requests == null) return created;

            int group = BeginGroup("Create Steps");
            foreach (var request in requests)
            {
                if (!IsConcreteStepType(request.StepType)) continue;
                created.Add(AddStepInternal(controller, request.StepType, request.Parent, request.Name, -1));
            }
            Undo.CollapseUndoOperations(group);
            return created;
        }

        /// <summary>
        /// Destroys the given steps' GameObjects (and thereby their children) as one undo group.
        /// Steps that are descendants of other steps in the set are skipped to avoid double-destroy.
        /// </summary>
        /// <param name="steps">Steps to remove. Null/destroyed entries are ignored.</param>
        /// <returns>The number of root GameObjects destroyed.</returns>
        public static int RemoveSteps(IEnumerable<Step> steps)
        {
            var roots = FilterTopmost(steps);
            if (roots.Count == 0) return 0;

            int group = BeginGroup(roots.Count == 1 ? "Delete Step" : "Delete Steps");
            foreach (var step in roots)
            {
                Undo.DestroyObjectImmediate(step.gameObject);
            }
            Undo.CollapseUndoOperations(group);
            return roots.Count;
        }

        /// <summary>
        /// Duplicates each source step's GameObject subtree next to the original and assigns a
        /// fresh Ref Id to every <see cref="Step"/> in each clone. One undo group per batch.
        /// Sources that are descendants of other sources are skipped (the parent clone already
        /// contains them).
        /// </summary>
        /// <param name="sources">Steps to duplicate. Null/destroyed entries are ignored.</param>
        /// <returns>The root clone steps, in source order.</returns>
        public static List<Step> DuplicateSteps(IEnumerable<Step> sources)
        {
            var roots = FilterTopmost(sources);
            var clones = new List<Step>();
            if (roots.Count == 0) return clones;

            int group = BeginGroup(roots.Count == 1 ? "Duplicate Step" : "Duplicate Steps");
            foreach (var source in roots)
            {
                var parent = source.transform.parent;
                var cloneGO = UnityEngine.Object.Instantiate(source.gameObject, parent);
                cloneGO.name = GameObjectUtility.GetUniqueNameForSibling(parent, source.gameObject.name);
                cloneGO.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                Undo.RegisterCreatedObjectUndo(cloneGO, "Duplicate Step");

                // Every step in the clone subtree must get its own Ref Id — duplicated ids
                // would silently break SceneObjectReference resolution.
                foreach (var cloneStep in cloneGO.GetComponentsInChildren<Step>(true))
                {
                    var so = new SerializedObject(cloneStep);
                    so.FindProperty("refId").stringValue = ReferenceGenerator.GenerateUniqueId(cloneStep.RefType);
                    // The created object is already undoable as a whole; no per-property undo needed.
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                clones.Add(cloneGO.GetComponent<Step>());
            }
            Undo.CollapseUndoOperations(group);
            return clones;
        }

        /// <summary>
        /// Replaces a step component with one of <paramref name="newType"/> on the same GameObject,
        /// preserving Ref Id, step id, and auxiliaries via serialized copy (deep-clones
        /// SerializeReference auxiliary instances instead of aliasing live objects).
        /// One undo group; children and transform position are untouched.
        /// </summary>
        /// <param name="oldStep">The step to convert.</param>
        /// <param name="newType">Concrete <see cref="Step"/> type to convert to.</param>
        /// <returns>The replacement step, or <c>null</c> on invalid arguments. Returns <paramref name="oldStep"/> unchanged if it already has that type.</returns>
        public static Step ChangeStepType(Step oldStep, Type newType)
        {
            if (oldStep == null || !IsConcreteStepType(newType)) return null;
            if (oldStep.GetType() == newType) return oldStep;

            int group = BeginGroup("Change Step Type");
            var newStep = ChangeStepTypeInternal(oldStep, newType);
            Undo.CollapseUndoOperations(group);
            return newStep;
        }

        /// <summary>
        /// Converts a batch of steps to <paramref name="newType"/> as one undo group.
        /// </summary>
        /// <param name="steps">Steps to convert. Null entries and steps already of the target type are skipped.</param>
        /// <param name="newType">Concrete <see cref="Step"/> type to convert to.</param>
        /// <returns>Old-step → new-step pairs for the steps that were converted (callers remap selection with this).</returns>
        public static List<KeyValuePair<Step, Step>> ChangeStepTypes(IEnumerable<Step> steps, Type newType)
        {
            var converted = new List<KeyValuePair<Step, Step>>();
            if (steps == null || !IsConcreteStepType(newType)) return converted;

            int group = BeginGroup("Change Step Type");
            foreach (var step in steps.Where(s => s != null && s.GetType() != newType).ToList())
            {
                converted.Add(new KeyValuePair<Step, Step>(step, ChangeStepTypeInternal(step, newType)));
            }
            Undo.CollapseUndoOperations(group);
            return converted;
        }

        /// <summary>
        /// Moves steps under a new parent transform and orders them contiguously starting at
        /// <paramref name="siblingIndex"/>, as one undo group. Steps that are descendants of
        /// other steps in the set move with their parent and are skipped.
        /// </summary>
        /// <param name="steps">Steps to move, in the order they should appear under the new parent.</param>
        /// <param name="newParent">Target parent transform (a step's transform or the controller root). Must not be <c>null</c>.</param>
        /// <param name="siblingIndex">Index of the first moved step among the parent's children, or -1 to append at the end.</param>
        /// <returns>The number of steps moved.</returns>
        public static int ReparentSteps(IEnumerable<Step> steps, Transform newParent, int siblingIndex = -1)
        {
            if (newParent == null) return 0;
            var roots = FilterTopmost(steps);
            // Reject moves that would parent a step under its own subtree.
            roots.RemoveAll(s => newParent == s.transform || newParent.IsChildOf(s.transform));
            if (roots.Count == 0) return 0;

            int group = BeginGroup(roots.Count == 1 ? "Move Step" : "Move Steps");
            int index = siblingIndex;
            foreach (var step in roots)
            {
                Undo.SetTransformParent(step.transform, newParent, "Move Step");
                if (index >= 0)
                {
                    step.transform.SetSiblingIndex(Mathf.Min(index, newParent.childCount - 1));
                    index++;
                }
                else
                {
                    step.transform.SetAsLastSibling();
                }
            }
            Undo.CollapseUndoOperations(group);
            return roots.Count;
        }

        private static Step AddStepInternal(SequenceController controller, Type stepType, Step parent, string name, int siblingIndex)
        {
            var go = new GameObject(string.IsNullOrEmpty(name) ? ObjectNames.NicifyVariableName(stepType.Name) : name);
            Undo.RegisterCreatedObjectUndo(go, "Create Step");

            Transform parentTransform = parent != null ? parent.transform : controller.transform;
            go.transform.SetParent(parentTransform);
            if (siblingIndex >= 0)
            {
                go.transform.SetSiblingIndex(Mathf.Min(siblingIndex, parentTransform.childCount - 1));
            }

            return (Step)Undo.AddComponent(go, stepType);
        }

        private static Step ChangeStepTypeInternal(Step oldStep, Type newType)
        {
            var go = oldStep.gameObject;

            // 1. Add the new component undoably (RecordObject on the GameObject does not
            //    make component addition undoable — Undo.AddComponent does).
            var newStep = (Step)Undo.AddComponent(go, newType);

            // 2. Copy preserved data (RefId, StepId, auxiliaries) at the serialized-data level.
            var oldSO = new SerializedObject(oldStep);
            var newSO = new SerializedObject(newStep);
            newSO.CopyFromSerializedProperty(oldSO.FindProperty("refId"));
            newSO.CopyFromSerializedProperty(oldSO.FindProperty("stepId"));

            // CopyFromSerializedProperty cannot transfer a SerializeReference array between
            // different objects (the managed-reference table is per-object), so clone each
            // auxiliary explicitly. EditorJsonUtility round-trip deep-clones the instance
            // while preserving UnityEngine.Object field references (instance ids).
            var oldAux = oldSO.FindProperty("auxiliaries");
            var newAux = newSO.FindProperty("auxiliaries");
            newAux.arraySize = oldAux.arraySize;
            for (int i = 0; i < oldAux.arraySize; i++)
            {
                var source = oldAux.GetArrayElementAtIndex(i).managedReferenceValue;
                object clone = null;
                if (source != null)
                {
                    clone = Activator.CreateInstance(source.GetType());
                    EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(source), clone);
                }
                newAux.GetArrayElementAtIndex(i).managedReferenceValue = clone;
            }
            newSO.ApplyModifiedProperties();

            // 3. Rebind the cloned auxiliaries to their new owner.
            newStep.EnsureAuxiliaryOwnerReferences();

            // 4. Destroy the old component (same undo group as the add).
            Undo.DestroyObjectImmediate(oldStep);

            return newStep;
        }

        /// <summary>
        /// Filters out null/destroyed entries, duplicates, and steps whose transform is a
        /// descendant of another step in the set, preserving input order.
        /// </summary>
        private static List<Step> FilterTopmost(IEnumerable<Step> steps)
        {
            var distinct = new List<Step>();
            if (steps == null) return distinct;
            foreach (var s in steps)
            {
                if (s != null && !distinct.Contains(s)) distinct.Add(s);
            }
            return distinct
                .Where(s => !distinct.Any(other => other != s && s.transform.IsChildOf(other.transform)))
                .ToList();
        }

        private static bool IsConcreteStepType(Type type) =>
            type != null && typeof(Step).IsAssignableFrom(type) && !type.IsAbstract;

        private static int BeginGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return Undo.GetCurrentGroup();
        }
    }
}
