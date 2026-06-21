using System;
using System.Collections.Generic;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Single Undo-grouped path for managing a <see cref="Step"/>'s <see cref="StepAuxiliary"/>
    /// list — add / remove / reorder / set-fields on the <c>[SerializeReference]</c> managed-reference
    /// array. The CRUD that <see cref="StepEditingService"/> deliberately left out. Edit mode only;
    /// no GUI dependencies. Owner references are rebound after every structural change via
    /// <see cref="Step.EnsureAuxiliaryOwnerReferences"/>.
    /// </summary>
    public static class AuxiliaryEditingService
    {
        private const string AuxiliariesProperty = "auxiliaries";

        /// <summary>
        /// Appends a new auxiliary of <paramref name="auxiliaryType"/> to <paramref name="step"/> as one
        /// undo group.
        /// </summary>
        /// <param name="step">The owning step.</param>
        /// <param name="auxiliaryType">Concrete <see cref="StepAuxiliary"/> type to instantiate.</param>
        /// <returns>The index of the new auxiliary, or -1 on invalid arguments.</returns>
        public static int AddAuxiliary(Step step, Type auxiliaryType)
        {
            if (step == null || !IsConcreteAuxiliaryType(auxiliaryType)) return -1;

            int group = BeginGroup("Add Auxiliary");
            var so = new SerializedObject(step);
            var array = so.FindProperty(AuxiliariesProperty);
            int index = array.arraySize;
            array.arraySize++;
            array.GetArrayElementAtIndex(index).managedReferenceValue = Activator.CreateInstance(auxiliaryType);
            so.ApplyModifiedProperties();
            step.EnsureAuxiliaryOwnerReferences();
            Undo.CollapseUndoOperations(group);
            return index;
        }

        /// <summary>
        /// Removes the auxiliary at <paramref name="index"/> from <paramref name="step"/> as one undo group.
        /// </summary>
        /// <param name="step">The owning step.</param>
        /// <param name="index">Index into <see cref="Step.Auxiliaries"/>.</param>
        /// <returns><c>true</c> if an auxiliary was removed.</returns>
        public static bool RemoveAuxiliary(Step step, int index)
        {
            if (step == null) return false;

            int group = BeginGroup("Remove Auxiliary");
            var so = new SerializedObject(step);
            var array = so.FindProperty(AuxiliariesProperty);
            if (index < 0 || index >= array.arraySize)
            {
                Undo.CollapseUndoOperations(group);
                return false;
            }
            array.DeleteArrayElementAtIndex(index);
            so.ApplyModifiedProperties();
            step.EnsureAuxiliaryOwnerReferences();
            Undo.CollapseUndoOperations(group);
            return true;
        }

        /// <summary>
        /// Moves the auxiliary at <paramref name="fromIndex"/> to <paramref name="toIndex"/> as one undo group.
        /// </summary>
        /// <param name="step">The owning step.</param>
        /// <param name="fromIndex">Current index.</param>
        /// <param name="toIndex">Destination index.</param>
        /// <returns><c>true</c> if the auxiliary was moved.</returns>
        public static bool ReorderAuxiliary(Step step, int fromIndex, int toIndex)
        {
            if (step == null) return false;

            int group = BeginGroup("Reorder Auxiliary");
            var so = new SerializedObject(step);
            var array = so.FindProperty(AuxiliariesProperty);
            if (fromIndex < 0 || fromIndex >= array.arraySize || toIndex < 0 || toIndex >= array.arraySize)
            {
                Undo.CollapseUndoOperations(group);
                return false;
            }
            array.MoveArrayElement(fromIndex, toIndex);
            so.ApplyModifiedProperties();
            step.EnsureAuxiliaryOwnerReferences();
            Undo.CollapseUndoOperations(group);
            return true;
        }

        /// <summary>
        /// Writes serialized fields on the auxiliary at <paramref name="index"/> as one undo group,
        /// coercing through <see cref="SerializedFieldCoercion"/>.
        /// </summary>
        /// <param name="step">The owning step.</param>
        /// <param name="index">Index into <see cref="Step.Auxiliaries"/>.</param>
        /// <param name="fields">Field name → string value.</param>
        /// <returns>Which fields were applied and which were rejected (and why).</returns>
        public static StepFieldEditingService.SetFieldsResult SetAuxiliaryFields(
            Step step, int index, IReadOnlyDictionary<string, string> fields)
            => SetAuxiliaryFields(step, index, StepFieldEditingService.WrapScalars(fields));

        /// <summary>
        /// Structured counterpart to
        /// <see cref="SetAuxiliaryFields(Step, int, IReadOnlyDictionary{string, string})"/>: each value is
        /// a <see cref="FieldNode"/>, so an auxiliary field can carry a nested composite object or a list
        /// of objects (e.g. a <c>DynamicLocalization</c> title with per-language <c>translations</c>).
        /// </summary>
        /// <param name="step">The owning step.</param>
        /// <param name="index">Index into <see cref="Step.Auxiliaries"/>.</param>
        /// <param name="fields">Field name → structured value.</param>
        /// <returns>Which fields were applied and which were rejected (and why).</returns>
        internal static StepFieldEditingService.SetFieldsResult SetAuxiliaryFields(
            Step step, int index, IReadOnlyDictionary<string, FieldNode> fields)
        {
            var applied = new List<string>();
            var rejected = new List<KeyValuePair<string, string>>();
            if (step == null || fields == null) return Result(applied, rejected);

            int group = BeginGroup("Set Auxiliary Fields");
            var so = new SerializedObject(step);
            var array = so.FindProperty(AuxiliariesProperty);
            if (index < 0 || index >= array.arraySize)
            {
                Undo.CollapseUndoOperations(group);
                rejected.Add(new KeyValuePair<string, string>("(index)", $"auxiliary index {index} out of range"));
                return Result(applied, rejected);
            }

            var element = array.GetArrayElementAtIndex(index);
            foreach (var pair in fields)
            {
                var prop = element.FindPropertyRelative(pair.Key);
                if (prop == null)
                {
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, "no such serialized field on the auxiliary"));
                    continue;
                }
                if (SerializedFieldCoercion.TrySet(prop, pair.Value, out var error))
                    applied.Add(pair.Key);
                else
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, error));
            }

            if (applied.Count > 0) so.ApplyModifiedProperties();
            step.EnsureAuxiliaryOwnerReferences();
            Undo.CollapseUndoOperations(group);
            return Result(applied, rejected);
        }

        /// <summary>
        /// Lists the writable serialized field names on the auxiliary at <paramref name="index"/>.
        /// </summary>
        /// <param name="step">The owning step.</param>
        /// <param name="index">Index into <see cref="Step.Auxiliaries"/>.</param>
        /// <returns>Writable serialized field names, empty if the index is out of range.</returns>
        public static List<string> GetWritableFields(Step step, int index)
        {
            var names = new List<string>();
            if (step == null) return names;

            var array = new SerializedObject(step).FindProperty(AuxiliariesProperty);
            if (index < 0 || index >= array.arraySize) return names;

            var element = array.GetArrayElementAtIndex(index);
            var iterator = element.Copy();
            var end = element.GetEndProperty();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                names.Add(iterator.name);
            }
            return names;
        }

        /// <summary>
        /// Reads the current values of the serialized fields on the auxiliary at <paramref name="index"/>
        /// (Sprint 25 follow-up) — the read counterpart to <see cref="SetAuxiliaryFields(Step, int, IReadOnlyDictionary{string, string})"/>.
        /// </summary>
        /// <param name="step">The owning step.</param>
        /// <param name="index">Index into <see cref="Step.Auxiliaries"/>.</param>
        /// <returns>Each field's name, type, and current value; empty if the index is out of range.</returns>
        public static List<StepFieldEditingService.FieldValue> GetAuxiliaryFields(Step step, int index)
        {
            var values = new List<StepFieldEditingService.FieldValue>();
            if (step == null) return values;

            var array = new SerializedObject(step).FindProperty(AuxiliariesProperty);
            if (index < 0 || index >= array.arraySize) return values;

            var element = array.GetArrayElementAtIndex(index);
            var iterator = element.Copy();
            var end = element.GetEndProperty();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                values.Add(new StepFieldEditingService.FieldValue(iterator.name,
                    iterator.propertyType.ToString(), SerializedFieldCoercion.ReadValue(iterator)));
            }
            return values;
        }

        private static StepFieldEditingService.SetFieldsResult Result(
            List<string> applied, List<KeyValuePair<string, string>> rejected)
            => new StepFieldEditingService.SetFieldsResult(applied, rejected);

        private static bool IsConcreteAuxiliaryType(Type type) =>
            type != null && typeof(StepAuxiliary).IsAssignableFrom(type) && !type.IsAbstract;

        private static int BeginGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return Undo.GetCurrentGroup();
        }
    }
}
