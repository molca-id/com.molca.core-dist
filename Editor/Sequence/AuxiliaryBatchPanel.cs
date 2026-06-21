using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using Molca.Editor.Utils;
using Molca.Editor.UI;

namespace Molca.Editor
{
    /// <summary>
    /// Embeddable IMGUI panel for editing <see cref="StepAuxiliary"/> data across a
    /// multi-step selection. Hosted by <see cref="SequenceVisualizerWindow"/> in its
    /// details pane, where Unity's built-in inspector cannot multi-edit
    /// <c>[SerializeReference]</c> lists.
    /// </summary>
    /// <remarks>
    /// All writes go through each step's <see cref="SerializedObject"/> so undo,
    /// dirty-marking, and prefab overrides behave like normal inspector edits.
    /// Edit-mode only; the host must not draw this panel in play mode.
    /// </remarks>
    internal class AuxiliaryBatchPanel
    {
        private int _selectedTypeIndex;
        private bool _expanded = true;

        private static GUIStyle _boldFoldoutStyle;
        private static GUIStyle BoldFoldoutStyle =>
            _boldFoldoutStyle ??= new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };

        /// <summary>
        /// Draws the batch auxiliary section for the given selection.
        /// </summary>
        /// <param name="selectedSteps">Currently selected steps, primary last.</param>
        public void Draw(IReadOnlyList<Step> selectedSteps)
        {
            var steps = selectedSteps.Where(s => s != null).ToList();
            if (steps.Count == 0) return;

            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _expanded = EditorGUILayout.Foldout(_expanded,
                $"Batch Auxiliaries ({steps.Count} steps)", true, BoldFoldoutStyle);
            if (!_expanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            // Coverage map: auxiliary type -> steps that carry at least one instance of it.
            var coverage = BuildCoverage(steps);

            DrawAddButton(steps);

            if (coverage.Count == 0)
            {
                EditorGUILayout.HelpBox("No auxiliaries on the selected steps. Use 'Add Auxiliary to Selection' to add one to every step at once.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            var types = coverage.Keys.OrderBy(t => t.Name).ToList();
            var labels = types.Select(t => $"{ObjectNames.NicifyVariableName(t.Name)} ({coverage[t].Count}/{steps.Count})").ToArray();
            _selectedTypeIndex = Mathf.Clamp(_selectedTypeIndex, 0, types.Count - 1);
            _selectedTypeIndex = EditorGUILayout.Popup("Auxiliary Type", _selectedTypeIndex, labels);

            var type = types[_selectedTypeIndex];
            var carriers = coverage[type];

            // Primary = last selected carrier; its values are shown and edits fan out.
            var primary = steps.LastOrDefault(s => carriers.Contains(s));

            EditorGUILayout.Space(2);
            DrawTypeActions(type, steps, carriers, primary);

            if (carriers.Count < steps.Count)
            {
                EditorGUILayout.HelpBox($"{steps.Count - carriers.Count} selected step(s) do not have {type.Name}. Edits apply only to steps that have it.", MessageType.None);
            }

            if (primary != null)
            {
                EditorGUILayout.LabelField($"Editing '{primary.name}' — changes apply to all {carriers.Count} step(s)", EditorStyles.miniLabel);
                DrawAndPropagate(type, primary, carriers);
            }

            EditorGUILayout.EndVertical();
        }

        #region Coverage / type discovery

        private static Dictionary<Type, List<Step>> BuildCoverage(List<Step> steps)
        {
            var coverage = new Dictionary<Type, List<Step>>();
            foreach (var step in steps)
            {
                foreach (var aux in step.Auxiliaries)
                {
                    if (aux == null) continue;
                    var t = aux.GetType();
                    if (!coverage.TryGetValue(t, out var list))
                        coverage[t] = list = new List<Step>();
                    if (!list.Contains(step)) list.Add(step);
                }
            }
            return coverage;
        }

        #endregion

        #region Batch add / remove / sync

        private void DrawAddButton(List<Step> steps)
        {
            if (!GUILayout.Button("Add Auxiliary to Selection")) return;

            var menu = new GenericMenu();
            var items = TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                .Where(t => !t.IsAbstract)
                .Select(t =>
                {
                    var attr = t.GetCustomAttribute<AuxiliaryMenuAttribute>();
                    return (path: attr != null && !string.IsNullOrEmpty(attr.Path) ? attr.Path : t.Name,
                            type: t,
                            allowMultiple: attr?.AllowMultiple ?? false);
                })
                .OrderBy(i => i.path, StringComparer.Ordinal);

            foreach (var item in items)
            {
                // Disabled when no selected step can receive it (all already have it and multiples are not allowed).
                bool anyTarget = item.allowMultiple || steps.Any(s => !HasAuxiliaryOfType(s, item.type));
                var content = new GUIContent(item.path);
                if (anyTarget)
                    menu.AddItem(content, false, () => AddToSelection(steps, item.type, item.allowMultiple));
                else
                    menu.AddDisabledItem(content);
            }
            menu.ShowAsContext();
        }

        private static void AddToSelection(List<Step> steps, Type type, bool allowMultiple)
        {
            Undo.IncrementCurrentGroup();
            int added = 0;
            foreach (var step in steps)
            {
                if (!allowMultiple && HasAuxiliaryOfType(step, type)) continue;

                Undo.RecordObject(step, $"Add {type.Name} to Selection");
                var aux = (StepAuxiliary)Activator.CreateInstance(type);
                aux.BindOwnerFromStep(step);
                step.AddAuxiliary(aux);
                EditorUtility.SetDirty(step);
                added++;
            }
            Undo.SetCurrentGroupName($"Add {type.Name} to {added} step(s)");
            Debug.Log($"[AuxiliaryBatchPanel] Added {type.Name} to {added} step(s).");
        }

        private void DrawTypeActions(Type type, List<Step> steps, List<Step> carriers, Step primary)
        {
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(carriers.Count >= steps.Count))
            {
                if (GUILayout.Button($"Add to Missing ({steps.Count - carriers.Count})"))
                {
                    AddToSelection(steps.Where(s => !HasAuxiliaryOfType(s, type)).ToList(), type, false);
                }
            }

            using (new EditorGUI.DisabledScope(primary == null || carriers.Count < 2))
            {
                if (GUILayout.Button("Sync All From Primary"))
                {
                    // Count-confirm: this overwrites every other carrier's values for this type.
                    int targets = carriers.Count - 1;
                    if (EditorUtility.DisplayDialog("Sync Auxiliary?",
                        $"Overwrite {type.Name} on {targets} other step(s) with the values from '{primary.name}'?\n\nThis replaces all fields, including per-step values.",
                        "Sync", "Cancel"))
                    {
                        PropagateWholeAuxiliary(type, primary, carriers);
                    }
                }
            }

            if (GUILayout.Button($"Remove From All ({carriers.Count})"))
            {
                if (EditorUtility.DisplayDialog("Remove Auxiliary?",
                    $"Remove {type.Name} from {carriers.Count} selected step(s)?", "Remove", "Cancel"))
                {
                    RemoveFromSteps(type, carriers);
                }
            }

            EditorGUILayout.EndHorizontal();

            // Clipboard row — shares AuxiliaryClipboard with StepEditor's context menu.
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(primary == null))
            {
                if (GUILayout.Button("Copy From Primary"))
                {
                    AuxiliaryClipboard.Copy(GetAuxiliaryInstance(primary, type));
                }
            }
            using (new EditorGUI.DisabledScope(!AuxiliaryClipboard.CanPasteType(type)))
            {
                if (GUILayout.Button($"Paste To All ({carriers.Count})"))
                {
                    PasteClipboardToCarriers(type, carriers);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Returns the first auxiliary instance of <paramref name="type"/> on <paramref name="step"/>, or null.</summary>
        private static StepAuxiliary GetAuxiliaryInstance(Step step, Type type)
        {
            return step == null ? null : step.Auxiliaries.FirstOrDefault(a => a != null && a.GetType() == type);
        }

        /// <summary>Pastes the clipboard auxiliary into every carrier of <paramref name="type"/> as one undo group.</summary>
        private static void PasteClipboardToCarriers(Type type, List<Step> carriers)
        {
            Undo.IncrementCurrentGroup();
            int pasted = 0;
            foreach (var step in carriers)
            {
                var target = GetAuxiliaryInstance(step, type);
                if (target == null) continue;
                Undo.RecordObject(step, $"Paste {type.Name}");
                if (AuxiliaryClipboard.Paste(target))
                {
                    EditorUtility.SetDirty(step);
                    pasted++;
                }
            }
            Undo.SetCurrentGroupName($"Paste {type.Name} to {pasted} step(s)");
        }

        private static void RemoveFromSteps(Type type, List<Step> carriers)
        {
            Undo.IncrementCurrentGroup();
            foreach (var step in carriers)
            {
                Undo.RecordObject(step, $"Remove {type.Name}");
                // Remove every instance of the type (AllowMultiple types may have several).
                foreach (var aux in step.Auxiliaries.Where(a => a != null && a.GetType() == type).ToList())
                    step.RemoveAuxiliary(aux);
                EditorUtility.SetDirty(step);
            }
            Undo.SetCurrentGroupName($"Remove {type.Name} from {carriers.Count} step(s)");
        }

        #endregion

        #region Field drawing + propagation

        /// <summary>
        /// Draws the primary step's auxiliary fields. Any field the user changes is
        /// immediately copied to the matching auxiliary on every other carrier step,
        /// leaving their untouched fields (e.g. per-step scene references) intact.
        /// </summary>
        private void DrawAndPropagate(Type type, Step primary, List<Step> carriers)
        {
            var primarySO = new SerializedObject(primary);
            var element = FindAuxiliaryProperty(primarySO, type);
            if (element == null)
            {
                EditorGUILayout.HelpBox($"Could not resolve {type.Name} on '{primary.name}'.", MessageType.Warning);
                return;
            }

            bool hasCustomDrawer = type.GetCustomAttribute<CustomAuxiliaryDrawerAttribute>() != null;

            EditorGUI.indentLevel++;
            if (hasCustomDrawer)
            {
                // Custom-drawn auxiliaries are opaque: draw whole, propagate whole on change.
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(element, true);
                if (EditorGUI.EndChangeCheck())
                {
                    primarySO.ApplyModifiedProperties();
                    PropagateWholeAuxiliary(type, primary, carriers);
                }
            }
            else
            {
                // Cache other carriers' SerializedObjects once for differ comparison this pass.
                var others = carriers.Where(s => s != primary).Select(s => new SerializedObject(s)).ToList();

                var iterator = element.Copy();
                var endProperty = iterator.GetEndProperty();
                iterator.NextVisible(true);
                do
                {
                    if (SerializedProperty.EqualContents(iterator, endProperty)) break;

                    bool differs = FieldDiffersAcrossCarriers(type, iterator.name, iterator, others);

                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(iterator, true);
                    Rect fieldRect = GUILayoutUtility.GetLastRect();
                    if (differs)
                    {
                        // Amber left bar + tooltip: this field's value is not uniform across the selection.
                        EditorGUI.DrawRect(new Rect(fieldRect.x - 2, fieldRect.y, 2, fieldRect.height), MolcaEditorColors.StatusWarn);
                        GUI.Label(new Rect(fieldRect.x - 14, fieldRect.y, 14, EditorGUIUtility.singleLineHeight),
                            new GUIContent("≠", "This field differs across the selected steps."));
                    }
                    if (EditorGUI.EndChangeCheck())
                    {
                        string changedField = iterator.name;
                        primarySO.ApplyModifiedProperties();
                        PropagateField(type, primary, carriers, changedField);
                        // Property layout may shift after apply; bail out of this pass.
                        break;
                    }
                } while (iterator.NextVisible(false));
            }
            EditorGUI.indentLevel--;
        }

        /// <summary>Copies one top-level field of the primary's auxiliary to all other carriers.</summary>
        private static void PropagateField(Type type, Step primary, List<Step> carriers, string fieldName)
        {
            var primarySO = new SerializedObject(primary);
            var source = FindAuxiliaryProperty(primarySO, type)?.FindPropertyRelative(fieldName);
            if (source == null) return;

            foreach (var step in carriers)
            {
                if (step == primary) continue;
                var targetSO = new SerializedObject(step);
                var target = FindAuxiliaryProperty(targetSO, type)?.FindPropertyRelative(fieldName);
                if (target == null) continue;
                CopyPropertyRecursive(source, target);
                targetSO.ApplyModifiedProperties();
            }
        }

        /// <summary>Copies the entire auxiliary value (all fields) from primary to all other carriers.</summary>
        private static void PropagateWholeAuxiliary(Type type, Step primary, List<Step> carriers)
        {
            var primarySO = new SerializedObject(primary);
            var source = FindAuxiliaryProperty(primarySO, type);
            if (source == null) return;

            foreach (var step in carriers)
            {
                if (step == primary) continue;
                var targetSO = new SerializedObject(step);
                var target = FindAuxiliaryProperty(targetSO, type);
                if (target == null) continue;
                CopyPropertyRecursive(source, target);
                targetSO.ApplyModifiedProperties();
            }
            Debug.Log($"[AuxiliaryBatchPanel] Synced {type.Name} from '{primary.name}' to {carriers.Count - 1} step(s).");
        }

        /// <summary>
        /// Returns whether <paramref name="fieldName"/> on the given auxiliary type has a
        /// different value on any of the <paramref name="others"/> than on the primary.
        /// </summary>
        private static bool FieldDiffersAcrossCarriers(Type type, string fieldName, SerializedProperty primaryField, List<SerializedObject> others)
        {
            foreach (var otherSO in others)
            {
                var otherField = FindAuxiliaryProperty(otherSO, type)?.FindPropertyRelative(fieldName);
                if (otherField == null) continue; // carrier lacks the field (different shape) — ignore
                if (!SerializedProperty.DataEquals(primaryField, otherField)) return true;
            }
            return false;
        }

        /// <summary>
        /// Finds the first <c>auxiliaries</c> array element whose managed reference is of
        /// the given concrete type. Returns null if the step has no such auxiliary.
        /// </summary>
        private static SerializedProperty FindAuxiliaryProperty(SerializedObject stepSO, Type type)
        {
            var list = stepSO.FindProperty("auxiliaries");
            if (list == null) return null;
            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                if (element.managedReferenceValue?.GetType() == type)
                    return element;
            }
            return null;
        }

        /// <summary>
        /// Deep-copies a serialized property subtree between two objects. Needed because
        /// <see cref="SerializedObject.CopyFromSerializedProperty"/> requires identical
        /// property paths, which differ when auxiliaries sit at different list indices.
        /// </summary>
        private static void CopyPropertyRecursive(SerializedProperty source, SerializedProperty target)
        {
            switch (source.propertyType)
            {
                case SerializedPropertyType.Generic:
                    if (source.isArray)
                    {
                        target.arraySize = source.arraySize;
                        for (int i = 0; i < source.arraySize; i++)
                            CopyPropertyRecursive(source.GetArrayElementAtIndex(i), target.GetArrayElementAtIndex(i));
                    }
                    else
                    {
                        var child = source.Copy();
                        var end = source.GetEndProperty();
                        if (child.NextVisible(true))
                        {
                            int depth = child.depth;
                            do
                            {
                                if (SerializedProperty.EqualContents(child, end)) break;
                                if (child.depth != depth) continue;
                                var targetChild = target.FindPropertyRelative(child.name);
                                if (targetChild != null)
                                    CopyPropertyRecursive(child, targetChild);
                            } while (child.NextVisible(false));
                        }
                    }
                    break;

                case SerializedPropertyType.ManagedReference:
                    // Nested managed references inside auxiliaries: clone by full-subtree copy is
                    // unsafe across objects; assign the same type then copy children.
                    target.managedReferenceValue = source.managedReferenceValue == null
                        ? null
                        : Activator.CreateInstance(source.managedReferenceValue.GetType());
                    if (source.managedReferenceValue != null)
                        goto case SerializedPropertyType.Generic;
                    break;

                case SerializedPropertyType.Gradient:
                    target.gradientValue = source.gradientValue;
                    break;

                default:
                    var value = SerializedPropertyUtils.GetSerializedPropertyValue(source);
                    SerializedPropertyUtils.SetSerializedPropertyValue(target, value);
                    break;
            }
        }

        private static bool HasAuxiliaryOfType(Step step, Type type)
        {
            return step.Auxiliaries.Any(a => a != null && a.GetType() == type);
        }

        #endregion
    }
}
