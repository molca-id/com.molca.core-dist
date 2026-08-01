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
        /// <summary>Where the generated vocabulary asset is written.</summary>
        public const string AssetPath = "Assets/_MolcaSDK/Settings/Global/Themes/Molca Color Theme Set.asset";

        /// <summary>Creates or regenerates the vocabulary asset.</summary>
        [MenuItem("Molca/ColorID/Create or Update Colour Vocabulary Asset", priority = 40)]
        public static void CreateOrUpdate()
        {
            var built = ColorThemeVocabulary.Build();

            var errors = new List<string>();
            if (!built.Validate(errors))
            {
                Object.DestroyImmediate(built);
                Debug.LogError("[ColorTheme] The code-defined vocabulary does not validate; nothing was "
                               + $"written.\n  {string.Join("\n  ", errors)}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));

            var existing = AssetDatabase.LoadAssetAtPath<ColorThemeSet>(AssetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(built, AssetPath);
            }
            else
            {
                // Copy into the existing asset rather than replacing the file, so every reference to it —
                // ColorThemeSettings, a generated manifest, a serialized inspector — survives.
                EditorUtility.CopySerialized(built, existing);
                Object.DestroyImmediate(built);
                existing.InvalidateIndexes();
                EditorUtility.SetDirty(existing);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<ColorThemeSet>(AssetPath);
            Debug.Log($"[ColorTheme] Wrote '{AssetPath}': {asset.TokenDefinitions.Count} tokens, "
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
