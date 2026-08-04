using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// The two ways a package's content is chosen: pick existing Addressables labels, or bind a whole
    /// group and let its name become the label.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> called from <see cref="ContentPackageDetailView"/>'s Content card.
    /// <para>
    /// <b>Every project has Addressables until it does not.</b> A project can legitimately have no
    /// Addressables settings object — a fresh project, or one that has not opened the Groups window yet
    /// — and the API returns null rather than an empty set. So <see cref="KnownLabels"/> distinguishes
    /// "no labels" from "cannot know", and the callers say which one they are looking at instead of
    /// showing every authored label as broken.
    /// </para>
    /// <para>
    /// <b>Binding a group is not undoable, and says so.</b> Adding the label to the package config goes
    /// through the editing service and is one Undo step; stamping that label onto the group's entries is
    /// an Addressables mutation that Undo does not cleanly carry, which is why it is confirmed first and
    /// logged after.
    /// </para>
    /// </remarks>
    internal static class ContentLabelPicker
    {
        /// <summary>
        /// Every label Addressables knows about, or null when Addressables is not configured.
        /// </summary>
        /// <returns>The label set, or null.</returns>
        public static HashSet<string> KnownLabels()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return null;

            return new HashSet<string>(settings.GetLabels() ?? new List<string>(), System.StringComparer.Ordinal);
        }

        /// <summary>Shows the label picker for one package, toggling each label on selection.</summary>
        /// <param name="context">The workspace context.</param>
        /// <param name="packageId">The package to edit.</param>
        public static void ShowLabels(ContentWorkspaceContext context, string packageId)
        {
            var known = KnownLabels();
            var menu = new GenericMenu();

            if (known == null)
            {
                menu.AddDisabledItem(new GUIContent("Addressables is not configured"));
                menu.ShowAsContext();
                return;
            }

            var config = context.Settings.GetPackageConfig(packageId);
            var selected = new HashSet<string>(
                config?.addressableLabels ?? System.Array.Empty<string>(), System.StringComparer.Ordinal);

            foreach (string label in known.OrderBy(entry => entry, System.StringComparer.Ordinal))
            {
                string captured = label;
                bool on = selected.Contains(label);

                menu.AddItem(new GUIContent(label), on, () => context.ApplyPackageEdit(
                    on ? context.Editing.RemoveLabel(packageId, captured)
                       : context.Editing.AddLabel(packageId, captured)));
            }

            // Labels the package declares that Addressables no longer has. Offered so they can be
            // removed from the same menu that added them — otherwise the only way to drop a renamed
            // label is the row's own Remove, and a reader looking at this list would not know it is
            // incomplete.
            foreach (string orphan in selected.Where(label => !known.Contains(label))
                         .OrderBy(entry => entry, System.StringComparer.Ordinal))
            {
                string captured = orphan;
                menu.AddItem(new GUIContent($"{orphan}  (not in catalog)"), true,
                    () => context.ApplyPackageEdit(context.Editing.RemoveLabel(packageId, captured)));
            }

            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent("No labels defined in Addressables"));

            menu.ShowAsContext();
        }

        /// <summary>Shows the group picker for one package, binding a group as a label on selection.</summary>
        /// <param name="context">The workspace context.</param>
        /// <param name="packageId">The package to edit.</param>
        public static void ShowGroups(ContentWorkspaceContext context, string packageId)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var menu = new GenericMenu();

            if (settings == null)
            {
                menu.AddDisabledItem(new GUIContent("Addressables is not configured"));
                menu.ShowAsContext();
                return;
            }

            var config = context.Settings.GetPackageConfig(packageId);
            var selected = new HashSet<string>(
                config?.addressableLabels ?? System.Array.Empty<string>(), System.StringComparer.Ordinal);

            var groups = settings.groups
                .Where(group => group != null && !group.IsDefaultGroup())
                .OrderBy(group => group.Name, System.StringComparer.Ordinal)
                .ToList();

            foreach (var group in groups)
            {
                var captured = group;
                string label = group.Name;
                bool on = selected.Contains(label);

                menu.AddItem(new GUIContent(group.Name), on, () =>
                {
                    if (on)
                    {
                        // Unbinding drops the label from this package only. Stripping it from the
                        // group's entries would silently change what every other package claiming that
                        // label ships.
                        context.ApplyPackageEdit(context.Editing.RemoveLabel(packageId, label));
                        return;
                    }

                    BindGroup(context, packageId, label, captured, settings);
                });
            }

            if (groups.Count == 0)
                menu.AddDisabledItem(new GUIContent("No non-default groups found"));

            menu.ShowAsContext();
        }

        /// <summary>
        /// Creates the label if needed, stamps it onto the group's entries, then adds it to the package.
        /// </summary>
        private static void BindGroup(
            ContentWorkspaceContext context,
            string packageId,
            string label,
            AddressableAssetGroup group,
            AddressableAssetSettings settings)
        {
            var unstamped = group.entries
                .Where(entry => entry != null && !entry.labels.Contains(label))
                .ToList();

            if (unstamped.Count > 0 && !EditorUtility.DisplayDialog(
                    "Bind group",
                    $"Add the label '{label}' to {unstamped.Count} entr" +
                    $"{(unstamped.Count == 1 ? "y" : "ies")} in the Addressables group '{group.Name}', " +
                    $"then give it to package '{packageId}'?\n\n" +
                    "Stamping labels onto Addressables entries cannot be undone with Ctrl+Z.",
                    "Bind", "Cancel"))
            {
                return;
            }

            if (!settings.GetLabels().Contains(label))
            {
                settings.AddLabel(label);
                Debug.Log($"[ContentPackage] Created Addressables label '{label}' for group '{group.Name}'.");
            }

            foreach (var entry in unstamped) entry.SetLabel(label, true, postEvent: false);

            if (unstamped.Count > 0)
            {
                EditorUtility.SetDirty(settings);
                Debug.Log($"[ContentPackage] Stamped '{label}' onto {unstamped.Count} entr" +
                          $"{(unstamped.Count == 1 ? "y" : "ies")} in group '{group.Name}'.");
            }

            context.ApplyPackageEdit(context.Editing.AddLabel(packageId, label));
        }
    }
}
