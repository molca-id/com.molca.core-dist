#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Molca.ColorID;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>What an import would change.</summary>
    public enum ColorThemeImportChangeKind
    {
        /// <summary>A token the target set does not declare.</summary>
        AddToken,

        /// <summary>A token whose metadata or values differ.</summary>
        UpdateToken,

        /// <summary>A token the document omits.</summary>
        RemoveToken,

        /// <summary>A variant the target set does not declare.</summary>
        AddVariant,

        /// <summary>A variant the document omits.</summary>
        RemoveVariant,

        /// <summary>A legacy alias the target set does not have.</summary>
        AddAlias,

        /// <summary>A legacy alias pointing somewhere else.</summary>
        UpdateAlias,

        /// <summary>A legacy alias the document omits.</summary>
        RemoveAlias,

        /// <summary>The accessibility requirement list differs.</summary>
        ChangeContrast
    }

    /// <summary>One difference between a document and the set it would replace.</summary>
    public sealed class ColorThemeImportChange
    {
        /// <summary>What kind of change this is.</summary>
        public ColorThemeImportChangeKind Kind { get; }

        /// <summary>What it applies to — a token ID, variant ID or legacy key.</summary>
        public string Subject { get; }

        /// <summary>What specifically differs.</summary>
        public string Detail { get; }

        internal ColorThemeImportChange(ColorThemeImportChangeKind kind, string subject, string detail)
        {
            Kind = kind;
            Subject = subject;
            Detail = detail;
        }

        /// <inheritdoc/>
        public override string ToString() => string.IsNullOrEmpty(Detail)
            ? $"{Kind}: {Subject}"
            : $"{Kind}: {Subject} — {Detail}";
    }

    /// <summary>A previewed import. Building one changes nothing.</summary>
    public sealed class ColorThemeImportPlan
    {
        /// <summary>The parsed document.</summary>
        public ColorThemeInterchangeDocument Document { get; }

        /// <summary>The set the import would overwrite.</summary>
        public ColorThemeSet Target { get; }

        /// <summary>
        /// The set the document describes, built in memory.
        /// </summary>
        /// <remarks>
        /// Not written anywhere. It exists so the preview can answer questions that need a <i>resolved</i>
        /// theme — does every variant still resolve, does any contrast requirement start failing — by
        /// actually resolving the incoming data rather than reasoning about it. Applying then copies from
        /// this same object, so the preview describes precisely what would land.
        /// </remarks>
        internal ColorThemeSet Candidate { get; }

        /// <summary>Every difference the import would make.</summary>
        public IReadOnlyList<ColorThemeImportChange> Changes { get; }

        /// <summary>Reasons the import cannot proceed.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>Things an author should read before approving.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Serialized sites naming a token the import would remove.</summary>
        public IReadOnlyList<string> AffectedReferences { get; }

        /// <summary>Contrast requirements that pass today and would fail after.</summary>
        public IReadOnlyList<string> ContrastRegressions { get; }

        /// <summary>Whether the import can be applied.</summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary>How many changes of a kind the import would make.</summary>
        public int CountOf(ColorThemeImportChangeKind kind)
        {
            int count = 0;
            foreach (var change in Changes) if (change.Kind == kind) count++;
            return count;
        }

        internal ColorThemeImportPlan(ColorThemeInterchangeDocument document, ColorThemeSet target,
            ColorThemeSet candidate, List<ColorThemeImportChange> changes, List<string> errors,
            List<string> warnings, List<string> affectedReferences, List<string> contrastRegressions)
        {
            Document = document;
            Target = target;
            Candidate = candidate;
            Changes = changes ?? new List<ColorThemeImportChange>();
            Errors = errors ?? new List<string>();
            Warnings = warnings ?? new List<string>();
            AffectedReferences = affectedReferences ?? new List<string>();
            ContrastRegressions = contrastRegressions ?? new List<string>();
        }

        /// <summary>A human-readable preview.</summary>
        public string ToPreview()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Colour theme import into '{Target?.DisplayName ?? "<new set>"}'");
            builder.AppendLine($"  source: {Document?.DisplayName} ({Document?.SetId})");

            if (Errors.Count > 0)
            {
                builder.AppendLine("BLOCKED:");
                foreach (string error in Errors) builder.AppendLine($"  - {error}");
                return builder.ToString();
            }

            builder.AppendLine($"  tokens: +{CountOf(ColorThemeImportChangeKind.AddToken)} "
                               + $"~{CountOf(ColorThemeImportChangeKind.UpdateToken)} "
                               + $"-{CountOf(ColorThemeImportChangeKind.RemoveToken)}");
            builder.AppendLine($"  variants: +{CountOf(ColorThemeImportChangeKind.AddVariant)} "
                               + $"-{CountOf(ColorThemeImportChangeKind.RemoveVariant)}");
            builder.AppendLine($"  aliases: +{CountOf(ColorThemeImportChangeKind.AddAlias)} "
                               + $"~{CountOf(ColorThemeImportChangeKind.UpdateAlias)} "
                               + $"-{CountOf(ColorThemeImportChangeKind.RemoveAlias)}");

            foreach (string warning in Warnings) builder.AppendLine($"  ! {warning}");

            foreach (string regression in ContrastRegressions)
                builder.AppendLine($"  ! contrast regression: {regression}");

            foreach (string reference in AffectedReferences)
                builder.AppendLine($"  ! affected reference: {reference}");

            foreach (string unsupported in Document.UnsupportedFields)
                builder.AppendLine($"  ? unsupported field ignored: {unsupported}");

            foreach (var change in Changes) builder.AppendLine($"  {change}");

            return builder.ToString();
        }
    }

    /// <summary>
    /// Reads the JSON interchange format and previews importing it into a theme set.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Interchange/</c>.
    /// <b>Shape:</b> editor-only static service. Parsing and planning write nothing.
    /// <para/>
    /// <b>An import is always previewed.</b> It replaces a project's entire colour contract, and the two
    /// ways that goes wrong are invisible in the file itself: a token the document drops may still be named
    /// by serialized content, and a colour the document changes may push a contrast requirement below its
    /// threshold. Both need the incoming data <i>resolved</i> to detect, which is why the plan builds the
    /// candidate set and interrogates it rather than diffing JSON.
    /// <para/>
    /// <b>Unknown fields are reported, not ignored.</b> A file from a newer exporter may carry meaning this
    /// version drops; a preview that stayed silent about it would look complete.
    /// </remarks>
    public static class ColorThemeInterchangeImporter
    {
        /// <summary>Parses interchange JSON.</summary>
        /// <param name="json">The document text.</param>
        /// <param name="errors">Reasons parsing failed.</param>
        /// <returns>The document, or <c>null</c> when it could not be read.</returns>
        public static ColorThemeInterchangeDocument Parse(string json, out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(json))
            {
                errors.Add("The document is empty.");
                return null;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException exception)
            {
                errors.Add($"Not valid JSON: {exception.Message}");
                return null;
            }

            var document = new ColorThemeInterchangeDocument
            {
                Schema = root.Value<string>("$schema"),
                SetId = root.Value<string>("setId"),
                DisplayName = root.Value<string>("displayName"),
                SchemaVersion = root.Value<int?>("schemaVersion") ?? ColorThemeSet.CurrentSchemaVersion,
                DefaultVariantId = root.Value<string>("defaultMode")
            };

            if (document.Schema != ColorThemeInterchange.SchemaId)
            {
                errors.Add($"Unrecognised schema '{document.Schema}'. This version reads "
                           + $"'{ColorThemeInterchange.SchemaId}'.");
                return null;
            }

            ReadModes(root, document, errors);
            ReadTokens(root, document, errors);
            ReadExtensions(root, document, errors);
            RecordUnsupported(root, document);

            return errors.Count == 0 ? document : null;
        }

        private static void ReadModes(JObject root, ColorThemeInterchangeDocument document,
            List<string> errors)
        {
            if (!(root["modes"] is JArray modes))
            {
                errors.Add("The document declares no 'modes' array, so it has no variants.");
                return;
            }

            foreach (var mode in modes)
            {
                string id = mode.Value<string>("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    errors.Add("A mode entry has no 'id'.");
                    continue;
                }

                document.Variants.Add(new ColorThemeInterchangeVariant
                {
                    Id = ColorThemeSet.NormalizeVariantId(id),
                    DisplayName = mode.Value<string>("displayName"),
                    IsHighContrast = mode.Value<bool?>("highContrast") ?? false
                });
            }
        }

        private static void ReadTokens(JObject root, ColorThemeInterchangeDocument document,
            List<string> errors)
        {
            if (!(root["tokens"] is JObject tokens))
            {
                errors.Add("The document declares no 'tokens' object.");
                return;
            }

            foreach (var property in tokens.Properties())
            {
                if (!(property.Value is JObject entry))
                {
                    errors.Add($"Token '{property.Name}' is not an object.");
                    continue;
                }

                var token = new ColorThemeInterchangeToken
                {
                    Id = property.Name,
                    Description = entry.Value<string>("$description")
                };

                var molca = entry["$extensions"]?["molca"] as JObject;
                if (molca != null) ReadTokenExtensions(molca, token, errors);

                if (token.Modes.Count == 0)
                {
                    // No per-mode data. A plain DTCG file only has $value, so it is applied to every
                    // declared mode — that is the only reading that makes such a file importable at all,
                    // and it is recorded as a warning rather than assumed to be what the author wanted.
                    string singleValue = entry.Value<string>("$value");
                    if (!string.IsNullOrEmpty(singleValue))
                    {
                        foreach (var variant in document.Variants)
                        {
                            token.Modes[variant.Id] = ParseValue(singleValue, token.Id, variant.Id, errors);
                        }
                    }
                }

                document.Tokens.Add(token);
            }
        }

        private static void ReadTokenExtensions(JObject molca, ColorThemeInterchangeToken token,
            List<string> errors)
        {
            if (Enum.TryParse(molca.Value<string>("kind") ?? string.Empty, out ColorTokenKind kind))
                token.Kind = kind;
            if (Enum.TryParse(molca.Value<string>("usage") ?? string.Empty, out ColorTokenUsage usage))
                token.Usage = usage;

            token.Required = molca.Value<bool?>("required") ?? true;
            token.DisplayName = molca.Value<string>("displayName");
            token.Deprecated = molca.Value<bool?>("deprecated") ?? false;
            token.ReplacementId = molca.Value<string>("replacementId");

            if (molca["tags"] is JArray tags)
            {
                foreach (var tag in tags)
                {
                    string value = tag.Value<string>();
                    if (!string.IsNullOrEmpty(value)) token.Tags.Add(value);
                }
            }

            if (!(molca["modes"] is JObject modes)) return;

            foreach (var mode in modes.Properties())
            {
                token.Modes[ColorThemeSet.NormalizeVariantId(mode.Name)] =
                    ParseValue(mode.Value, token.Id, mode.Name, errors);
            }
        }

        /// <summary>Reads a mode value in any of its three accepted spellings.</summary>
        private static ColorThemeInterchangeValue ParseValue(JToken raw, string tokenId, string modeId,
            List<string> errors)
        {
            if (raw is JObject aliasObject)
            {
                return new ColorThemeInterchangeValue
                {
                    Alias = aliasObject.Value<string>("alias"),
                    AliasAlpha = aliasObject.Value<float?>("alpha") ?? 1f
                };
            }

            string text = raw?.Value<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                errors.Add($"Token '{tokenId}' mode '{modeId}' has no value.");
                return new ColorThemeInterchangeValue { Hex = "#00000000" };
            }

            // "{some/token}" is a reference, mirroring DTCG's alias syntax.
            if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                return new ColorThemeInterchangeValue { Alias = text.Substring(1, text.Length - 2) };

            if (!ColorThemeInterchange.TryParseHex(text, out Color color))
            {
                errors.Add($"Token '{tokenId}' mode '{modeId}' has value '{text}', which is neither a hex "
                           + "colour nor a '{token}' reference.");
                return new ColorThemeInterchangeValue { Hex = "#00000000" };
            }

            return new ColorThemeInterchangeValue { Hex = ColorThemeInterchange.ToHex(color) };
        }

        private static void ReadExtensions(JObject root, ColorThemeInterchangeDocument document,
            List<string> errors)
        {
            if (!(root["$extensions"]?["molca"] is JObject molca)) return;

            if (molca["legacyAliases"] is JArray aliases)
            {
                foreach (var alias in aliases)
                {
                    document.LegacyAliases.Add(new ColorThemeInterchangeAlias
                    {
                        Swatch = alias.Value<string>("swatch"),
                        ColorId = alias.Value<string>("colorId"),
                        Token = alias.Value<string>("token"),
                        Note = alias.Value<string>("note")
                    });
                }
            }

            if (!(molca["accessibility"] is JArray requirements)) return;

            foreach (var requirement in requirements)
            {
                var contrast = new ColorThemeInterchangeContrast
                {
                    Foreground = requirement.Value<string>("foreground"),
                    Background = requirement.Value<string>("background"),
                    UnderSurface = requirement.Value<string>("underSurface"),
                    MinimumRatio = requirement.Value<float?>("minimumRatio")
                                   ?? ColorContrast.MinimumNormalText,
                    Rationale = requirement.Value<string>("rationale")
                };

                if (Enum.TryParse(requirement.Value<string>("severity") ?? string.Empty,
                        out ColorContrastSeverity severity))
                    contrast.Severity = severity;

                if (requirement["modes"] is JArray modes)
                {
                    foreach (var mode in modes)
                    {
                        string id = mode.Value<string>();
                        if (!string.IsNullOrEmpty(id))
                            contrast.AppliesToVariants.Add(ColorThemeSet.NormalizeVariantId(id));
                    }
                }

                document.Accessibility.Add(contrast);
            }
        }

        /// <summary>Records top-level fields this version does not read.</summary>
        private static void RecordUnsupported(JObject root, ColorThemeInterchangeDocument document)
        {
            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                "$schema", "setId", "displayName", "schemaVersion", "defaultMode", "modes", "tokens",
                "$extensions"
            };

            foreach (var property in root.Properties())
            {
                if (!known.Contains(property.Name)) document.UnsupportedFields.Add($"$.{property.Name}");
            }
        }

        // ── planning ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Previews importing a document over a theme set.
        /// </summary>
        /// <param name="document">The parsed document.</param>
        /// <param name="target">The set that would be overwritten.</param>
        /// <returns>A plan. Nothing is written.</returns>
        public static ColorThemeImportPlan Plan(ColorThemeInterchangeDocument document, ColorThemeSet target)
        {
            var changes = new List<ColorThemeImportChange>();
            var errors = new List<string>();
            var warnings = new List<string>();
            var affected = new List<string>();
            var regressions = new List<string>();

            if (document == null)
            {
                errors.Add("No document supplied.");
                return new ColorThemeImportPlan(null, target, null, changes, errors, warnings, affected,
                    regressions);
            }

            if (target == null) errors.Add("No target theme set supplied.");
            else
            {
                string refusal = ColorThemeAssetWriteAccess.DescribeRefusal(
                    AssetDatabase.GetAssetPath(target));
                if (refusal != null) errors.Add(refusal);

                if (!string.IsNullOrEmpty(document.SetId) && !string.IsNullOrEmpty(target.StableSetId)
                    && document.SetId != target.StableSetId)
                {
                    // Not blocked: importing one team's theme over another's is a legitimate thing to do
                    // deliberately. It is a warning because doing it by accident orphans every persisted
                    // user preference, which are scoped by this ID.
                    warnings.Add($"The document's set ID '{document.SetId}' differs from the target's "
                                 + $"'{target.StableSetId}'. Every persisted variant preference is scoped by "
                                 + "that ID and will be ignored after this import.");
                }
            }

            var candidate = BuildCandidate(document, errors);
            if (candidate == null || errors.Count > 0)
            {
                if (candidate != null) UnityEngine.Object.DestroyImmediate(candidate);
                return new ColorThemeImportPlan(document, target, null, changes, errors, warnings, affected,
                    regressions);
            }

            var candidateErrors = new List<string>();
            if (!candidate.Validate(candidateErrors))
            {
                foreach (string error in candidateErrors) errors.Add($"Incoming set is invalid: {error}");
            }

            var candidateThemes = ResolveAll(candidate, errors);

            if (target != null)
            {
                DiffTokens(document, target, changes);
                DiffVariants(document, target, changes);
                DiffAliases(document, target, changes);
                DiffContrast(document, target, changes);
                CollectAffectedReferences(document, target, affected, warnings);
                CollectContrastRegressions(target, candidate, candidateThemes, regressions);
            }

            return new ColorThemeImportPlan(document, target, candidate, changes, errors, warnings, affected,
                regressions);
        }

        /// <summary>Applies a previewed import.</summary>
        /// <param name="plan">The approved plan.</param>
        /// <param name="error">Why it was refused, when it was.</param>
        /// <returns><c>true</c> when the target was rewritten.</returns>
        /// <remarks>
        /// Copies from the plan's own candidate, so what lands is exactly what was previewed rather than a
        /// second parse of the same file.
        /// </remarks>
        public static bool Apply(ColorThemeImportPlan plan, out string error)
        {
            error = null;

            if (plan == null || !plan.IsValid)
            {
                error = plan == null ? "No plan supplied." : string.Join("; ", plan.Errors);
                return false;
            }

            if (plan.Target == null || plan.Candidate == null)
            {
                error = "The plan has no target or no candidate.";
                return false;
            }

            Undo.RecordObject(plan.Target, "Import colour theme");
            EditorUtility.CopySerialized(plan.Candidate, plan.Target);
            plan.Target.InvalidateIndexes();
            EditorUtility.SetDirty(plan.Target);
            AssetDatabase.SaveAssets();
            return true;
        }

        // ── candidate construction ─────────────────────────────────────────────────────────────

        /// <summary>Builds the set the document describes, in memory.</summary>
        internal static ColorThemeSet BuildCandidate(ColorThemeInterchangeDocument document,
            List<string> errors)
        {
            if (document.Variants.Count == 0)
            {
                errors.Add("The document declares no modes, so no variant could be activated.");
                return null;
            }

            var definitions = new List<ColorTokenDefinition>();
            var variants = new List<ColorThemeVariant>();
            var aliases = new List<LegacyColorAlias>();
            var requirements = new List<ColorContrastRequirement>();

            foreach (var variant in document.Variants)
            {
                variants.Add(new ColorThemeVariant(variant.Id, variant.DisplayName, variant.IsHighContrast));
            }

            foreach (var token in document.Tokens)
            {
                if (!ColorTokenId.Validate(token.Id, out string idError))
                {
                    errors.Add($"Token '{token.Id}' is not a canonical ID: {idError}");
                    continue;
                }

                definitions.Add(new ColorTokenDefinition(token.Id, token.Kind, token.Usage, token.Required,
                    token.DisplayName, token.Description));

                foreach (var variant in variants)
                {
                    if (!token.Modes.TryGetValue(variant.Id, out var value)) continue;

                    if (value.IsAlias)
                    {
                        variant.SetValue(token.Id, Mathf.Approximately(value.AliasAlpha, 1f)
                            ? ColorExpression.FromAlias(value.Alias)
                            : ColorExpression.FromAliasWithAlpha(value.Alias, value.AliasAlpha));
                        continue;
                    }

                    if (ColorThemeInterchange.TryParseHex(value.Hex, out Color literal))
                        variant.SetValue(token.Id, ColorExpression.FromLiteral(literal));
                    else
                        errors.Add($"Token '{token.Id}' mode '{variant.Id}' has an unreadable colour.");
                }
            }

            foreach (var alias in document.LegacyAliases)
            {
                if (string.IsNullOrEmpty(alias.Swatch) || string.IsNullOrEmpty(alias.ColorId)) continue;
                aliases.Add(new LegacyColorAlias(alias.Swatch, alias.ColorId, alias.Token, alias.Note));
            }

            foreach (var contrast in document.Accessibility)
            {
                var requirement = new ColorContrastRequirement(contrast.Foreground, contrast.Background,
                    contrast.MinimumRatio, contrast.Severity, contrast.UnderSurface, contrast.Rationale);
                requirements.Add(requirement);

                if (contrast.AppliesToVariants.Count > 0)
                {
                    // The variant scope is a private serialized list with no constructor parameter; a
                    // requirement that silently applied everywhere would change what the import means.
                    ColorThemeSetEditing.SetContrastVariantScope(requirement, contrast.AppliesToVariants);
                }
            }

            if (errors.Count > 0) return null;

            var candidate = ScriptableObject.CreateInstance<ColorThemeSet>();
            candidate.name = document.DisplayName ?? "Imported Colour Theme Set";
            ColorThemeSetEditing.Populate(candidate, document.SetId, document.DisplayName, definitions,
                variants, aliases, requirements);
            return candidate;
        }

        private static Dictionary<string, ResolvedColorTheme> ResolveAll(ColorThemeSet set,
            List<string> errors)
        {
            var resolved = new Dictionary<string, ResolvedColorTheme>(StringComparer.Ordinal);

            foreach (string variantId in set.GetVariantIds())
            {
                if (ColorThemeResolver.TryResolve(set, variantId, 0, out var theme, out var diagnostics)
                    == ColorThemeActivation.Activated)
                {
                    resolved[variantId] = theme;
                    continue;
                }

                errors.Add($"Incoming variant '{variantId}' does not resolve: "
                           + string.Join("; ", diagnostics));
            }

            return resolved;
        }

        // ── diffing ────────────────────────────────────────────────────────────────────────────

        private static void DiffTokens(ColorThemeInterchangeDocument document, ColorThemeSet target,
            List<ColorThemeImportChange> changes)
        {
            var incoming = new HashSet<string>(StringComparer.Ordinal);

            foreach (var token in document.Tokens)
            {
                incoming.Add(token.Id);
                var existing = target.GetDefinition(token.Id);

                if (existing == null)
                {
                    changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.AddToken, token.Id,
                        $"{token.Kind}, {token.Usage}, required={token.Required}"));
                    continue;
                }

                var differences = new List<string>();
                if (existing.Kind != token.Kind) differences.Add($"kind {existing.Kind}→{token.Kind}");
                if (existing.Usage != token.Usage) differences.Add($"usage {existing.Usage}→{token.Usage}");
                if (existing.Required != token.Required)
                    differences.Add($"required {existing.Required}→{token.Required}");
                if (existing.Deprecated != token.Deprecated)
                    differences.Add($"deprecated {existing.Deprecated}→{token.Deprecated}");

                if (differences.Count > 0)
                {
                    changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.UpdateToken, token.Id,
                        string.Join(", ", differences)));
                }
            }

            foreach (var definition in target.TokenDefinitions)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Id)) continue;
                if (incoming.Contains(definition.Id)) continue;

                changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.RemoveToken,
                    definition.Id, "the document does not declare it"));
            }
        }

        private static void DiffVariants(ColorThemeInterchangeDocument document, ColorThemeSet target,
            List<ColorThemeImportChange> changes)
        {
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var variant in document.Variants)
            {
                incoming.Add(variant.Id);
                if (target.GetVariant(variant.Id) == null)
                {
                    changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.AddVariant,
                        variant.Id, variant.DisplayName));
                }
            }

            foreach (string variantId in target.GetVariantIds())
            {
                if (!incoming.Contains(variantId))
                {
                    changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.RemoveVariant,
                        variantId, "the document does not declare it"));
                }
            }
        }

        private static void DiffAliases(ColorThemeInterchangeDocument document, ColorThemeSet target,
            List<ColorThemeImportChange> changes)
        {
            var incoming = new Dictionary<LegacyColorKey, string>();

            foreach (var alias in document.LegacyAliases)
            {
                var key = new LegacyColorKey(alias.Swatch, alias.ColorId);
                if (!key.IsAssigned) continue;
                incoming[key] = alias.Token;

                string existing = target.ResolveLegacyToken(key);
                if (existing == null)
                {
                    changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.AddAlias,
                        key.ToString(), $"→ {alias.Token}"));
                }
                else if (existing != alias.Token)
                {
                    changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.UpdateAlias,
                        key.ToString(), $"{existing} → {alias.Token}"));
                }
            }

            foreach (var alias in target.LegacyAliases)
            {
                if (alias == null || !alias.Key.IsAssigned) continue;
                if (incoming.ContainsKey(alias.Key)) continue;

                changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.RemoveAlias,
                    alias.Key.ToString(),
                    $"was → {alias.CanonicalTokenId}; content using this pair stops resolving"));
            }
        }

        private static void DiffContrast(ColorThemeInterchangeDocument document, ColorThemeSet target,
            List<ColorThemeImportChange> changes)
        {
            int before = target.AccessibilityRequirements.Count;
            int after = document.Accessibility.Count;
            if (before == after) return;

            changes.Add(new ColorThemeImportChange(ColorThemeImportChangeKind.ChangeContrast,
                "accessibility requirements", $"{before} → {after}"));
        }

        /// <summary>Finds serialized sites naming a token the import would remove.</summary>
        private static void CollectAffectedReferences(ColorThemeInterchangeDocument document,
            ColorThemeSet target, List<string> affected, List<string> warnings)
        {
            var removed = new HashSet<string>(StringComparer.Ordinal);
            var incoming = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in document.Tokens) incoming.Add(token.Id);

            foreach (var definition in target.TokenDefinitions)
            {
                if (definition != null && !string.IsNullOrEmpty(definition.Id)
                    && !incoming.Contains(definition.Id))
                    removed.Add(definition.Id);
            }

            if (removed.Count == 0) return;

            // A full audit is the only way to answer this: a token is "in use" because some prefab or scene
            // names it, and that is a content question the theme set cannot answer about itself.
            var snapshot = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);
            foreach (var site in snapshot.UsageSites)
            {
                if (site.CanonicalTokenId == null || !removed.Contains(site.CanonicalTokenId)) continue;
                affected.Add($"{site.AssetPath} names '{site.CanonicalTokenId}' ({site.Kind})");
            }

            if (affected.Count > 0)
            {
                warnings.Add($"{affected.Count} serialized site(s) name a token this import removes. They "
                             + "will stop resolving. Rename or alias before importing.");
            }
        }

        /// <summary>Finds requirements that pass today and would fail after the import.</summary>
        private static void CollectContrastRegressions(ColorThemeSet target, ColorThemeSet candidate,
            Dictionary<string, ResolvedColorTheme> candidateThemes, List<string> regressions)
        {
            var passingBefore = new HashSet<string>(StringComparer.Ordinal);

            foreach (string variantId in target.GetVariantIds())
            {
                if (ColorThemeResolver.TryResolve(target, variantId, 0, out var theme, out _)
                    != ColorThemeActivation.Activated) continue;

                foreach (var result in ColorThemeResolver.EvaluateContrast(target, theme))
                {
                    // Incomplete is neither a pass nor a failure, so it cannot establish a baseline to
                    // regress from.
                    if (result.IsIncomplete || !result.Passed) continue;
                    passingBefore.Add(Key(variantId, result));
                }
            }

            foreach (var pair in candidateThemes)
            {
                foreach (var result in ColorThemeResolver.EvaluateContrast(candidate, pair.Value))
                {
                    if (result.IsIncomplete || result.Passed) continue;

                    // Only a *regression* — a pair that already failed is the state the author is living
                    // with, and repeating it here would bury the ones this import breaks.
                    if (passingBefore.Contains(Key(pair.Key, result))) regressions.Add(result.ToString());
                }
            }
        }

        private static string Key(string variantId, ColorContrastResult result) =>
            $"{variantId}|{result.Requirement.ForegroundTokenId}|{result.Requirement.BackgroundTokenId}";
    }
}
#endif
