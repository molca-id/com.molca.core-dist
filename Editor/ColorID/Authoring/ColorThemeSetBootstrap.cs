#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Writes the canonical <see cref="ColorThemeVocabulary"/> to a project asset.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Authoring/</c>.
    /// <b>Registration:</b> <c>[MenuItem]</c>.
    /// <para/>
    /// The vocabulary lives in code and the asset is generated from it, rather than the asset being
    /// hand-authored. That keeps the mapping rationale next to the mapping, makes the whole contract
    /// reviewable as a diff, and lets the tests assert the vocabulary against the V1 baseline without an
    /// asset existing at all.
    /// <para/>
    /// Regenerating replaces the token contract wholesale. It does <b>not</b> touch
    /// <c>ColorThemeSettings</c> or any content — installing the module and pointing it at this asset is a
    /// deliberate, separate step, because that is the switch that moves a project from V1 to V2.
    /// </remarks>
    public static class ColorThemeSetBootstrap
    {
        /// <summary>File name of the generated vocabulary asset.</summary>
        public const string AssetFileName = "Molca Color Theme Set.asset";

        /// <summary>
        /// The project's vocabulary asset, wherever it lives — or where one would be created.
        /// </summary>
        /// <remarks>
        /// Located by type rather than by a fixed path. A consumer who imports the Starter Project Content
        /// sample receives this asset under <c>Assets/Samples/…</c>, which a hardcoded
        /// <c>Assets/_MolcaSDK/…</c> constant would never find — Core would create a second, blank one and
        /// configure that instead. Returns <c>null</c> when the project holds several, since this writes to
        /// whichever it picks.
        /// </remarks>
        public static string ResolveAssetPath(out string ambiguity) =>
            ColorThemeAssetLocator.ResolveOrDefault<ColorThemeSet>(AssetFileName, out ambiguity);

        /// <summary>Creates or regenerates the vocabulary asset.</summary>
        [MenuItem("Molca/ColorID/Create or Update Colour Vocabulary Asset", priority = 40)]
        public static void CreateOrUpdate()
        {
            var assetPath = ResolveAssetPath(out var ambiguity);
            if (assetPath == null)
            {
                Debug.LogError($"[ColorTheme] {ambiguity}");
                return;
            }

            var built = ColorThemeVocabulary.Build();

            var errors = new List<string>();
            if (!built.Validate(errors))
            {
                Object.DestroyImmediate(built);
                Debug.LogError("[ColorTheme] The code-defined vocabulary does not validate; nothing was "
                               + $"written.\n  {string.Join("\n  ", errors)}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));

            var existing = AssetDatabase.LoadAssetAtPath<ColorThemeSet>(assetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(built, assetPath);
            }
            else
            {
                // Copy into the existing asset rather than replacing the file, so every reference to it —
                // ColorThemeSettings, a generated manifest, a serialized inspector — survives.
                //
                // CopySerialized copies m_Name as well, and `built` is an in-memory CreateInstance with
                // none, so the name has to be put back or every regeneration silently blanks it. The blank
                // does not break resolution, which is why it went unnoticed, but it leaves the asset
                // unnamed in the Inspector and unfindable by name.
                string existingName = existing.name;
                EditorUtility.CopySerialized(built, existing);
                existing.name = existingName;
                Object.DestroyImmediate(built);
                existing.InvalidateIndexes();
                EditorUtility.SetDirty(existing);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<ColorThemeSet>(assetPath);
            Debug.Log($"[ColorTheme] Wrote '{assetPath}': {asset.TokenDefinitions.Count} tokens, "
                      + $"{asset.Variants.Count} variants, {asset.LegacyAliases.Count} legacy aliases, "
                      + $"{asset.AccessibilityRequirements.Count} contrast requirements.\n"
                      + "Next: add a ColorThemeSettings module to GlobalSettings and point it at this "
                      + "asset. That is the step that switches the project from V1 to V2.");
        }

        /// <summary>Reports how the vocabulary measures up, without writing anything.</summary>
        [MenuItem("Molca/ColorID/Report Colour Vocabulary Contrast", priority = 41)]
        public static void ReportContrast()
        {
            var built = ColorThemeVocabulary.Build();
            try
            {
                var report = new System.Text.StringBuilder("[ColorTheme] Contrast report\n");

                foreach (string variantId in built.GetVariantIds())
                {
                    if (ColorThemeResolver.TryResolve(built, variantId, 0, out var theme, out var diag)
                        != ColorThemeActivation.Activated)
                    {
                        report.AppendLine($"  {variantId}: did not resolve — {string.Join("; ", diag)}");
                        continue;
                    }

                    report.AppendLine($"  {variantId}:");
                    foreach (var result in ColorThemeResolver.EvaluateContrast(built, theme))
                        report.AppendLine($"    {result}");
                }

                Debug.Log(report.ToString());
            }
            finally
            {
                Object.DestroyImmediate(built);
            }
        }
    }
}
#endif
