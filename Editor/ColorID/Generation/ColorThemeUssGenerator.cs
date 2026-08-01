#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.ColorID.Editor
{
    /// <summary>The result of a generation run.</summary>
    public readonly struct ColorThemeGenerationResult
    {
        /// <summary>Whether every variant was generated.</summary>
        public bool Success { get; }

        /// <summary>The manifest asset, or <c>null</c> on failure.</summary>
        public ColorThemeManifest Manifest { get; }

        /// <summary>Author-facing messages: what was written, or why nothing was.</summary>
        public IReadOnlyList<string> Messages { get; }

        internal ColorThemeGenerationResult(bool success, ColorThemeManifest manifest,
            IReadOnlyList<string> messages)
        {
            Success = success;
            Manifest = manifest;
            Messages = messages ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Generates one USS stylesheet per theme variant, plus a manifest recording what was generated.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Generation/</c>.
    /// <b>Shape:</b> editor-only static service, invoked explicitly.
    /// <para/>
    /// <b>Generation is an explicit action, never an <c>OnValidate</c> side effect.</b> That is a direct
    /// lesson from V1, whose palette <c>OnValidate</c> rewrote persisted overrides and recoloured every
    /// open scene as a side effect of a keystroke. Writing files during import or validation is the same
    /// class of mistake at a larger scale.
    /// <para/>
    /// Output is <b>deterministic</b>: tokens are emitted in sorted order and colours formatted
    /// culture-invariantly, so regenerating unchanged data produces a byte-identical file and version
    /// control shows nothing. The only non-deterministic field, the timestamp, lives in the manifest and
    /// is excluded from every freshness comparison.
    /// </remarks>
    public static class ColorThemeUssGenerator
    {
        /// <summary>Where generated theme output is written, relative to the project root.</summary>
        /// <remarks>
        /// Consumer space, never package source: a package is read-only to the projects that install it,
        /// and generated output belongs to whoever generated it.
        /// </remarks>
        public const string OutputRoot = "Assets/_MolcaSDK/Generated/Themes";

        /// <summary>Generates stylesheets and a manifest for every variant of a theme set.</summary>
        /// <param name="themeSet">The source theme set.</param>
        /// <returns>What was written, or why nothing was.</returns>
        public static ColorThemeGenerationResult Generate(ColorThemeSet themeSet)
        {
            var messages = new List<string>();

            if (themeSet == null)
            {
                messages.Add("No theme set supplied.");
                return new ColorThemeGenerationResult(false, null, messages);
            }

            if (string.IsNullOrEmpty(themeSet.StableSetId))
            {
                messages.Add($"Theme set '{themeSet.name}' has no stable set ID. Generated output is "
                             + "namespaced by it, so one must be assigned before generating.");
                return new ColorThemeGenerationResult(false, null, messages);
            }

            var validationErrors = new List<string>();
            if (!themeSet.Validate(validationErrors))
            {
                messages.Add($"Theme set '{themeSet.DisplayName}' is not valid; nothing was generated.");
                messages.AddRange(validationErrors);
                return new ColorThemeGenerationResult(false, null, messages);
            }

            string setDirectory = $"{OutputRoot}/{themeSet.StableSetId}";
            Directory.CreateDirectory(setDirectory);

            var variantEntries = new List<ColorThemeVariantStylesheet>();
            var fingerprints = new List<string>();
            int tokenCount = 0;
            bool success = true;

            try
            {
                // One import pass for every file, rather than one per file: importing a StyleSheet is
                // expensive, and a set with several variants would otherwise stall the editor.
                AssetDatabase.StartAssetEditing();

                foreach (string variantId in themeSet.GetVariantIds())
                {
                    var outcome = ColorThemeResolver.TryResolve(themeSet, variantId, 0, out var theme,
                        out var diagnostics);

                    if (outcome != ColorThemeActivation.Activated)
                    {
                        messages.Add($"Variant '{variantId}' did not resolve ({outcome}); skipped.");
                        messages.AddRange(diagnostics);
                        success = false;
                        continue;
                    }

                    string path = $"{setDirectory}/{variantId}.uss";
                    string contents = BuildStylesheet(themeSet, theme);

                    // Compare before writing so an unchanged variant does not churn the file's
                    // timestamp and trigger a needless reimport.
                    if (!File.Exists(path) || File.ReadAllText(path) != contents)
                    {
                        File.WriteAllText(path, contents, new UTF8Encoding(false));
                        messages.Add($"Wrote {path}");
                    }
                    else
                    {
                        messages.Add($"Unchanged {path}");
                    }

                    variantEntries.Add(new ColorThemeVariantStylesheet(variantId, null));
                    fingerprints.Add(theme.SourceFingerprint);
                    tokenCount = Mathf.Max(tokenCount, theme.TokenCount);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();

            // The StyleSheet assets only exist after the refresh above, so references are resolved in a
            // second pass rather than at write time.
            for (int i = 0; i < variantEntries.Count; i++)
            {
                string path = $"{setDirectory}/{variantEntries[i].VariantId}.uss";
                var stylesheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (stylesheet == null)
                {
                    messages.Add($"Wrote {path} but Unity did not import it as a StyleSheet.");
                    success = false;
                    continue;
                }
                variantEntries[i] = new ColorThemeVariantStylesheet(variantEntries[i].VariantId, stylesheet);
            }

            var manifest = LoadOrCreateManifest($"{setDirectory}/theme-manifest.asset");
            manifest.Populate(themeSet, ColorThemeUssGeneratorVersion.Current, variantEntries,
                fingerprints, tokenCount,
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();

            messages.Add($"Generated {variantEntries.Count} variant stylesheet(s), {tokenCount} tokens each.");
            return new ColorThemeGenerationResult(success, manifest, messages);
        }

        private static ColorThemeManifest LoadOrCreateManifest(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<ColorThemeManifest>(path);
            if (existing != null) return existing;

            var created = ScriptableObject.CreateInstance<ColorThemeManifest>();
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        /// <summary>Builds the USS text for one resolved variant.</summary>
        /// <param name="themeSet">The source set, named in the header comment.</param>
        /// <param name="theme">The resolved snapshot to export.</param>
        /// <returns>Deterministic USS content.</returns>
        /// <remarks>
        /// Every token in the snapshot is exported, primitives included. Excluding primitives would
        /// break an alias a hand-written USS rule might reasonably reference, and the size cost of a few
        /// dozen extra custom properties is nil.
        /// </remarks>
        public static string BuildStylesheet(ColorThemeSet themeSet, ResolvedColorTheme theme)
        {
            var builder = new StringBuilder();

            // No timestamp in the file: it would make every regeneration a diff.
            builder.Append("/* Generated by Molca ColorThemeUssGenerator v")
                .Append(ColorThemeUssGeneratorVersion.Current)
                .Append(". Do not edit — regenerate from the theme set.\n");
            builder.Append("   Theme set: ").Append(themeSet.DisplayName)
                .Append(" (").Append(themeSet.StableSetId).Append(")\n");
            builder.Append("   Variant:   ").Append(theme.VariantId).Append('\n');
            builder.Append("   Source fingerprint: ").Append(theme.SourceFingerprint).Append(" */\n\n");

            builder.Append('.').Append(ColorThemeUssNaming.ThemeClass).Append(" {\n");

            // GetTokenIds is sorted, which is what makes the output byte-stable.
            foreach (string tokenId in theme.GetTokenIds())
            {
                if (!theme.TryGetColor(tokenId, out Color color)) continue;

                builder.Append("    ")
                    .Append(ColorThemeUssNaming.ToVariableName(tokenId))
                    .Append(": ")
                    .Append(FormatColor(color))
                    .Append(";\n");
            }

            builder.Append("}\n");
            return builder.ToString();
        }

        /// <summary>Formats a colour as a USS literal.</summary>
        /// <remarks>
        /// Opaque colours use <c>#RRGGBB</c> and translucent ones <c>rgba()</c> with 8-bit channels and
        /// an invariant-culture alpha. Culture matters: a machine with a comma decimal separator would
        /// otherwise emit <c>rgba(0, 0, 0, 0,5)</c>, which is invalid USS and would fail only on that
        /// developer's machine.
        /// </remarks>
        private static string FormatColor(Color color)
        {
            byte r = ToByte(color.r), g = ToByte(color.g), b = ToByte(color.b);

            if (color.a >= 1f) return $"#{r:X2}{g:X2}{b:X2}";

            string alpha = Mathf.Clamp01(color.a).ToString("0.###", CultureInfo.InvariantCulture);
            return $"rgba({r}, {g}, {b}, {alpha})";
        }

        private static byte ToByte(float channel) =>
            (byte)Mathf.Clamp(Mathf.RoundToInt(channel * 255f), 0, 255);
    }
}
#endif
