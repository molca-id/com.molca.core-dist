using Molca.UI.Tokens;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.UI.Tokens
{
    /// <summary>
    /// Batch-mode entry points for the catalog colour migration, so a migration batch can be previewed and
    /// applied from CI or a headless run rather than only by hand.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/UI/Tokens/</c>.
    /// <b>Registration:</b> invoked with <c>-executeMethod</c>.
    /// <para/>
    /// Separate from <see cref="MolcaUiTokenCatalogMigration"/>'s menu items because those act on the
    /// Selection, which does not exist in batch mode. The path comes from
    /// <c>-molcaCatalogPath &lt;assetPath&gt;</c>; without it every catalog in the project is previewed,
    /// which is the useful default for a survey.
    /// <para/>
    /// Applying is a <i>separate</i> method from previewing on purpose: <c>-executeMethod</c> takes no
    /// arguments the caller can typo into a mutation, so "preview" and "write" cannot be confused for one
    /// another at the command line.
    /// </remarks>
    public static class MolcaUiTokenCatalogMigrationCli
    {
        private const string PathArgument = "-molcaCatalogPath";

        /// <summary>Previews every targeted catalog. Writes nothing.</summary>
        public static void Preview()
        {
            foreach (var catalog in Targets())
            {
                Debug.Log(MolcaUiTokenCatalogMigration.Plan(catalog).ToPreview(), catalog);
            }
        }

        /// <summary>Previews and then applies, keeping the legacy pairs.</summary>
        public static void Apply()
        {
            foreach (var catalog in Targets())
            {
                var plan = MolcaUiTokenCatalogMigration.Plan(catalog);
                Debug.Log(plan.ToPreview(), catalog);

                if (!plan.IsValid)
                {
                    Debug.LogError($"[Molca UI] Refusing to migrate '{catalog.name}'.", catalog);
                    continue;
                }

                int applied = MolcaUiTokenCatalogMigration.Apply(plan);
                Debug.Log($"[Molca UI] Migrated {applied} colour entry/entries in '{catalog.name}'; "
                          + $"{plan.BlockedCount} left on the legacy pair.", catalog);
            }
        }

        private static MolcaUiTokenCatalog[] Targets()
        {
            string requested = ReadPathArgument();

            if (!string.IsNullOrEmpty(requested))
            {
                var single = AssetDatabase.LoadAssetAtPath<MolcaUiTokenCatalog>(requested);
                if (single == null)
                {
                    Debug.LogError($"[Molca UI] No UI Token Catalog at '{requested}'.");
                    return new MolcaUiTokenCatalog[0];
                }
                return new[] { single };
            }

            var guids = AssetDatabase.FindAssets("t:MolcaUiTokenCatalog");
            var found = new MolcaUiTokenCatalog[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                found[i] = AssetDatabase.LoadAssetAtPath<MolcaUiTokenCatalog>(
                    AssetDatabase.GUIDToAssetPath(guids[i]));
            }

            if (found.Length == 0) Debug.LogWarning("[Molca UI] This project has no UI Token Catalog.");
            return found;
        }

        private static string ReadPathArgument()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == PathArgument) return args[i + 1];
            }
            return null;
        }
    }
}
