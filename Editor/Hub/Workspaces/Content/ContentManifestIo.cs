using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Molca.ContentPackage;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// Reads a package definition out of a JSON manifest, and writes the settings asset back out.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> called from <see cref="ContentDeliveryView"/>'s Tools card.
    /// <para>
    /// <b>Import goes through the editing service, one setter at a time.</b> The inspector's version
    /// built a <see cref="ContentPackageSettings.PackageConfig"/> and pushed it onto the list itself,
    /// which meant a manifest could introduce a package that skipped every refusal the service makes —
    /// including the read-only check, so importing into a settings asset inside a package silently
    /// produced a definition an upgrade would discard.
    /// </para>
    /// <para>
    /// An overwrite is a remove-then-add rather than a field-by-field patch, because a manifest
    /// describes a whole package: patching would leave labels and dependencies from the old definition
    /// that the file being imported does not mention, which is the opposite of what "import this
    /// manifest" means.
    /// </para>
    /// </remarks>
    internal static class ContentManifestIo
    {
        /// <summary>The subset of a package definition a manifest file carries.</summary>
        [Serializable]
        private class ImportableManifest
        {
            public string packageId;
            public string displayName;
            public string version = "1.0.0";
            public string description;
            public string author;
            public bool isRequired;
            public string[] addressableLabels;
            public string[] dependencies;
        }

        /// <summary>
        /// Prompts for a manifest file and imports the package it describes.
        /// </summary>
        /// <param name="context">The workspace context, which owns the write path.</param>
        /// <returns>The imported package id, or null when nothing was imported.</returns>
        public static string Import(ContentWorkspaceContext context)
        {
            string path = EditorUtility.OpenFilePanel("Import Package Manifest", "", "json");
            if (string.IsNullOrEmpty(path)) return null;

            ImportableManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<ImportableManifest>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("Import failed", exception.Message, "OK");
                Debug.LogError($"[ContentPackage] Manifest import failed: {exception}");
                return null;
            }

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.packageId))
            {
                EditorUtility.DisplayDialog("Import failed", "Missing or empty packageId in the JSON.", "OK");
                return null;
            }

            string packageId = manifest.packageId.Trim();

            if (context.Settings.GetPackageConfig(packageId) != null)
            {
                if (!EditorUtility.DisplayDialog("Package exists",
                        $"'{packageId}' already exists. Replace its definition with the manifest's?\n\n" +
                        "Labels and dependencies the manifest does not mention are dropped.",
                        "Replace", "Cancel"))
                {
                    return null;
                }

                var removal = context.Editing.RemovePackage(packageId);
                if (!removal.Changed)
                {
                    EditorUtility.DisplayDialog("Import failed", removal.Message, "OK");
                    return null;
                }
            }

            int group = Undo.GetCurrentGroup();

            var added = context.Editing.AddPackage(packageId);
            if (!added.Changed)
            {
                EditorUtility.DisplayDialog("Import failed", added.Message, "OK");
                return null;
            }

            var notes = new List<string>();
            Apply(notes, context.Editing.SetDisplayName(packageId, manifest.displayName ?? packageId));
            Apply(notes, context.Editing.SetVersion(packageId, manifest.version));
            Apply(notes, context.Editing.SetDescription(packageId, manifest.description ?? ""));
            Apply(notes, context.Editing.SetAuthor(packageId, manifest.author ?? ""));
            Apply(notes, context.Editing.SetRequired(packageId, manifest.isRequired));
            Apply(notes, context.Editing.SetLabels(packageId, manifest.addressableLabels ?? Array.Empty<string>()));
            Apply(notes, context.Editing.SetDependencies(packageId, manifest.dependencies ?? Array.Empty<string>()));

            // One import is one action to whoever ran it, so the eight service records collapse into a
            // single Ctrl+Z rather than eight.
            Undo.SetCurrentGroupName("Import Content Manifest");
            Undo.CollapseUndoOperations(group);

            context.ApplyPackageEdit(added);

            string detail =
                $"Imported '{packageId}'.\n\n" +
                $"Labels: {manifest.addressableLabels?.Length ?? 0}    " +
                $"Dependencies: {manifest.dependencies?.Length ?? 0}";

            if (notes.Count > 0) detail += "\n\n" + string.Join("\n", notes);

            EditorUtility.DisplayDialog("Import complete", detail, "OK");
            return packageId;
        }

        /// <summary>Writes the whole settings asset out as JSON.</summary>
        /// <param name="settings">The asset to export.</param>
        public static void Export(ContentPackageSettings settings)
        {
            if (settings == null) return;

            string path = EditorUtility.SaveFilePanel(
                "Export Settings", "", "ContentPackageSettings.json", "json");
            if (string.IsNullOrEmpty(path)) return;

            File.WriteAllText(path, JsonUtility.ToJson(settings, true));
            EditorUtility.DisplayDialog("Export complete", $"Saved to:\n{path}", "OK");
        }

        /// <summary>Collects anything a setter refused or warned about, so the dialog can show it.</summary>
        private static void Apply(List<string> notes, Molca.ContentPackage.Editor.ContentEditResult result)
        {
            if (result == null) return;

            // A manifest that repeats a default lands on "already that" for several fields; only a
            // refusal or a consequence worth knowing about is worth a line in the dialog.
            if (!result.Changed && !result.Message.EndsWith("is already that.", StringComparison.Ordinal))
                notes.Add(result.Message);
            else if (result.Changed && result.Message.Contains("blocks publishing"))
                notes.Add(result.Message);
        }
    }
}
