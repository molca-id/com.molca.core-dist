#if UNITY_EDITOR
using System.IO;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Menu entries for exporting and importing the colour-theme interchange format.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Interchange/</c>.
    /// <b>Registration:</b> <c>[MenuItem]</c>.
    /// <para/>
    /// Import is two separate commands, not one command with a confirmation dialog. A preview an author can
    /// read at their own pace, copy out of the console, and compare against a file is worth more than a
    /// modal they will dismiss — and it makes "I looked at what this would do" a step that leaves a trace.
    /// </remarks>
    public static class ColorThemeInterchangeMenu
    {
        private const string ExportPreference = "Molca.ColorTheme.LastInterchangePath";

        /// <summary>Exports the project's installed theme set to a JSON file.</summary>
        [MenuItem("Molca/ColorID/Export Colour Theme (JSON)…", priority = 80)]
        private static void Export()
        {
            var themeSet = Resolve(out string error);
            if (themeSet == null)
            {
                Debug.LogError(error);
                return;
            }

            string suggested = $"{Sanitize(themeSet.DisplayName)}.tokens.json";
            string path = EditorUtility.SaveFilePanel("Export colour theme",
                EditorPrefs.GetString(ExportPreference, Application.dataPath), suggested, "json");
            if (string.IsNullOrEmpty(path)) return;

            var document = ColorThemeInterchangeExporter.Build(themeSet);
            File.WriteAllText(path, ColorThemeInterchangeExporter.ToJson(document, themeSet));
            EditorPrefs.SetString(ExportPreference, Path.GetDirectoryName(path));

            Debug.Log($"[ColorTheme] Exported {document.Tokens.Count} token(s) across "
                      + $"{document.Variants.Count} variant(s) to '{path}'.");
        }

        /// <summary>Reads a JSON file and logs what importing it would change. Writes nothing.</summary>
        [MenuItem("Molca/ColorID/Preview Colour Theme Import (JSON)…", priority = 81)]
        private static void PreviewImport()
        {
            var plan = BuildImportPlan();
            if (plan != null) Debug.Log(plan.ToPreview());
        }

        /// <summary>Reads a JSON file, logs the preview, then applies it.</summary>
        [MenuItem("Molca/ColorID/Import Colour Theme (JSON)…", priority = 82)]
        private static void Import()
        {
            var plan = BuildImportPlan();
            if (plan == null) return;

            Debug.Log(plan.ToPreview());

            if (!plan.IsValid)
            {
                Debug.LogError("[ColorTheme] The import was refused; nothing was written.");
                return;
            }

            if (ColorThemeInterchangeImporter.Apply(plan, out string error))
            {
                Debug.Log($"[ColorTheme] Imported into '{plan.Target.DisplayName}'. "
                          + $"{plan.Changes.Count} change(s) applied.", plan.Target);
                return;
            }

            Debug.LogError($"[ColorTheme] Import failed: {error}");
        }

        private static ColorThemeImportPlan BuildImportPlan()
        {
            var themeSet = Resolve(out string error);
            if (themeSet == null)
            {
                Debug.LogError(error);
                return null;
            }

            string path = EditorUtility.OpenFilePanel("Import colour theme",
                EditorPrefs.GetString(ExportPreference, Application.dataPath), "json");
            if (string.IsNullOrEmpty(path)) return null;

            EditorPrefs.SetString(ExportPreference, Path.GetDirectoryName(path));

            var document = ColorThemeInterchangeImporter.Parse(File.ReadAllText(path), out var parseErrors);
            if (document == null)
            {
                Debug.LogError($"[ColorTheme] Could not read '{path}':\n  "
                               + string.Join("\n  ", parseErrors));
                return null;
            }

            return ColorThemeInterchangeImporter.Plan(document, themeSet);
        }

        private static ColorThemeSet Resolve(out string error)
        {
            error = null;

            if (Selection.activeObject is ColorThemeSet selected) return selected;

            var installed = ColorThemeAuditService.FindThemeSettings()?.ThemeSet;
            if (installed != null) return installed;

            error = "[ColorTheme] Select a Color Theme Set asset, or install a ColorThemeSettings module "
                    + "so the project's own set can be used.";
            return null;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "colour-theme";

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(System.Array.IndexOf(invalid, c) >= 0 ? '-' : c);
            }
            return builder.ToString();
        }
    }
}
#endif
