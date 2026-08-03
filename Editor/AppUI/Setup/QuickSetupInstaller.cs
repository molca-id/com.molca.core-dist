#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Molca.App.Editor.Setup
{
    /// <summary>
    /// Copies the package's quick-setup starter settings into the consumer project's
    /// <c>Assets/_MolcaSDK/Settings/</c> so a fresh project has a working <c>GlobalSettings</c>
    /// graph, input actions and lighting to bootstrap from.
    /// </summary>
    /// <remarks>
    /// The starter assets ship as immutable package templates under
    /// <c>Packages/com.molca.core/Samples~/QuickSetup/Settings/</c>. That folder ends in <c>~</c>, so
    /// Unity never imports it and its GUIDs never enter the project's asset database until they are
    /// copied. The copy is one-way (template → consumer space) and <b>idempotent</b>: existing
    /// consumer files are left untouched unless overwrite is explicitly requested. The package
    /// templates are never written back to.
    /// <para/>
    /// <b>The colour palette templates deliberately carry the same GUIDs as the palettes the
    /// packaged <c>Runtime Manager</c> prefab references.</b> That prefab serializes its
    /// <c>Available Schemes</c> array by GUID, so if the installed palettes landed under fresh
    /// GUIDs the fresh-install case would deserialize that array as all-null and theme switching
    /// would be dead on arrival. Matching GUIDs is what closes that reference. It cannot collide
    /// with an existing project asset, because a project that already has those palettes also
    /// already has the files — and existing files are skipped.
    /// <para/>
    /// The remaining template GUIDs are disjoint from any active settings and carry no such
    /// constraint. Nothing outside the templates references them.
    /// <para/>
    /// This is the supported install path for the held-back bootstrap config documented in
    /// <c>SDK_PACKAGING.md</c> ("Quick setup templates"); full first-run wizard polish is tracked
    /// separately in the onboarding sprint. Editor-only; runs on the main thread.
    /// </remarks>
    public static class QuickSetupInstaller
    {
        /// <summary>Package-relative path to the immutable starter-settings templates.</summary>
        private const string TemplateRoot = "Packages/com.molca.core/Samples~/QuickSetup/Settings";

        /// <summary>Consumer-space destination the active settings are copied into.</summary>
        private const string DestinationRoot = "Assets/_MolcaSDK/Settings";

        /// <summary>
        /// Copies starter settings into the project, keeping any files that already exist.
        /// </summary>
        [MenuItem("Molca/SDK/Quick Setup/Install Starter Settings", priority = 10)]
        public static void InstallKeepingExisting() => Install(overwriteExisting: false);

        /// <summary>
        /// Copies starter settings into the project, overwriting files that already exist.
        /// Use this to repair a partially-set-up project; it never touches the package templates.
        /// </summary>
        [MenuItem("Molca/SDK/Quick Setup/Repair (Overwrite) Starter Settings", priority = 11)]
        public static void InstallOverwriting()
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "Repair Starter Settings",
                $"Overwrite any existing files under {DestinationRoot} with the package starter " +
                "templates? Local edits to those settings will be lost.",
                "Overwrite", "Cancel");
            if (confirmed)
            {
                Install(overwriteExisting: true);
            }
        }

        /// <summary>
        /// Copies every template file under <see cref="TemplateRoot"/> into <see cref="DestinationRoot"/>,
        /// preserving the relative folder layout.
        /// </summary>
        /// <param name="overwriteExisting">
        /// When <c>false</c>, files that already exist in consumer space are skipped (idempotent install).
        /// When <c>true</c>, existing files are replaced from the templates.
        /// </param>
        private static void Install(bool overwriteExisting)
        {
            // Samples~ is hidden from the asset database, so resolve the physical path and use
            // System.IO rather than AssetDatabase APIs to read the templates.
            string templateDir = Path.GetFullPath(TemplateRoot);
            if (!Directory.Exists(templateDir))
            {
                Debug.LogError($"[QuickSetup] Starter templates not found at '{TemplateRoot}'. " +
                               "Is com.molca.core installed correctly?");
                return;
            }

            string destDir = Path.GetFullPath(DestinationRoot);
            int copied = 0, skipped = 0;

            foreach (string srcPath in Directory.GetFiles(templateDir, "*", SearchOption.AllDirectories))
            {
                string relative = srcPath.Substring(templateDir.Length).TrimStart(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string destPath = Path.Combine(destDir, relative);

                if (File.Exists(destPath) && !overwriteExisting)
                {
                    skipped++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                File.Copy(srcPath, destPath, overwrite: true);
                copied++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[QuickSetup] Starter settings installed into {DestinationRoot} " +
                      $"({copied} copied, {skipped} kept). Wire GlobalSettings into MolcaProjectSettings to finish setup.");
        }
    }
}
#endif
