#if UNITY_EDITOR
using System.Collections.Generic;
using Molca.ColorID;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Writes a <see cref="ColorThemeSet"/> to the JSON interchange format.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Interchange/</c>.
    /// <b>Shape:</b> editor-only static service. Reads only; never touches the asset.
    /// <para/>
    /// <b>Deterministic.</b> Tokens and variants keep their authored order, dictionaries are written in that
    /// order rather than hash order, floats use the invariant culture, and nothing carries a timestamp or a
    /// machine name. Two exports of unchanged data are byte-identical, so the output can be committed and
    /// diffed — which is most of the point of having an interchange format at all.
    /// </remarks>
    public static class ColorThemeInterchangeExporter
    {
        /// <summary>Builds an interchange document from a theme set.</summary>
        /// <param name="themeSet">The set to export.</param>
        /// <param name="defaultVariantId">
        /// The variant whose resolved colour becomes each token's DTCG <c>$value</c>. Blank uses the first
        /// declared variant.
        /// </param>
        /// <returns>The document, or <c>null</c> when there is nothing to export.</returns>
        public static ColorThemeInterchangeDocument Build(ColorThemeSet themeSet,
            string defaultVariantId = null)
        {
            if (themeSet == null) return null;

            var document = new ColorThemeInterchangeDocument
            {
                SetId = themeSet.StableSetId,
                DisplayName = themeSet.DisplayName,
                SchemaVersion = themeSet.SchemaVersion
            };

            var variantIds = themeSet.GetVariantIds();
            document.DefaultVariantId = !string.IsNullOrEmpty(defaultVariantId)
                                        && themeSet.GetVariant(defaultVariantId) != null
                ? themeSet.GetVariant(defaultVariantId).Id
                : variantIds.Length > 0 ? variantIds[0] : null;

            foreach (var variant in themeSet.Variants)
            {
                if (variant == null || string.IsNullOrEmpty(variant.Id)) continue;
                document.Variants.Add(new ColorThemeInterchangeVariant
                {
                    Id = variant.Id,
                    DisplayName = variant.DisplayName,
                    IsHighContrast = variant.IsHighContrast
                });
            }

            foreach (var definition in themeSet.TokenDefinitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Id)) continue;

                var token = new ColorThemeInterchangeToken
                {
                    Id = definition.Id,
                    Kind = definition.Kind,
                    Usage = definition.Usage,
                    Required = definition.Required,
                    DisplayName = definition.DisplayName,
                    Description = definition.Description,
                    Deprecated = definition.Deprecated,
                    ReplacementId = definition.ReplacementId
                };

                foreach (string tag in definition.Tags) token.Tags.Add(tag);

                foreach (var variant in themeSet.Variants)
                {
                    if (variant == null || string.IsNullOrEmpty(variant.Id)) continue;

                    var expression = FindExpression(variant, definition.Id);
                    if (expression == null) continue;   // Optional token this variant does not supply.

                    token.Modes[variant.Id] = expression.IsAlias
                        ? new ColorThemeInterchangeValue
                        {
                            Alias = expression.AliasTokenId,
                            AliasAlpha = expression.AlphaMultiplier
                        }
                        : new ColorThemeInterchangeValue
                        {
                            Hex = ColorThemeInterchange.ToHex(expression.Literal)
                        };
                }

                document.Tokens.Add(token);
            }

            foreach (var alias in themeSet.LegacyAliases)
            {
                if (alias == null || !alias.Key.IsAssigned) continue;
                document.LegacyAliases.Add(new ColorThemeInterchangeAlias
                {
                    Swatch = alias.Key.SwatchName,
                    ColorId = alias.Key.ColorId,
                    Token = alias.CanonicalTokenId,
                    Note = alias.Note
                });
            }

            foreach (var requirement in themeSet.AccessibilityRequirements)
            {
                if (requirement == null) continue;

                var contrast = new ColorThemeInterchangeContrast
                {
                    Foreground = requirement.ForegroundTokenId,
                    Background = requirement.BackgroundTokenId,
                    UnderSurface = requirement.UnderSurfaceTokenId,
                    MinimumRatio = requirement.MinimumRatio,
                    Severity = requirement.Severity,
                    Rationale = requirement.Rationale
                };
                foreach (string variantId in requirement.AppliesToVariants)
                    contrast.AppliesToVariants.Add(variantId);

                document.Accessibility.Add(contrast);
            }

            return document;
        }

        /// <summary>Serializes a document to JSON.</summary>
        /// <param name="document">The document to write.</param>
        /// <param name="themeSet">
        /// The set the document came from, used to resolve each token's DTCG <c>$value</c>. May be
        /// <c>null</c>, in which case <c>$value</c> is omitted for aliases.
        /// </param>
        /// <returns>Indented JSON with a trailing newline.</returns>
        public static string ToJson(ColorThemeInterchangeDocument document, ColorThemeSet themeSet = null)
        {
            if (document == null) return null;

            // The default variant resolved once, so every token's $value comes from one coherent snapshot
            // rather than being re-derived per token.
            ResolvedColorTheme defaultTheme = null;
            if (themeSet != null && !string.IsNullOrEmpty(document.DefaultVariantId))
            {
                ColorThemeResolver.TryResolve(themeSet, document.DefaultVariantId, 0, out defaultTheme,
                    out _);
            }

            var root = new JObject
            {
                ["$schema"] = document.Schema,
                ["setId"] = document.SetId,
                ["displayName"] = document.DisplayName,
                ["schemaVersion"] = document.SchemaVersion,
                ["defaultMode"] = document.DefaultVariantId
            };

            var modes = new JArray();
            foreach (var variant in document.Variants)
            {
                modes.Add(new JObject
                {
                    ["id"] = variant.Id,
                    ["displayName"] = variant.DisplayName,
                    ["highContrast"] = variant.IsHighContrast
                });
            }
            root["modes"] = modes;

            var tokens = new JObject();
            foreach (var token in document.Tokens)
            {
                tokens[token.Id] = WriteToken(token, defaultTheme);
            }
            root["tokens"] = tokens;

            var molca = new JObject();

            var aliases = new JArray();
            foreach (var alias in document.LegacyAliases)
            {
                aliases.Add(new JObject
                {
                    ["swatch"] = alias.Swatch,
                    ["colorId"] = alias.ColorId,
                    ["token"] = alias.Token,
                    ["note"] = alias.Note
                });
            }
            molca["legacyAliases"] = aliases;

            var accessibility = new JArray();
            foreach (var contrast in document.Accessibility)
            {
                var entry = new JObject
                {
                    ["foreground"] = contrast.Foreground,
                    ["background"] = contrast.Background,
                    ["minimumRatio"] = contrast.MinimumRatio,
                    ["severity"] = contrast.Severity.ToString(),
                    ["rationale"] = contrast.Rationale
                };
                if (!string.IsNullOrEmpty(contrast.UnderSurface))
                    entry["underSurface"] = contrast.UnderSurface;
                if (contrast.AppliesToVariants.Count > 0)
                    entry["modes"] = new JArray(contrast.AppliesToVariants);

                accessibility.Add(entry);
            }
            molca["accessibility"] = accessibility;

            root["$extensions"] = new JObject { ["molca"] = molca };

            return root.ToString(Formatting.Indented) + "\n";
        }

        private static JObject WriteToken(ColorThemeInterchangeToken token, ResolvedColorTheme defaultTheme)
        {
            var entry = new JObject { ["$type"] = ColorThemeInterchange.ColorType };

            // $value is the standard half: a single resolved colour a plain DTCG reader can use. The
            // authored expressions live under $extensions, which is the lossless half.
            if (defaultTheme != null && defaultTheme.TryGetColor(token.Id, out Color resolved))
                entry["$value"] = ColorThemeInterchange.ToHex(resolved);

            if (!string.IsNullOrEmpty(token.Description)) entry["$description"] = token.Description;

            var molca = new JObject
            {
                ["kind"] = token.Kind.ToString(),
                ["usage"] = token.Usage.ToString(),
                ["required"] = token.Required
            };

            if (!string.IsNullOrEmpty(token.DisplayName)) molca["displayName"] = token.DisplayName;
            if (token.Deprecated)
            {
                molca["deprecated"] = true;
                if (!string.IsNullOrEmpty(token.ReplacementId))
                    molca["replacementId"] = token.ReplacementId;
            }
            if (token.Tags.Count > 0) molca["tags"] = new JArray(token.Tags);

            var modeValues = new JObject();
            foreach (var pair in token.Modes)
            {
                modeValues[pair.Key] = WriteValue(pair.Value);
            }
            molca["modes"] = modeValues;

            entry["$extensions"] = new JObject { ["molca"] = molca };
            return entry;
        }

        private static JToken WriteValue(ColorThemeInterchangeValue value)
        {
            if (!value.IsAlias) return value.Hex;

            // An alias without a multiplier is written as a bare reference string, in DTCG's own
            // "{token.path}"-style spirit; one with a multiplier needs an object, so both forms exist and
            // the reader accepts either.
            if (Mathf.Approximately(value.AliasAlpha, 1f)) return $"{{{value.Alias}}}";

            return new JObject
            {
                ["alias"] = value.Alias,
                ["alpha"] = value.AliasAlpha
            };
        }

        private static ColorExpression FindExpression(ColorThemeVariant variant, string tokenId)
        {
            foreach (var value in variant.Values)
            {
                if (value != null && value.TokenId == tokenId) return value.Expression;
            }
            return null;
        }
    }
}
#endif
