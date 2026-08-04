using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.UI.Components;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>
    /// Edits the project's declared scene load sets from inside the workspace.
    /// </summary>
    /// <remarks>
    /// <para>Load sets decide every cross-scene finding, and until now the only way to author one was to
    /// hand-write <c>ProjectSettings/MolcaReferenceLoadSets.json</c> — which the Coverage view politely told
    /// you to do. A configuration surface that names a file instead of offering a control is a surface
    /// nobody uses, and an unauthored load set means every cross-scene reference is validated against a
    /// guess.</para>
    ///
    /// <para>Not a repair plan, because a load set is a statement about the project rather than a change to
    /// its assets: nothing about a reference moves when one is written. It is committed, though, and shared
    /// with CI, so the editor says so and writes through <see cref="ReferenceLoadSetStore.Save"/> rather
    /// than touching the file.</para>
    /// </remarks>
    internal sealed class ReferenceHubLoadSetEditor : VisualElement
    {
        private readonly Action _onChanged;
        private readonly List<Draft> _drafts = new List<Draft>();

        /// <summary>A load set being edited, kept mutable so the fields can write straight into it.</summary>
        private sealed class Draft
        {
            internal string Id;
            internal string EntryScene;
            internal readonly List<string> Concurrent = new List<string>();
            internal readonly List<string> Deferred = new List<string>();

            internal ReferenceLoadSet ToLoadSet() =>
                new ReferenceLoadSet(Id, EntryScene, Concurrent, Deferred, isInferred: false);
        }

        /// <summary>Builds the editor over the sets currently in force.</summary>
        /// <param name="onChanged">Invoked after a save, so the workspace can re-read and re-render.</param>
        internal ReferenceHubLoadSetEditor(Action onChanged)
        {
            _onChanged = onChanged;
            AddToClassList("molca-references__loadsets");

            // Seeded from whatever is in force, inferred included: turning the guess into an authored set is
            // the most common first edit, and retyping it by hand is the reason it never happens.
            foreach (var set in ReferenceLoadSetStore.Sets)
            {
                var draft = new Draft { Id = set.IsInferred ? "default" : set.Id, EntryScene = set.EntryScene };
                draft.Concurrent.AddRange(set.ConcurrentScenes);
                draft.Deferred.AddRange(set.DeferredScenes);
                _drafts.Add(draft);
            }

            Rebuild();
        }

        private void Rebuild()
        {
            Clear();

            for (var index = 0; index < _drafts.Count; index++)
                Add(BuildDraft(_drafts[index], index));

            var actions = new VisualElement();
            actions.AddToClassList("molca-references__actions");
            Add(actions);

            actions.Add(MolcaButtons.Mini("Add load set", () =>
            {
                _drafts.Add(new Draft { Id = $"set-{_drafts.Count + 1}", EntryScene = FirstBuildScene() });
                Rebuild();
            }));

            var save = MolcaButtons.Primary($"Save {ReferenceLoadSetStore.FilePath}", Save);
            save.tooltip =
                "Write these sets to ProjectSettings, which is committed. Every developer and CI will "
                + "validate cross-scene references against exactly this.";
            actions.Add(save);

            if (!ReferenceLoadSetStore.IsInferred)
            {
                actions.Add(MolcaButtons.Mini("Revert to inferred", () =>
                {
                    if (!EditorUtility.DisplayDialog(
                            "Discard Authored Load Sets?",
                            "Cross-scene validation will fall back to a set inferred from build settings, "
                            + "which treats every enabled scene after the first as deferred. Findings that "
                            + "depend on your authored sets will disappear.",
                            "Discard", "Cancel"))
                        return;

                    if (ReferenceLoadSetStore.Save(Array.Empty<ReferenceLoadSet>()))
                        _onChanged?.Invoke();
                }));
            }
        }

        private VisualElement BuildDraft(Draft draft, int index)
        {
            var block = new VisualElement();
            block.AddToClassList("molca-references__loadset");
            block.Add(TextRow("Id", draft.Id, value => draft.Id = value));
            block.Add(SceneRow("Entry scene", draft.EntryScene, value => draft.EntryScene = value));

            block.Add(SceneList(
                "Concurrent", draft.Concurrent,
                "Loaded together with the entry scene. A reference into one of these is Available."));
            block.Add(SceneList(
                "Deferred", draft.Deferred,
                "May arrive later during play. A reference into one of these must tolerate a wait."));

            var remove = MolcaButtons.Mini("Remove set", () =>
            {
                _drafts.RemoveAt(index);
                Rebuild();
            });
            block.Add(remove);

            return block;
        }

        private VisualElement SceneList(string label, List<string> scenes, string tooltip)
        {
            var group = new VisualElement();
            group.AddToClassList("molca-references__loadset-group");

            var heading = new Label($"{label} ({scenes.Count})") { tooltip = tooltip };
            heading.AddToClassList("molca-references__field-title");
            group.Add(heading);

            for (var index = 0; index < scenes.Count; index++)
            {
                var captured = index;
                var row = new VisualElement();
                row.AddToClassList("molca-references__loadset-row");
                row.Add(SceneField(scenes[captured], value => scenes[captured] = value));
                row.Add(MolcaButtons.Mini("–", () =>
                {
                    scenes.RemoveAt(captured);
                    Rebuild();
                }));
                group.Add(row);
            }

            group.Add(MolcaButtons.Mini($"Add {label.ToLowerInvariant()} scene", () =>
            {
                scenes.Add(string.Empty);
                Rebuild();
            }));

            return group;
        }

        private static VisualElement TextRow(string label, string value, Action<string> onChanged)
        {
            var field = new TextField(label) { value = value ?? string.Empty };
            field.AddToClassList("molca-references__loadset-field");
            field.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            return field;
        }

        private static VisualElement SceneRow(string label, string value, Action<string> onChanged)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-references__loadset-row");

            var caption = new Label(label);
            caption.AddToClassList("molca-references__field-key");
            row.Add(caption);
            row.Add(SceneField(value, onChanged));
            return row;
        }

        /// <summary>
        /// An object field over the scene asset, which writes the path.
        /// </summary>
        /// <remarks>
        /// A path typed by hand is a path that silently stops matching after a scene is moved, and a load set
        /// naming a scene that no longer exists validates nothing while looking configured. Picking the asset
        /// makes that class of drift visible immediately — the field goes empty.
        /// </remarks>
        private static VisualElement SceneField(string scenePath, Action<string> onChanged)
        {
            var field = new ObjectField
            {
                objectType = typeof(SceneAsset),
                allowSceneObjects = false,
                value = string.IsNullOrEmpty(scenePath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath),
            };
            field.AddToClassList("molca-references__loadset-field");
            field.RegisterValueChangedCallback(evt =>
                onChanged(evt.newValue == null ? string.Empty : AssetDatabase.GetAssetPath(evt.newValue)));
            return field;
        }

        private void Save()
        {
            var sets = _drafts
                .Where(d => !string.IsNullOrWhiteSpace(d.EntryScene))
                .Select(d => d.ToLoadSet())
                .ToList();

            var dropped = _drafts.Count - sets.Count;
            if (dropped > 0)
            {
                // A set with no entry scene cannot say when anything is loaded, so writing it would add a
                // rule that matches nothing while appearing in the list as configuration.
                Debug.LogWarning(
                    $"[ReferenceLoadSets] {dropped} set(s) have no entry scene and were not written. A load "
                    + "set is defined by the scene the others join.");
            }

            var duplicateIds = sets.GroupBy(s => s.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIds.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    "Duplicate Load Set Ids",
                    $"These ids are used more than once: {string.Join(", ", duplicateIds)}.\n\n"
                    + "Findings name the set they came from, so two sets sharing an id make a report "
                    + "impossible to act on.",
                    "OK");
                return;
            }

            if (!ReferenceLoadSetStore.Save(sets))
                return;

            ReferenceAuditService.Invalidate("the scene load sets changed");
            _onChanged?.Invoke();
        }

        private static string FirstBuildScene() =>
            EditorBuildSettings.scenes.FirstOrDefault(s => s != null && s.enabled)?.path ?? string.Empty;
    }
}
