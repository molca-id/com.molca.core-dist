#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>The kind of change a transaction performs.</summary>
    public enum ColorThemeTransactionKind
    {
        /// <summary>Change a token's canonical ID, optionally leaving an alias behind.</summary>
        RenameToken,

        /// <summary>Remove a token, repointing its references at a replacement.</summary>
        DeleteTokenWithReplacement,

        /// <summary>Add a token and give every variant a value for it.</summary>
        AddToken,

        /// <summary>Add a variant, seeded from an existing one.</summary>
        AddVariant,

        /// <summary>Remove a variant, leaving the token contract untouched.</summary>
        RemoveVariant,

        /// <summary>Map a legacy pair to a canonical token.</summary>
        AddLegacyAlias
    }

    /// <summary>One mutation a plan intends to make.</summary>
    public sealed class ColorThemePlannedChange
    {
        /// <summary>Where the change happens.</summary>
        public string AssetPath { get; }

        /// <summary>What the change is, in author-facing terms.</summary>
        public string Description { get; }

        /// <summary>Whether the executor can actually perform it.</summary>
        public bool IsWritable { get; }

        /// <summary>Why it cannot be performed, when <see cref="IsWritable"/> is <c>false</c>.</summary>
        public string BlockedReason { get; }

        /// <summary>Creates a planned change.</summary>
        public ColorThemePlannedChange(string assetPath, string description, bool isWritable,
            string blockedReason = null)
        {
            AssetPath = assetPath;
            Description = description;
            IsWritable = isWritable;
            BlockedReason = blockedReason;
        }

        /// <inheritdoc/>
        public override string ToString() => IsWritable
            ? $"{AssetPath}: {Description}"
            : $"{AssetPath}: {Description} — BLOCKED: {BlockedReason}";
    }

    /// <summary>
    /// A previewed, fingerprint-bound plan. Building one changes nothing.
    /// </summary>
    /// <remarks>
    /// A plan carries the audit fingerprint it was built from. <see cref="ColorThemeTransactionExecutor"/>
    /// refuses to apply a plan whose fingerprint no longer matches a fresh audit, which is what stops a
    /// plan reviewed against one state from being applied to a different one — the palette or the content
    /// may have changed in between, and a rename applied to changed data can repoint the wrong sites.
    /// </remarks>
    public sealed class ColorThemeTransactionPlan
    {
        /// <summary>What this plan does.</summary>
        public ColorThemeTransactionKind Kind { get; }

        /// <summary>The theme set the plan targets.</summary>
        public ColorThemeSet ThemeSet { get; }

        /// <summary>The audit fingerprint this plan was built against.</summary>
        public string SnapshotFingerprint { get; }

        /// <summary>Every intended mutation, writable and blocked alike.</summary>
        public IReadOnlyList<ColorThemePlannedChange> Changes { get; }

        /// <summary>Reasons the plan cannot be applied at all, or empty.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>Non-blocking notes an author should read before approving.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Opaque parameters the executor needs.</summary>
        internal IReadOnlyDictionary<string, string> Parameters { get; }

        /// <summary>Whether the plan is applicable.</summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary>Changes the executor will perform.</summary>
        public int WritableChangeCount
        {
            get
            {
                int count = 0;
                foreach (var change in Changes) if (change.IsWritable) count++;
                return count;
            }
        }

        /// <summary>
        /// Changes that cannot be performed, almost always because the site is package-owned.
        /// </summary>
        /// <remarks>
        /// Reported rather than attempted. An installed package is read-only to the project that
        /// installs it, and the shipped content is overwhelmingly package-owned — so this count is
        /// usually large and is the main reason a rename keeps a compatibility alias.
        /// </remarks>
        public int BlockedChangeCount => Changes.Count - WritableChangeCount;

        internal ColorThemeTransactionPlan(ColorThemeTransactionKind kind, ColorThemeSet themeSet,
            string snapshotFingerprint, List<ColorThemePlannedChange> changes, List<string> errors,
            List<string> warnings, Dictionary<string, string> parameters)
        {
            Kind = kind;
            ThemeSet = themeSet;
            SnapshotFingerprint = snapshotFingerprint;
            Changes = changes ?? new List<ColorThemePlannedChange>();
            Errors = errors ?? new List<string>();
            Warnings = warnings ?? new List<string>();
            Parameters = parameters ?? new Dictionary<string, string>();
        }

        /// <summary>A human-readable preview for the Hub or a console.</summary>
        public string ToPreview()
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"{Kind} on '{ThemeSet?.DisplayName}'");
            if (Errors.Count > 0)
            {
                builder.AppendLine("BLOCKED:");
                foreach (string error in Errors) builder.AppendLine($"  - {error}");
                return builder.ToString();
            }

            builder.AppendLine($"{WritableChangeCount} change(s) will be applied, "
                               + $"{BlockedChangeCount} reported read-only.");
            foreach (string warning in Warnings) builder.AppendLine($"  ! {warning}");
            foreach (var change in Changes) builder.AppendLine($"  {change}");
            return builder.ToString();
        }
    }

    /// <summary>
    /// Builds previews of theme-set changes. Never mutates anything.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Transactions/</c>.
    /// <b>Shape:</b> editor-only static service, invoked explicitly by Theme Studio or MCP authoring.
    /// <para/>
    /// Planning and applying are separate types on purpose: it makes "nothing happened yet" a property of
    /// which class you called rather than of which argument you passed.
    /// </remarks>
    public static class ColorThemeTransactionPlanner
    {
        /// <summary>Plans a token rename.</summary>
        /// <param name="snapshot">A fresh audit, which binds the plan and supplies the usage index.</param>
        /// <param name="fromTokenId">The token to rename.</param>
        /// <param name="toTokenId">The new canonical ID.</param>
        /// <param name="createAlias">
        /// Whether to leave a migration alias so references that cannot be rewritten keep resolving.
        /// </param>
        public static ColorThemeTransactionPlan PlanRenameToken(ColorThemeAuditSnapshot snapshot,
            string fromTokenId, string toTokenId, bool createAlias = true)
        {
            var changes = new List<ColorThemePlannedChange>();
            var errors = new List<string>();
            var warnings = new List<string>();
            var themeSet = snapshot?.ThemeSet;

            if (themeSet == null)
            {
                errors.Add("No theme set to modify.");
                return Plan(ColorThemeTransactionKind.RenameToken, null, snapshot, changes, errors,
                    warnings, null);
            }

            if (themeSet.GetDefinition(fromTokenId) == null)
                errors.Add($"Theme set does not declare '{fromTokenId}'.");

            if (!ColorTokenId.Validate(toTokenId, out string idError))
                errors.Add($"'{toTokenId}' is not a canonical token ID: {idError}");
            else if (themeSet.GetDefinition(toTokenId) != null)
                errors.Add($"'{toTokenId}' already exists; renaming onto it would merge two tokens.");

            if (errors.Count > 0)
            {
                return Plan(ColorThemeTransactionKind.RenameToken, themeSet, snapshot, changes, errors,
                    warnings, null);
            }

            string themeSetPath = AssetDatabase.GetAssetPath(themeSet);
            changes.Add(new ColorThemePlannedChange(themeSetPath,
                $"rename token '{fromTokenId}' to '{toTokenId}' and update every variant value, alias "
                + "and contrast requirement that names it", true));

            var sites = snapshot.GetSitesForToken(fromTokenId);
            int blocked = 0;
            foreach (var site in sites)
            {
                bool writable = site.IsWritable && site.Kind == ColorThemeUsageKind.CanonicalTokenReference;
                string reason = null;

                if (site.IsPackageOwned)
                {
                    reason = "package-owned; an installed package is read-only to this project";
                    blocked++;
                }
                else if (site.Kind != ColorThemeUsageKind.CanonicalTokenReference)
                {
                    // A legacy pair does not name a token directly, so a rename does not touch it; the
                    // alias map is what keeps it resolving.
                    reason = "legacy reference — resolves through the alias map, not the token ID";
                    blocked++;
                }

                changes.Add(new ColorThemePlannedChange(site.AssetPath,
                    $"repoint {site.Kind} to '{toTokenId}'", writable, reason));
            }

            if (createAlias)
            {
                warnings.Add($"A migration alias will be created so references still naming "
                             + $"'{fromTokenId}' keep resolving.");
            }
            else if (blocked > 0)
            {
                warnings.Add($"{blocked} reference(s) cannot be rewritten and no alias was requested — "
                             + "those sites will stop resolving. Create an alias unless that is intended.");
            }

            return Plan(ColorThemeTransactionKind.RenameToken, themeSet, snapshot, changes, errors,
                warnings, new Dictionary<string, string>
                {
                    ["from"] = fromTokenId,
                    ["to"] = toTokenId,
                    ["createAlias"] = createAlias ? "true" : "false"
                });
        }

        /// <summary>Plans adding a token with a value in every variant.</summary>
        /// <param name="snapshot">A fresh audit, which binds the plan.</param>
        /// <param name="tokenId">The canonical ID to add.</param>
        /// <param name="initialColor">The literal colour every variant starts with.</param>
        /// <param name="kind">Primitive or semantic.</param>
        /// <param name="usage">What the token colours.</param>
        /// <param name="required">Whether every variant must resolve it.</param>
        /// <remarks>
        /// Adding a token is a single transaction across <i>all</i> variants precisely because a token
        /// present in one variant and absent from another is the V1 defect this model exists to prevent.
        /// </remarks>
        public static ColorThemeTransactionPlan PlanAddToken(ColorThemeAuditSnapshot snapshot,
            string tokenId, Color initialColor, ColorTokenKind kind = ColorTokenKind.Semantic,
            ColorTokenUsage usage = ColorTokenUsage.None, bool required = true)
        {
            var changes = new List<ColorThemePlannedChange>();
            var errors = new List<string>();
            var themeSet = snapshot?.ThemeSet;

            if (themeSet == null) errors.Add("No theme set to modify.");
            else
            {
                if (!ColorTokenId.Validate(tokenId, out string idError))
                    errors.Add($"'{tokenId}' is not a canonical token ID: {idError}");
                else if (themeSet.GetDefinition(tokenId) != null)
                    errors.Add($"'{tokenId}' already exists.");
            }

            if (errors.Count == 0)
            {
                string path = AssetDatabase.GetAssetPath(themeSet);
                changes.Add(new ColorThemePlannedChange(path,
                    $"declare token '{tokenId}' ({kind}, {usage}, required={required})", true));
                foreach (string variantId in themeSet.GetVariantIds())
                {
                    changes.Add(new ColorThemePlannedChange(path,
                        $"give variant '{variantId}' a literal value for '{tokenId}'", true));
                }
            }

            return Plan(ColorThemeTransactionKind.AddToken, themeSet, snapshot, changes, errors,
                new List<string>(), new Dictionary<string, string>
                {
                    ["tokenId"] = tokenId,
                    ["color"] = $"{initialColor.r},{initialColor.g},{initialColor.b},{initialColor.a}",
                    ["kind"] = kind.ToString(),
                    ["usage"] = usage.ToString(),
                    ["required"] = required ? "true" : "false"
                });
        }

        /// <summary>Plans mapping a legacy pair to a canonical token.</summary>
        /// <param name="snapshot">A fresh audit, which binds the plan.</param>
        /// <param name="swatchName">The V1 swatch name.</param>
        /// <param name="colorId">The V1 colour ID.</param>
        /// <param name="canonicalTokenId">The canonical token to map to.</param>
        /// <param name="note">Why this mapping was chosen — recorded on the alias.</param>
        public static ColorThemeTransactionPlan PlanAddLegacyAlias(ColorThemeAuditSnapshot snapshot,
            string swatchName, string colorId, string canonicalTokenId, string note = null)
        {
            var changes = new List<ColorThemePlannedChange>();
            var errors = new List<string>();
            var themeSet = snapshot?.ThemeSet;
            var key = new LegacyColorKey(swatchName, colorId);

            if (themeSet == null) errors.Add("No theme set to modify.");
            else
            {
                if (!key.IsAssigned) errors.Add("The legacy pair is incomplete.");
                if (themeSet.GetDefinition(canonicalTokenId) == null)
                    errors.Add($"Theme set does not declare '{canonicalTokenId}'.");
                if (themeSet.ResolveLegacyToken(key) != null)
                    errors.Add($"Legacy pair '{key}' is already mapped.");
            }

            if (errors.Count == 0)
            {
                changes.Add(new ColorThemePlannedChange(AssetDatabase.GetAssetPath(themeSet),
                    $"map legacy '{key}' to '{canonicalTokenId}'", true));
            }

            return Plan(ColorThemeTransactionKind.AddLegacyAlias, themeSet, snapshot, changes, errors,
                new List<string>(), new Dictionary<string, string>
                {
                    ["swatch"] = swatchName,
                    ["colorId"] = colorId,
                    ["canonical"] = canonicalTokenId,
                    ["note"] = note ?? string.Empty
                });
        }

        private static ColorThemeTransactionPlan Plan(ColorThemeTransactionKind kind,
            ColorThemeSet themeSet, ColorThemeAuditSnapshot snapshot,
            List<ColorThemePlannedChange> changes, List<string> errors, List<string> warnings,
            Dictionary<string, string> parameters) =>
            new ColorThemeTransactionPlan(kind, themeSet, snapshot?.Fingerprint, changes, errors,
                warnings, parameters);
    }

    /// <summary>The outcome of applying a plan.</summary>
    public sealed class ColorThemeTransactionResult
    {
        /// <summary>Whether the plan was applied.</summary>
        public bool Applied { get; }

        /// <summary>Why it was not, when it was not.</summary>
        public string RejectionReason { get; }

        /// <summary>Changes actually performed.</summary>
        public int AppliedChangeCount { get; }

        /// <summary>Changes reported but not performed, almost always package-owned sites.</summary>
        public IReadOnlyList<ColorThemePlannedChange> RemainingChanges { get; }

        /// <summary>A fresh audit taken after applying, or <c>null</c> when nothing was applied.</summary>
        public ColorThemeAuditSnapshot PostAudit { get; }

        internal ColorThemeTransactionResult(bool applied, string rejectionReason,
            int appliedChangeCount, IReadOnlyList<ColorThemePlannedChange> remaining,
            ColorThemeAuditSnapshot postAudit)
        {
            Applied = applied;
            RejectionReason = rejectionReason;
            AppliedChangeCount = appliedChangeCount;
            RemainingChanges = remaining ?? Array.Empty<ColorThemePlannedChange>();
            PostAudit = postAudit;
        }

        /// <inheritdoc/>
        public override string ToString() => Applied
            ? $"Applied {AppliedChangeCount} change(s); {RemainingChanges.Count} remaining. "
              + $"Post-audit: {PostAudit}"
            : $"Rejected: {RejectionReason}";
    }

    /// <summary>
    /// Applies a plan, or refuses to.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Transactions/</c>.
    /// <b>Shape:</b> editor-only static service, invoked explicitly after a human or a tool approves a
    /// preview.
    /// <para/>
    /// Three guarantees:
    /// <list type="number">
    /// <item><description>
    /// <b>Stale plans are refused.</b> A fresh audit is taken and its fingerprint compared with the
    /// plan's; a mismatch aborts before anything is written.
    /// </description></item>
    /// <item><description>
    /// <b>Package-owned sites are never touched.</b> They are reported in
    /// <see cref="ColorThemeTransactionResult.RemainingChanges"/> so the author can decide between a
    /// package update, a consumer override, or leaving the alias in place.
    /// </description></item>
    /// <item><description>
    /// <b>Changes to loaded objects go through Unity Undo</b>, so an approved-then-regretted rename is
    /// one Ctrl+Z rather than a manual repair.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class ColorThemeTransactionExecutor
    {
        /// <summary>Applies a plan after re-verifying it.</summary>
        /// <param name="plan">The approved plan.</param>
        /// <returns>What happened, including a post-apply audit.</returns>
        public static ColorThemeTransactionResult Apply(ColorThemeTransactionPlan plan)
        {
            if (plan == null) return Reject("No plan supplied.");
            if (!plan.IsValid) return Reject($"Plan is not applicable: {string.Join("; ", plan.Errors)}");
            if (plan.ThemeSet == null) return Reject("Plan has no theme set.");

            // Re-audit before touching anything: the project may have changed since the preview the
            // author approved.
            var fresh = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);
            if (!string.Equals(fresh.Fingerprint, plan.SnapshotFingerprint, StringComparison.Ordinal))
            {
                return Reject(
                    "The project changed after this plan was previewed (audit fingerprint "
                    + $"'{plan.SnapshotFingerprint}' is now '{fresh.Fingerprint}'). Re-plan and review "
                    + "again — applying against changed data could repoint the wrong sites.");
            }

            var themeSet = plan.ThemeSet;
            Undo.RecordObject(themeSet, $"Colour theme: {plan.Kind}");

            int applied;
            try
            {
                applied = plan.Kind switch
                {
                    ColorThemeTransactionKind.RenameToken => ApplyRename(plan, themeSet),
                    ColorThemeTransactionKind.AddToken => ApplyAddToken(plan, themeSet),
                    ColorThemeTransactionKind.AddLegacyAlias => ApplyAddAlias(plan, themeSet),
                    _ => -1
                };
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return Reject($"Applying failed: {exception.Message}");
            }

            if (applied < 0) return Reject($"{plan.Kind} is not implemented yet.");

            themeSet.InvalidateIndexes();
            EditorUtility.SetDirty(themeSet);
            AssetDatabase.SaveAssets();

            var remaining = new List<ColorThemePlannedChange>();
            foreach (var change in plan.Changes) if (!change.IsWritable) remaining.Add(change);

            // Rescan so the caller sees the state its change produced, not the state it planned against.
            var postAudit = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);
            return new ColorThemeTransactionResult(true, null, applied, remaining, postAudit);
        }

        private static int ApplyRename(ColorThemeTransactionPlan plan, ColorThemeSet themeSet)
        {
            string from = plan.Parameters["from"];
            string to = plan.Parameters["to"];
            bool createAlias = plan.Parameters["createAlias"] == "true";

            int changes = ColorThemeSetEditing.RenameToken(themeSet, from, to);

            if (createAlias)
            {
                // The legacy spelling of a canonical ID is its dotted first/rest split, which is what a
                // pre-rename serialized reference would have carried.
                int separator = from.IndexOf(ColorTokenId.Separator);
                if (separator > 0)
                {
                    ColorThemeSetEditing.AddAlias(themeSet, from.Substring(0, separator),
                        from.Substring(separator + 1), to,
                        $"Auto-created by a rename from '{from}'.");
                    changes++;
                }
            }

            return changes;
        }

        private static int ApplyAddToken(ColorThemeTransactionPlan plan, ColorThemeSet themeSet)
        {
            string tokenId = plan.Parameters["tokenId"];
            var parts = plan.Parameters["color"].Split(',');
            var color = new Color(
                float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));

            return ColorThemeSetEditing.AddToken(themeSet, tokenId, color,
                (ColorTokenKind)Enum.Parse(typeof(ColorTokenKind), plan.Parameters["kind"]),
                (ColorTokenUsage)Enum.Parse(typeof(ColorTokenUsage), plan.Parameters["usage"]),
                plan.Parameters["required"] == "true");
        }

        private static int ApplyAddAlias(ColorThemeTransactionPlan plan, ColorThemeSet themeSet) =>
            ColorThemeSetEditing.AddAlias(themeSet, plan.Parameters["swatch"], plan.Parameters["colorId"],
                plan.Parameters["canonical"], plan.Parameters["note"]);

        private static ColorThemeTransactionResult Reject(string reason) =>
            new ColorThemeTransactionResult(false, reason, 0, null, null);
    }
}
#endif
