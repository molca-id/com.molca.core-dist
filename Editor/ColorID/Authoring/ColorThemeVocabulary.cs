#if UNITY_EDITOR
using System.Collections.Generic;
using Molca.ColorID;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// The canonical Molca colour vocabulary: the token contract, its Dark/Light values, the legacy
    /// alias map, and the accessibility requirements — defined once, in code.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Authoring/</c>.
    /// <b>Shape:</b> editor-only static definition. <see cref="ColorThemeSetBootstrap"/> writes it to an
    /// asset; tests build it in memory and assert it against the V1 baseline.
    /// <para/>
    /// <b>Three tiers</b>, the standard design-token shape:
    /// <list type="number">
    /// <item><description>
    /// <c>palette/*</c> — primitives. Raw ramps and hues with no opinion about use. Not offered in the
    /// component picker by default.
    /// </description></item>
    /// <item><description>
    /// <c>surface/*</c>, <c>text/*</c>, <c>border/*</c>, <c>action/*</c>, <c>status/*</c>,
    /// <c>focus/*</c> — semantic roles. This is the authoring API.
    /// </description></item>
    /// <item><description>
    /// Component tokens — deliberately absent. The revamp plan's non-goals rule out turning this into a
    /// full component-token rewrite; the UI Token Catalog already covers that layer.
    /// </description></item>
    /// </list>
    /// <para/>
    /// <b>Why this is a rebuild rather than a 1:1 remap.</b> The measured V1 data does not support the V1
    /// names:
    /// <list type="bullet">
    /// <item><description>
    /// <c>Default.Secondary</c>'s RGB is <i>identical</i> to <c>Default.Background</c>, and
    /// <c>Default.Disabled</c>'s is too. "Secondary" was never a secondary brand colour — it is the
    /// surface base at full alpha. <c>Default.Accent</c> is likewise a second surface level. The only
    /// genuine brand colour in the palette is <c>Default.Primary</c>.
    /// </description></item>
    /// <item><description>
    /// The <c>Text.*</c> family is not all text. <c>Text.20</c> has 8 uses and every one is an
    /// <c>Image</c> fill; <c>Text.80</c> is 3 fills to 1 label. They are washes, and naming them
    /// <c>text/*</c> would make every future contrast check on them meaningless.
    /// </description></item>
    /// <item><description>
    /// <c>Default.Text</c> and <c>Text.100</c> are byte-identical at 8 bits in both variants, so they
    /// collapse to one token with no visible change.
    /// </description></item>
    /// </list>
    /// <b>Hard constraint:</b> every mapped legacy key must resolve to the same 8-bit RGBA it does today,
    /// so migrating content cannot change what renders. <c>ColorThemeVocabularyTests</c> asserts that
    /// against the Phase 0 baseline for all 18 keys actually in use.
    /// <para/>
    /// <b>Two documented exceptions</b>, both in the Light variant only: <c>Text.60</c> and <c>Text.40</c>
    /// carried a WCAG failure that V1 could not detect, because nothing recorded that they were
    /// foregrounds. Their Light alphas are raised to clear their thresholds; Dark — the shipped default
    /// variant — is byte-identical. The baseline test names both exceptions rather than being relaxed.
    /// </remarks>
    public static class ColorThemeVocabulary
    {
        /// <summary>The Dark variant ID.</summary>
        public const string DarkVariantId = "dark";

        /// <summary>The Light variant ID.</summary>
        public const string LightVariantId = "light";

        /// <summary>Stable set ID for the shipped Molca vocabulary.</summary>
        public const string StableSetId = "molca-core-color-vocabulary-v1";

        // ---- Tier 1: primitives -------------------------------------------------------------------
        // Values are the exact V1 baseline. The neutral ramp and the status hues are variant-invariant
        // in V1 and stay that way; only shell, ink and brand differ between Dark and Light.

        private const string NeutralRampPrefix = "palette/neutral/";

        /// <summary>Warm dark / light grey app-chrome tones (V1 <c>Default.Background</c> RGB).</summary>
        private const string ShellBase = "palette/shell/base";

        /// <summary>A second surface level (V1 <c>Default.Accent</c> RGB).</summary>
        private const string ShellRaised = "palette/shell/raised";

        /// <summary>The disabled tone, which differs from the shell base in Light (V1 <c>Default.Disabled</c>).</summary>
        private const string ShellDisabled = "palette/shell/disabled";

        /// <summary>Foreground base (V1 <c>Text.100</c> / <c>Default.Text</c>).</summary>
        private const string InkBase = "palette/ink/base";

        private const string Brand500 = "palette/brand/500";
        private const string Green500 = "palette/green/500";
        private const string Amber500 = "palette/amber/500";
        private const string Red500 = "palette/red/500";

        /// <summary>Every token in the contract, with its metadata.</summary>
        public static IReadOnlyList<ColorTokenDefinition> Tokens => BuildTokens();

        /// <summary>Builds an in-memory theme set carrying the whole vocabulary.</summary>
        /// <returns>A validated, unsaved <see cref="ColorThemeSet"/> instance the caller owns.</returns>
        /// <remarks>
        /// Both variants are populated in one pass so the set satisfies required-token parity the moment
        /// it exists — the property the contract model is built to guarantee.
        /// </remarks>
        public static ColorThemeSet Build()
        {
            var set = ScriptableObject.CreateInstance<ColorThemeSet>();

            var dark = new ColorThemeVariant(DarkVariantId, "Dark");
            var light = new ColorThemeVariant(LightVariantId, "Light");

            PopulatePrimitives(dark, light);
            PopulateSemantics(dark, light);

            ColorThemeSetEditing.Populate(set, StableSetId, "Molca Colour Vocabulary",
                BuildTokens(), new List<ColorThemeVariant> { dark, light }, BuildLegacyAliases(),
                BuildContrastRequirements());

            return set;
        }

        private static void PopulatePrimitives(ColorThemeVariant dark, ColorThemeVariant light)
        {
            // Pure grey ramp, identical in both variants — as in V1's Gray.* swatch.
            for (int step = 0; step <= 10; step += 2)
            {
                float value = step / 10f;
                var grey = new Color(value, value, value, 1f);
                string id = $"{NeutralRampPrefix}{step * 100}";
                dark.SetValue(id, ColorExpression.FromLiteral(grey));
                light.SetValue(id, ColorExpression.FromLiteral(grey));
            }

            Literal(dark, light, ShellBase,
                new Color(0.137255f, 0.121569f, 0.12549f, 1f),
                new Color(0.843137f, 0.843137f, 0.843137f, 1f));

            Literal(dark, light, ShellRaised,
                new Color(0.152941f, 0.152941f, 0.164706f, 1f),
                new Color(0.726429f, 0.778831f, 0.803774f, 1f));

            Literal(dark, light, ShellDisabled,
                new Color(0.137255f, 0.121569f, 0.12549f, 1f),
                new Color(0.879245f, 0.827818f, 0.840675f, 1f));

            Literal(dark, light, InkBase,
                new Color(0.92549f, 0.929412f, 0.933333f, 1f),
                new Color(0.12549f, 0.12549f, 0.12549f, 1f));

            Literal(dark, light, Brand500,
                new Color(0.811765f, 1f, 0.2f, 1f),
                new Color(0.380653f, 0.396226f, 0.331185f, 1f));

            // Status hues carry the same value in both variants in V1, so they stay shared rather than
            // being invented per variant.
            LiteralShared(dark, light, Green500, new Color(0.423529f, 0.894118f, 0.094118f, 1f));
            LiteralShared(dark, light, Amber500, new Color(0.952941f, 0.760784f, 0.070588f, 1f));
            LiteralShared(dark, light, Red500, new Color(0.952941f, 0.070588f, 0.376471f, 1f));
        }

        private static void PopulateSemantics(ColorThemeVariant dark, ColorThemeVariant light)
        {
            // Surfaces. surface/canvas keeps V1's 0.901961 alpha rather than being flattened, because
            // shipped content composites over it and changing it would change what renders.
            AliasWithAlpha(dark, light, "surface/canvas", ShellBase, 0.901961f);
            Alias(dark, light, "surface/panel", ShellBase);
            Alias(dark, light, "surface/raised", ShellRaised);
            Alias(dark, light, "surface/sunken", $"{NeutralRampPrefix}200");

            // Scrims are black at alpha in both variants — an overlay is not a themed colour.
            LiteralShared(dark, light, "surface/scrim", new Color(0f, 0f, 0f, 0.4f));
            LiteralShared(dark, light, "surface/scrim-medium", new Color(0f, 0f, 0f, 0.6f));
            LiteralShared(dark, light, "surface/scrim-strong", new Color(0f, 0f, 0f, 0.8f));

            // Washes: the ink colour at low alpha used as a *fill*. This is what V1 called Text.20 and
            // Text.80, which the usage evidence shows are surfaces, not foregrounds.
            AliasWithAlpha(dark, light, "surface/wash-subtle", InkBase, 0.2f);
            AliasWithAlpha(dark, light, "surface/wash-strong", InkBase, 0.8f);

            // Foregrounds.
            Alias(dark, light, "text/primary", InkBase);

            // The two de-emphasised text tokens are the one place the vocabulary deliberately departs from
            // the V1 values, and only in Light. V1 used a single alpha per token for both variants, which
            // works in Dark (light ink on a dark shell) and fails in Light (dark ink on a shell that is
            // itself light): 0.60 measures 5.86:1 in Dark but 3.80:1 in Light, and 0.40 measures 3.38:1
            // against 2.28:1. The Light alphas below are the smallest values that clear each token's
            // authored threshold with margin — 0.67 -> 4.62:1 (needs 4.5) and 0.53 -> 3.16:1 (needs 3.0).
            // Dark is untouched, and Dark is the shipped default variant, so no content changes appearance
            // unless a project has actually switched to Light. See COLORID_LEGACY_KEY_USAGE_INVENTORY §7.1.
            AliasWithVariantAlpha(dark, light, "text/muted", InkBase, 0.6f, 0.67f);
            AliasWithVariantAlpha(dark, light, "text/subtle", InkBase, 0.4f, 0.53f);

            Alias(dark, light, "text/accent", Brand500);

            Alias(dark, light, "border/default", $"{NeutralRampPrefix}600");
            Alias(dark, light, "focus/ring", Brand500);

            // Actions.
            Alias(dark, light, "action/primary/fill", Brand500);

            // The one place the two variants genuinely need different aliases: the Dark brand is a bright
            // lime that needs dark text on it, the Light brand a dark olive that needs light text.
            dark.SetValue("action/primary/on-fill", ColorExpression.FromAlias($"{NeutralRampPrefix}0"));
            light.SetValue("action/primary/on-fill", ColorExpression.FromAlias($"{NeutralRampPrefix}1000"));

            Alias(dark, light, "action/pressed/fill", $"{NeutralRampPrefix}1000");
            AliasWithAlpha(dark, light, "action/disabled/fill", ShellDisabled, 0.392157f);

            // Statuses. error is named /text because its measured V1 usage is 4 labels to 1 fill.
            Alias(dark, light, "status/success/fill", Green500);
            Alias(dark, light, "status/warning/fill", Amber500);
            Alias(dark, light, "status/error/text", Red500);
            AliasWithAlpha(dark, light, "status/error/surface", Red500, 0.15f);
        }

        private static void Literal(ColorThemeVariant dark, ColorThemeVariant light, string id,
            Color darkValue, Color lightValue)
        {
            dark.SetValue(id, ColorExpression.FromLiteral(darkValue));
            light.SetValue(id, ColorExpression.FromLiteral(lightValue));
        }

        private static void LiteralShared(ColorThemeVariant dark, ColorThemeVariant light, string id,
            Color value) => Literal(dark, light, id, value, value);

        private static void Alias(ColorThemeVariant dark, ColorThemeVariant light, string id,
            string targetId)
        {
            dark.SetValue(id, ColorExpression.FromAlias(targetId));
            light.SetValue(id, ColorExpression.FromAlias(targetId));
        }

        private static void AliasWithAlpha(ColorThemeVariant dark, ColorThemeVariant light, string id,
            string targetId, float alpha)
        {
            dark.SetValue(id, ColorExpression.FromAliasWithAlpha(targetId, alpha));
            light.SetValue(id, ColorExpression.FromAliasWithAlpha(targetId, alpha));
        }

        /// <summary>
        /// Aliases one target at a <i>different</i> alpha per variant — for tokens whose legibility depends
        /// on the variant's own background, not on a single authored opacity.
        /// </summary>
        private static void AliasWithVariantAlpha(ColorThemeVariant dark, ColorThemeVariant light, string id,
            string targetId, float darkAlpha, float lightAlpha)
        {
            dark.SetValue(id, ColorExpression.FromAliasWithAlpha(targetId, darkAlpha));
            light.SetValue(id, ColorExpression.FromAliasWithAlpha(targetId, lightAlpha));
        }

        private static List<ColorTokenDefinition> BuildTokens()
        {
            var tokens = new List<ColorTokenDefinition>();

            // Primitives are not required: a variant may legitimately not carry one, and they are never
            // bound directly by application components.
            for (int step = 0; step <= 10; step += 2)
            {
                tokens.Add(new ColorTokenDefinition($"{NeutralRampPrefix}{step * 100}",
                    ColorTokenKind.Primitive, ColorTokenUsage.Any, required: false,
                    description: "Pure grey ramp step. Variant-invariant."));
            }

            foreach (var (id, description) in new[]
                     {
                         (ShellBase, "App chrome base tone."),
                         (ShellRaised, "Second app chrome level, for raised surfaces."),
                         (ShellDisabled, "Chrome tone used for disabled fills."),
                         (InkBase, "Foreground base tone."),
                         (Brand500, "The brand colour. The only genuine brand hue in this palette."),
                         (Green500, "Success hue. Variant-invariant."),
                         (Amber500, "Warning hue. Variant-invariant."),
                         (Red500, "Error hue. Variant-invariant.")
                     })
            {
                tokens.Add(new ColorTokenDefinition(id, ColorTokenKind.Primitive, ColorTokenUsage.Any,
                    required: false, description: description));
            }

            void Semantic(string id, ColorTokenUsage usage, string description) =>
                tokens.Add(new ColorTokenDefinition(id, ColorTokenKind.Semantic, usage, required: true,
                    description: description));

            Semantic("surface/canvas", ColorTokenUsage.Surface,
                "The window background. Translucent (alpha 0.9) as in V1, so anything measuring contrast "
                + "against it must name an under-surface.");
            Semantic("surface/panel", ColorTokenUsage.Surface, "An opaque panel or card background.");
            Semantic("surface/raised", ColorTokenUsage.Surface, "A surface raised above the panel level.");
            Semantic("surface/sunken", ColorTokenUsage.Surface, "A recessed well or input background.");
            Semantic("surface/scrim", ColorTokenUsage.Surface, "Modal overlay behind a dialog.");
            Semantic("surface/scrim-medium", ColorTokenUsage.Surface,
                "A mid-weight modal overlay. Added after the content-migration preview found a live "
                + "Black.60 site that the first vocabulary pass had no alias for.");
            Semantic("surface/scrim-strong", ColorTokenUsage.Surface, "A heavier modal overlay.");
            Semantic("surface/wash-subtle", ColorTokenUsage.Surface,
                "The ink colour at 20% used as a fill — a divider, hover wash or disabled backing. "
                + "Named a surface because every measured V1 use of Text.20 was an Image.");
            Semantic("surface/wash-strong", ColorTokenUsage.Surface, "The ink colour at 80% used as a fill.");

            Semantic("text/primary", ColorTokenUsage.Text | ColorTokenUsage.Icon, "Default body text.");
            Semantic("text/muted", ColorTokenUsage.Text | ColorTokenUsage.Icon,
                "Secondary text. Clears 4.5:1 on the canvas in both variants, at a per-variant alpha.");
            Semantic("text/subtle", ColorTokenUsage.Text,
                "De-emphasised text. Held to the large-text threshold (3:1) by design, not to 4.5:1, so it "
                + "is only for large or non-essential text.");
            Semantic("text/accent", ColorTokenUsage.Text, "Brand-coloured text.");

            Semantic("border/default", ColorTokenUsage.Border, "Default border and divider.");
            Semantic("focus/ring", ColorTokenUsage.Focus, "Keyboard focus indicator.");

            Semantic("action/primary/fill", ColorTokenUsage.Surface, "Primary button fill.");
            Semantic("action/primary/on-fill", ColorTokenUsage.Text | ColorTokenUsage.Icon,
                "Text and icons on a primary fill. Aliases opposite ends of the neutral ramp per variant, "
                + "because the Dark brand is light and the Light brand is dark.");
            Semantic("action/pressed/fill", ColorTokenUsage.Surface, "Pressed-state fill.");
            Semantic("action/disabled/fill", ColorTokenUsage.Surface, "Disabled-state fill.");

            Semantic("status/success/fill", ColorTokenUsage.Status | ColorTokenUsage.Surface,
                "Success fill. Not a text colour — its ratio on the Light canvas is about 1.1:1.");
            Semantic("status/warning/fill", ColorTokenUsage.Status | ColorTokenUsage.Surface,
                "Warning fill. The only V1 key bound to a 3D Renderer.");
            Semantic("status/error/text", ColorTokenUsage.Status | ColorTokenUsage.Text,
                "Error text. Named /text because measured V1 usage was 4 labels to 1 fill.");
            Semantic("status/error/surface", ColorTokenUsage.Status | ColorTokenUsage.Surface,
                "Error background wash.");

            return tokens;
        }

        /// <summary>
        /// Core version in which every shipped legacy alias became deprecated.
        /// </summary>
        /// <remarks>
        /// The release that makes canonical tokens the default authoring path. Deprecation starts here
        /// because that is the first release in which an author has an alternative — deprecating a surface
        /// before its replacement ships is a warning nobody can act on.
        /// </remarks>
        public const string AliasesDeprecatedSince = "1.18.0";

        /// <summary>
        /// The earliest version in which a shipped legacy alias may be removed.
        /// </summary>
        /// <remarks>
        /// A major release, and a floor rather than a schedule: removal additionally requires an audit
        /// showing no blocking usage — see <c>ColorThemeDeprecationReport</c>. Declared in one place for
        /// the whole table so the policy cannot drift alias by alias.
        /// </remarks>
        public const string AliasesRemovableIn = "2.0.0";

        /// <summary>
        /// Maps every legacy <c>(swatch, colorId)</c> pair in measured use to its canonical token, with the
        /// shipped lifecycle stamped onto each entry.
        /// </summary>
        private static List<LegacyColorAlias> BuildLegacyAliases()
        {
            var stamped = new List<LegacyColorAlias>();
            foreach (var mapping in BuildLegacyAliasMappings())
            {
                stamped.Add(new LegacyColorAlias(mapping.LegacySwatchName, mapping.LegacyColorId,
                    mapping.CanonicalTokenId, mapping.Note, AliasesDeprecatedSince, AliasesRemovableIn));
            }

            return stamped;
        }

        /// <summary>
        /// The mapping table itself: every legacy pair in measured use, and why it maps where it does.
        /// </summary>
        /// <remarks>
        /// All 18 keys the usage inventory found in serialized content, plus the four unused keys that map
        /// cleanly onto a primitive. The <c>White.*</c> family is deliberately absent: 0 of its 5 keys are
        /// referenced anywhere, so it is dropped rather than carried forward.
        /// <para/>
        /// Lifecycle fields are deliberately absent here and stamped by <see cref="BuildLegacyAliases"/>,
        /// so this stays a table of mappings and rationale.
        /// </remarks>
        private static List<LegacyColorAlias> BuildLegacyAliasMappings() => new List<LegacyColorAlias>
        {
            new LegacyColorAlias("Default", "Text", "text/primary",
                "63 uses, 55 of them TextMeshPro. The 8 Image uses inherit the text colour as a fill; "
                + "review whether those want surface/wash-subtle instead."),
            new LegacyColorAlias("Default", "Primary", "action/primary/fill",
                "26 uses, 23 of them Image. The 3 TextMeshPro uses want text/accent — same colour, so "
                + "rendering is unchanged either way."),
            new LegacyColorAlias("Default", "Secondary", "surface/panel",
                "Its RGB is identical to Default.Background: never a secondary brand colour, but the "
                + "surface base at full alpha."),
            new LegacyColorAlias("Default", "Accent", "surface/raised",
                "9 uses, all Image, and a distinct chrome tone rather than a brand accent."),
            new LegacyColorAlias("Default", "Background", "surface/canvas", "19 uses, all Image."),
            new LegacyColorAlias("Default", "Disabled", "action/disabled/fill",
                "Background RGB at 0.39 alpha; used only as a ColorIDButton disabled state."),
            new LegacyColorAlias("Default", "Error", "status/error/text", "5 uses, 4 of them TextMeshPro."),
            new LegacyColorAlias("Default", "Warning", "status/warning/fill",
                "One use, a 3D Renderer — the only material-path binding in the project."),
            new LegacyColorAlias("Default", "Success", "status/success/fill",
                "Unused in content; aliased so any future reference resolves."),

            new LegacyColorAlias("Text", "100", "text/primary",
                "Byte-identical to Default.Text at 8 bits in both variants, so the two collapse."),
            new LegacyColorAlias("Text", "80", "surface/wash-strong", "4 uses, 3 of them Image."),
            new LegacyColorAlias("Text", "60", "text/muted",
                "23 uses, 17 of them TextMeshPro. Dark is byte-identical; Light is deliberately 0.67 rather "
                + "than V1's 0.60, which failed WCAG AA at 3.80:1."),
            new LegacyColorAlias("Text", "40", "text/subtle",
                "9 uses, 6 of them TextMeshPro. Dark is byte-identical; Light is deliberately 0.53 rather "
                + "than V1's 0.40, which failed even the large-text threshold at 2.28:1."),
            new LegacyColorAlias("Text", "20", "surface/wash-subtle",
                "8 uses and every one is an Image. Despite the swatch name this is a fill, not text."),
            new LegacyColorAlias("Text", "Secondary", "text/muted",
                "A pair the V1 vocabulary never defined — Text carried only {20,40,60,80,100} — so it has "
                + "always resolved to the magenta sentinel. One use: Confirmation Detailed's Cancel button, "
                + "overriding an instance of Button.prefab whose own ColorID is Text.60. Since Text.60 "
                + "already aliases to text/muted, whose authored description is literally \"Secondary "
                + "text\", the override was the author naming the source's own colour and mistyping the "
                + "key. Aliased here rather than deleted so the intent survives; it renders as the source "
                + "always intended. Decided 2026-08-02."),

            new LegacyColorAlias("Gray", "20", "surface/sunken", "8 uses, all Image."),
            new LegacyColorAlias("Gray", "60", "border/default", "5 uses, all Image."),
            new LegacyColorAlias("Gray", "100", "action/pressed/fill",
                "One use, a ColorIDButton pressed state."),
            new LegacyColorAlias("Gray", "0", $"{NeutralRampPrefix}0", "Unused; maps to a primitive."),
            new LegacyColorAlias("Gray", "40", $"{NeutralRampPrefix}400", "Unused; maps to a primitive."),
            new LegacyColorAlias("Gray", "80", $"{NeutralRampPrefix}800", "Unused; maps to a primitive."),

            new LegacyColorAlias("Black", "40", "surface/scrim",
                "4 uses split 2 Image / 2 TextMeshPro. Black at 40% is an overlay idiom, so it maps to a "
                + "scrim; the 2 text uses are the least certain mapping in this table and want review."),
            new LegacyColorAlias("Black", "60", "surface/scrim-medium",
                "Missed by the first vocabulary pass: the Phase 0 inventory's text scan does not see a "
                + "legacy pair carried as a prefab-instance override, and EnterPIN's Cancel Button carries "
                + "one. It was therefore resolving to the magenta sentinel under V2. Black at 0.6 alpha, "
                + "so the alias restores exactly what V1 rendered."),
            new LegacyColorAlias("Black", "80", "surface/scrim-strong", "2 uses, both Image."),
            new LegacyColorAlias("Black", "100", $"{NeutralRampPrefix}1000",
                "Missed for the same reason as Black.60 — carried only as a prefab-instance override, which "
                + "the inventory's text scan does not see — so it too was resolving to magenta. One use: "
                + "ContentPackage List Item's pressed state. The scrim ramp stops at 80 because a scrim is "
                + "by definition translucent, so fully opaque black maps to the neutral primitive instead. "
                + "Decided 2026-08-02.")
        };

        /// <summary>
        /// The authored accessibility contract.
        /// </summary>
        /// <remarks>
        /// Severities reflect what the shipped values actually achieve, measured rather than aspired to.
        /// Pairs that pass today are locked at <see cref="ColorContrastSeverity.Error"/> so a future edit
        /// cannot quietly break them; pairs that already fail are recorded as
        /// <see cref="ColorContrastSeverity.Warning"/> with the measured ratio in the rationale, because
        /// raising them is a design decision and shipping a theme whose own build gate fails would be
        /// worse than shipping an honest warning.
        /// <para/>
        /// Every requirement against <c>surface/canvas</c> names an under-surface, because that token is
        /// translucent — without one the result is reported incomplete rather than guessed.
        /// </remarks>
        private static List<ColorContrastRequirement> BuildContrastRequirements() =>
            new List<ColorContrastRequirement>
            {
                new ColorContrastRequirement("text/primary", "surface/canvas",
                    ColorContrast.MinimumNormalText, ColorContrastSeverity.Error, ShellBase,
                    "Body text on the window background. Measures about 14:1 in Dark."),

                new ColorContrastRequirement("text/primary", "surface/panel",
                    ColorContrast.MinimumNormalText, ColorContrastSeverity.Error, null,
                    "Body text on an opaque panel."),

                new ColorContrastRequirement("text/muted", "surface/canvas",
                    ColorContrast.MinimumNormalText, ColorContrastSeverity.Error, ShellBase,
                    "Secondary text on the window background. Measures 5.86:1 in Dark. Light failed at "
                    + "3.80:1 under V1's single-alpha value; raising the Light alpha to 0.67 reaches "
                    + "4.62:1, so this is now locked at Error in both variants."),

                new ColorContrastRequirement("text/subtle", "surface/canvas",
                    ColorContrast.MinimumLargeText, ColorContrastSeverity.Error, ShellBase,
                    "De-emphasised text, held to the large-text threshold by design. Measures 3.38:1 in "
                    + "Dark. Light failed at 2.28:1 under V1's single-alpha value; raising the Light alpha "
                    + "to 0.53 reaches 3.16:1. Locked at Error so neither variant can regress."),

                new ColorContrastRequirement("action/primary/on-fill", "action/primary/fill",
                    ColorContrast.MinimumNormalText, ColorContrastSeverity.Error, null,
                    "Label on a primary button. The per-variant alias is what makes this pass in both."),

                new ColorContrastRequirement("status/error/text", "surface/canvas",
                    ColorContrast.MinimumLargeText, ColorContrastSeverity.Warning, ShellBase,
                    "Error text on the window background measures about 4.0:1 in Dark — short of 4.5:1. "
                    + "Recorded as a warning rather than an error because this is the shipped V1 value; "
                    + "darkening the red or lightening the canvas is a design decision.")
            };
    }
}
#endif
