#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Molca.ColorID;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.ColorID.Editor
{
    /// <summary>Why one <see cref="ColorID"/> component cannot be migrated.</summary>
    public enum ColorMigrationBlock
    {
        /// <summary>Nothing blocks it.</summary>
        None = 0,

        /// <summary>The owning asset is an immutable installed package.</summary>
        AssetNotWritable,

        /// <summary>The legacy pair has no authored alias, so there is no token to migrate it to.</summary>
        NoCanonicalToken,

        /// <summary>
        /// Another component in the same asset holds a serialized reference to this
        /// <see cref="ColorID"/>, so removing it would break that component.
        /// </summary>
        ReferencedByAnotherComponent,

        /// <summary>The object already carries a <see cref="ColorThemeBinding"/>.</summary>
        AlreadyMigrated,

        /// <summary>No component on the object (or its descendants) can take a colour.</summary>
        NoColorTargets,

        /// <summary>
        /// The object carries several <see cref="ColorID"/> components naming different colours, so what
        /// it should become is ambiguous.
        /// </summary>
        /// <remarks>
        /// V1 resolved this by execution order — whichever component applied last won. There is no correct
        /// automatic translation of that into a single binding, so it is reported for a human to resolve by
        /// deleting the redundant component. Several components naming the <i>same</i> colour are merely
        /// redundant and are migrated as one.
        /// </remarks>
        ConflictingLegacyComponents,

        /// <summary>
        /// The object belongs to a nested prefab instance, so it is not this asset's to rewrite.
        /// </summary>
        /// <remarks>
        /// Loading a prefab's contents materializes the prefabs it nests, so their <see cref="ColorID"/>
        /// components are reachable here even though this file does not contain them — it holds a
        /// reference and a list of overrides. Migrating one would add the binding as an <i>override</i> on
        /// the instance and record the removed <see cref="ColorID"/> as another, duplicating the change
        /// into every prefab that nests the same source. The source prefab's own migration handles it once.
        /// This is also why a plan can list far fewer sites than a naive component count suggests.
        /// </remarks>
        PartOfNestedPrefab,

        /// <summary>
        /// The object belongs to a nested prefab instance <i>and</i> overrides its colour, so migrating
        /// the source prefab would silently drop that override.
        /// </summary>
        /// <remarks>
        /// Separated from <see cref="PartOfNestedPrefab"/> because the two need different things from an
        /// author. A plain nested site needs nothing: migrating its source handles it. An overridden one
        /// is a colour this asset chose and the source does not have — migrating the source gives the
        /// instance the source's token, and the override is lost. Someone has to decide whether that
        /// colour still matters, and re-express it as a binding override if it does.
        /// <para/>
        /// This is also where the two apparently-unaliased pairs in this project live, which is why the
        /// audit's text scan never reported them: an override is serialized as a
        /// <c>propertyPath</c>/<c>value</c> modification, not as the field pair the scan matches.
        /// </remarks>
        NestedPrefabColorOverride
    }

    /// <summary>One <see cref="ColorID"/> component the migration considered.</summary>
    public sealed class ColorMigrationSite
    {
        /// <summary>Project-relative path of the prefab or scene.</summary>
        public string AssetPath { get; }

        /// <summary>
        /// Unambiguous locator for the GameObject inside that asset.
        /// </summary>
        /// <remarks>
        /// Carries a sibling index per level (<c>Panel/Row[2]/Icon[0]</c>) rather than names alone. Real
        /// content routinely has identically named siblings — five objects called <c>Divider</c> under one
        /// parent — and a name-only path would make the apply step address the wrong one, or several at
        /// once.
        /// </remarks>
        public string ObjectPath { get; }

        /// <summary>How many <see cref="ColorID"/> components on the object this site covers.</summary>
        /// <remarks>
        /// More than one when an object carries redundant components naming the same colour. All of them
        /// are removed together, and one binding set replaces them.
        /// </remarks>
        public int LegacyComponentCount { get; }

        /// <summary>The legacy pair the component carries.</summary>
        public LegacyColorKey LegacyKey { get; }

        /// <summary>The canonical token it would migrate to, or <c>null</c>.</summary>
        public string CanonicalTokenId { get; }

        /// <summary>How many bindings would be written.</summary>
        public int TargetCount { get; }

        /// <summary>Why it cannot be migrated, or <see cref="ColorMigrationBlock.None"/>.</summary>
        public ColorMigrationBlock Block { get; }

        /// <summary>Author-facing detail behind <see cref="Block"/>, or <c>null</c>.</summary>
        public string BlockDetail { get; }

        /// <summary>Creates a site.</summary>
        public ColorMigrationSite(string assetPath, string objectPath, LegacyColorKey legacyKey,
            string canonicalTokenId, int targetCount, ColorMigrationBlock block, string blockDetail = null,
            int legacyComponentCount = 1)
        {
            AssetPath = assetPath;
            ObjectPath = objectPath;
            LegacyKey = legacyKey;
            CanonicalTokenId = canonicalTokenId;
            TargetCount = targetCount;
            Block = block;
            BlockDetail = blockDetail;
            LegacyComponentCount = legacyComponentCount;
        }

        /// <summary>Whether this site would be rewritten.</summary>
        public bool IsMigratable => Block == ColorMigrationBlock.None;

        /// <inheritdoc/>
        public override string ToString() => IsMigratable
            ? $"{AssetPath}:{ObjectPath} — {LegacyKey} -> {CanonicalTokenId} ({TargetCount} target(s))"
            : $"{AssetPath}:{ObjectPath} — {LegacyKey} BLOCKED: {Block}"
              + (string.IsNullOrEmpty(BlockDetail) ? "" : $" ({BlockDetail})");
    }

    /// <summary>What a migration run is allowed to touch.</summary>
    public sealed class ColorContentMigrationOptions
    {
        /// <summary>
        /// Only assets whose path starts with one of these prefixes are considered. Empty means all.
        /// </summary>
        /// <remarks>
        /// This is what makes the plan's §16.6 batches real: a batch is a path filter, previewed and
        /// applied on its own, so a regression is attributable to one batch rather than to "the migration".
        /// </remarks>
        public IReadOnlyList<string> PathPrefixes { get; }

        /// <summary>Whether to scan scenes as well as prefabs.</summary>
        /// <remarks>
        /// Off by default. Migrating a scene requires opening it, which is far more disruptive than
        /// loading prefab contents and is worth opting into deliberately.
        /// </remarks>
        public bool IncludeScenes { get; }

        /// <summary>
        /// Whether the <see cref="ColorID"/> component is removed once its bindings exist.
        /// </summary>
        /// <remarks>
        /// On by default, and the safer choice despite sounding like the riskier one: leaving both
        /// components on an object means two systems writing the same colour channel, and which one wins
        /// depends on execution order. Turn it off only to stage a migration you intend to verify visually
        /// before deleting anything.
        /// </remarks>
        public bool RemoveLegacyComponent { get; }

        /// <summary>Creates options.</summary>
        /// <param name="pathPrefixes">Project-relative path prefixes, or <c>null</c> for all.</param>
        /// <param name="includeScenes">Whether to scan scenes.</param>
        /// <param name="removeLegacyComponent">Whether to remove the migrated <see cref="ColorID"/>.</param>
        public ColorContentMigrationOptions(IReadOnlyList<string> pathPrefixes = null,
            bool includeScenes = false, bool removeLegacyComponent = true)
        {
            PathPrefixes = pathPrefixes ?? Array.Empty<string>();
            IncludeScenes = includeScenes;
            RemoveLegacyComponent = removeLegacyComponent;
        }

        /// <summary>Prefabs everywhere, legacy components removed.</summary>
        public static ColorContentMigrationOptions AllPrefabs => new ColorContentMigrationOptions();

        /// <summary>Whether a path is in scope.</summary>
        /// <param name="assetPath">A project-relative asset path.</param>
        public bool Includes(string assetPath)
        {
            if (PathPrefixes.Count == 0) return true;
            foreach (string prefix in PathPrefixes)
            {
                if (assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>A previewed content migration. Building one changes nothing.</summary>
    public sealed class ColorContentMigrationPlan
    {
        /// <summary>The audit fingerprint this plan was built against.</summary>
        public string SnapshotFingerprint { get; }

        /// <summary>What the run was allowed to touch.</summary>
        public ColorContentMigrationOptions Options { get; }

        /// <summary>Every site considered, migratable and blocked alike.</summary>
        public IReadOnlyList<ColorMigrationSite> Sites { get; }

        /// <summary>Reasons the plan cannot be applied at all, or empty.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>Creates a plan.</summary>
        public ColorContentMigrationPlan(string snapshotFingerprint,
            ColorContentMigrationOptions options, IReadOnlyList<ColorMigrationSite> sites,
            IReadOnlyList<string> errors)
        {
            SnapshotFingerprint = snapshotFingerprint;
            Options = options;
            Sites = sites ?? Array.Empty<ColorMigrationSite>();
            Errors = errors ?? Array.Empty<string>();
        }

        /// <summary>Whether the plan is applicable.</summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary>Sites that would be rewritten.</summary>
        public IEnumerable<ColorMigrationSite> Migratable => Sites.Where(s => s.IsMigratable);

        /// <summary>Sites that would not.</summary>
        public IEnumerable<ColorMigrationSite> Blocked => Sites.Where(s => !s.IsMigratable);

        /// <summary>Assets the apply step would open and write.</summary>
        public IEnumerable<string> AffectedAssets =>
            Migratable.Select(s => s.AssetPath).Distinct().OrderBy(p => p, StringComparer.Ordinal);

        /// <summary>A human-readable preview.</summary>
        public string ToPreview()
        {
            var text = new StringBuilder("[ColorTheme] Content migration plan\n");

            foreach (string error in Errors) text.AppendLine($"  ERROR: {error}");

            int migratable = Migratable.Count();
            text.AppendLine($"  {migratable} component(s) in {AffectedAssets.Count()} asset(s) would be "
                            + $"migrated; {Sites.Count - migratable} blocked.");
            text.AppendLine($"  Legacy ColorID components are "
                            + (Options.RemoveLegacyComponent ? "removed" : "kept") + " after migration.");

            foreach (var group in Migratable.GroupBy(s => s.AssetPath).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                text.AppendLine($"  {group.Key}");
                foreach (var site in group.OrderBy(s => s.ObjectPath, StringComparer.Ordinal))
                {
                    text.AppendLine($"    {site.ObjectPath}: {site.LegacyKey} -> {site.CanonicalTokenId} "
                                    + $"({site.TargetCount} target(s))");
                }
            }

            var blockedByReason = Blocked.GroupBy(s => s.Block).OrderByDescending(g => g.Count());
            foreach (var group in blockedByReason)
            {
                text.AppendLine($"  BLOCKED — {group.Key}: {group.Count()} site(s)");
                foreach (var site in group.Take(10))
                {
                    text.AppendLine($"    {site.AssetPath}:{site.ObjectPath}"
                                    + (string.IsNullOrEmpty(site.BlockDetail) ? "" : $" — {site.BlockDetail}"));
                }
                if (group.Count() > 10) text.AppendLine($"    … and {group.Count() - 10} more");
            }

            return text.ToString();
        }
    }

    /// <summary>The outcome of applying a migration plan.</summary>
    public sealed class ColorContentMigrationResult
    {
        /// <summary>Whether anything was written.</summary>
        public bool Applied { get; }

        /// <summary>Why nothing was, when <see cref="Applied"/> is <c>false</c>.</summary>
        public string RejectionReason { get; }

        /// <summary>How many <see cref="ColorID"/> components were replaced.</summary>
        public int MigratedComponentCount { get; }

        /// <summary>How many <see cref="ColorBinding"/> entries were written.</summary>
        public int WrittenBindingCount { get; }

        /// <summary>Assets that were rewritten.</summary>
        public IReadOnlyList<string> WrittenAssets { get; }

        /// <summary>Per-asset failures that did not stop the run.</summary>
        public IReadOnlyList<string> Failures { get; }

        /// <summary>Creates a result.</summary>
        public ColorContentMigrationResult(bool applied, string rejectionReason, int migratedComponentCount,
            int writtenBindingCount, IReadOnlyList<string> writtenAssets, IReadOnlyList<string> failures)
        {
            Applied = applied;
            RejectionReason = rejectionReason;
            MigratedComponentCount = migratedComponentCount;
            WrittenBindingCount = writtenBindingCount;
            WrittenAssets = writtenAssets ?? Array.Empty<string>();
            Failures = failures ?? Array.Empty<string>();
        }

        /// <inheritdoc/>
        public override string ToString() => Applied
            ? $"Migrated {MigratedComponentCount} ColorID component(s) into {WrittenBindingCount} "
              + $"binding(s) across {WrittenAssets.Count} asset(s)."
            : $"Not applied: {RejectionReason}";
    }

    /// <summary>
    /// Converts shipped <see cref="ColorID"/> components into <see cref="ColorThemeBinding"/> components,
    /// as a previewed transaction (revamp plan §16.6).
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Transactions/</c>.
    /// <b>Shape:</b> editor-only static service. Plan, review, then apply.
    /// <para/>
    /// <b>This is the only tool in the colour system that rewrites content.</b> Everything else either
    /// reads (the audit, the reports) or writes theme data (the transaction engine, the interchange
    /// importer). That difference is why the safety rules below are refusals rather than warnings: a bad
    /// rewrite of 200 prefabs is not something a preview catches after the fact.
    /// <para/>
    /// <b>Five refusals, each protecting a specific failure:</b>
    /// <list type="number">
    /// <item><description>
    /// <b>Referenced by another component.</b> <c>ColorIDButton</c> and <c>ButtonState</c> hold a
    /// serialized reference to a <see cref="ColorID"/> and drive it at runtime through
    /// <c>SetColor</c>. Removing that component would leave a null reference and a button that never
    /// changes colour, and no amount of reviewing a diff of 200 prefabs would reliably catch it. Detected
    /// by walking every component's <see cref="SerializedObject"/> in the same asset.
    /// </description></item>
    /// <item><description>
    /// <b>No canonical token.</b> A pair with no authored alias has nothing to migrate <i>to</i>; writing
    /// an unassigned binding would replace a colour that works with one that renders nothing.
    /// </description></item>
    /// <item><description>
    /// <b>Asset not writable.</b> An immutable installed package would discard the edit at the next
    /// resolve, so the change would silently disappear. Uses
    /// <see cref="ColorThemeAssetWriteAccess"/> rather than the audit's stricter path-prefix rule,
    /// because in this repository the Molca packages are embedded and genuinely authored here.
    /// </description></item>
    /// <item><description>
    /// <b>Already migrated.</b> Re-running must be idempotent, and an object with a binding already has
    /// its answer.
    /// </description></item>
    /// <item><description>
    /// <b>No colour targets.</b> An empty binding list looks applied and renders nothing, which is worse
    /// than leaving the legacy component in place.
    /// </description></item>
    /// </list>
    /// <b>Alpha is preserved per target, not per component.</b> A <c>ColorID.ColorTarget</c> with
    /// <c>UseAlpha</c> off pins its own alpha; that becomes <see cref="ColorAlphaPolicy.Explicit"/> on
    /// exactly that binding. Collapsing it to a component-level setting would change what renders.
    /// </remarks>
    public static class ColorContentMigration
    {
        /// <summary>Builds a migration plan. Writes nothing.</summary>
        /// <param name="snapshot">A fresh audit snapshot, for fingerprint binding.</param>
        /// <param name="options">What the run may touch. <c>null</c> means all prefabs.</param>
        /// <returns>The plan.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="snapshot"/> is <c>null</c>.</exception>
        public static ColorContentMigrationPlan Plan(ColorThemeAuditSnapshot snapshot,
            ColorContentMigrationOptions options = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            options = options ?? ColorContentMigrationOptions.AllPrefabs;

            var errors = new List<string>();
            var sites = new List<ColorMigrationSite>();

            var themeSet = snapshot.ThemeSet;
            if (themeSet == null)
            {
                errors.Add("No Color Theme Set is installed, so there is no alias map to migrate through. "
                           + "Install V2 first.");
                return new ColorContentMigrationPlan(snapshot.Fingerprint, options, sites, errors);
            }

            foreach (string assetPath in CollectCandidatePaths(options))
            {
                try
                {
                    if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        PlanPrefab(assetPath, themeSet, sites);
                    else
                        PlanScene(assetPath, themeSet, sites);
                }
                catch (Exception exception)
                {
                    // One unreadable asset must not silently shrink the plan: a plan that quietly omits
                    // sites would report a smaller migration than the project actually needs.
                    errors.Add($"Could not plan '{assetPath}': {exception.Message}");
                }
            }

            sites.Sort((a, b) =>
            {
                int byPath = string.Compare(a.AssetPath, b.AssetPath, StringComparison.Ordinal);
                return byPath != 0
                    ? byPath
                    : string.Compare(a.ObjectPath, b.ObjectPath, StringComparison.Ordinal);
            });

            return new ColorContentMigrationPlan(snapshot.Fingerprint, options, sites, errors);
        }

        /// <summary>Applies a plan, rebinding content in place.</summary>
        /// <param name="plan">A plan built by <see cref="Plan"/>.</param>
        /// <returns>What was written.</returns>
        /// <remarks>
        /// Refuses a plan whose fingerprint no longer matches a fresh audit. Content may have changed since
        /// the plan was reviewed, and a rewrite applied to changed data can rebind objects nobody looked at.
        /// </remarks>
        public static ColorContentMigrationResult Apply(ColorContentMigrationPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            if (!plan.IsValid)
            {
                return new ColorContentMigrationResult(false,
                    "the plan is not valid: " + string.Join("; ", plan.Errors), 0, 0, null, null);
            }

            var fresh = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);
            if (fresh.Fingerprint != plan.SnapshotFingerprint)
            {
                return new ColorContentMigrationResult(false,
                    "the project changed since this plan was built, so it was not applied. Re-plan and "
                    + "review again.", 0, 0, null, null);
            }

            var written = new List<string>();
            var failures = new List<string>();
            int migrated = 0;
            int bindings = 0;

            foreach (var group in plan.Migratable.GroupBy(s => s.AssetPath))
            {
                try
                {
                    int assetBindings;
                    int assetMigrated = group.Key.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                        ? ApplyToPrefab(group.Key, group.ToList(), plan.Options, out assetBindings)
                        : ApplyToScene(group.Key, group.ToList(), plan.Options, out assetBindings);

                    if (assetMigrated > 0)
                    {
                        migrated += assetMigrated;
                        bindings += assetBindings;
                        written.Add(group.Key);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"{group.Key}: {exception.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new ColorContentMigrationResult(true, null, migrated, bindings, written, failures);
        }

        #region Planning

        private static IEnumerable<string> CollectCandidatePaths(ColorContentMigrationOptions options)
        {
            string filter = options.IncludeScenes ? "t:Prefab t:Scene" : "t:Prefab";

            return AssetDatabase.FindAssets(filter)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct()
                .Where(options.Includes)
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        private static void PlanPrefab(string assetPath, ColorThemeSet themeSet,
            List<ColorMigrationSite> sites)
        {
            // Cheap reject before loading prefab contents, which is expensive. The serialized field name
            // is the pre-rename spelling that shipped data still uses.
            if (!FileMentionsLegacyColor(assetPath)) return;

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                PlanRoots(assetPath, new[] { root }, themeSet, sites);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void PlanScene(string assetPath, ColorThemeSet themeSet,
            List<ColorMigrationSite> sites)
        {
            if (!FileMentionsLegacyColor(assetPath)) return;

            var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            try
            {
                PlanRoots(assetPath, scene.GetRootGameObjects(), themeSet, sites);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void PlanRoots(string assetPath, IReadOnlyList<GameObject> roots,
            ColorThemeSet themeSet, List<ColorMigrationSite> sites)
        {
            bool writable = ColorThemeAssetWriteAccess.CanWrite(assetPath);
            string notWritable = writable ? null : ColorThemeAssetWriteAccess.DescribeRefusal(assetPath);

            // Built once per asset: the reference scan is O(components x serialized properties), and doing
            // it per ColorID would make a heavy prefab quadratic.
            var referenced = CollectReferencedColorIds(roots);

            // Grouped by GameObject, not by component. Real content puts two ColorID components on one
            // object (a Slider fill carrying the same colour twice), and planning per component would emit
            // two sites for one object — which the apply step would then process twice, the second
            // clearing the bindings the first had just written.
            foreach (var root in roots)
            {
                foreach (var group in root.GetComponentsInChildren<ColorID>(true).GroupBy(c => c.gameObject))
                {
                    sites.Add(PlanObject(assetPath, group.Key, group.ToList(), themeSet, writable,
                        notWritable, referenced));
                }
            }
        }

        private static ColorMigrationSite PlanObject(string assetPath, GameObject owner,
            IReadOnlyList<ColorID> legacyComponents, ColorThemeSet themeSet, bool writable,
            string notWritableDetail, HashSet<ColorID> referenced)
        {
            string objectPath = HierarchyPath(owner.transform);
            var first = legacyComponents[0];
            var key = new LegacyColorKey(first.SwatchName, first.ColorId);
            string canonical = themeSet.ResolveLegacyToken(key);
            int count = legacyComponents.Count;

            ColorMigrationSite Blocked(ColorMigrationBlock block, string detail = null) =>
                new ColorMigrationSite(assetPath, objectPath, key, canonical, 0, block, detail, count);

            if (!writable) return Blocked(ColorMigrationBlock.AssetNotWritable, notWritableDetail);

            // Checked early, before anything more expensive: an object owned by a nested prefab is not
            // this asset's to rewrite whatever else is true of it.
            if (PrefabUtility.IsPartOfPrefabInstance(owner))
            {
                string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(owner);
                string where = string.IsNullOrEmpty(source) ? "a nested prefab" : $"'{source}'";

                var overridden = legacyComponents.Where(OverridesItsSourceColor).ToList();
                if (overridden.Count > 0)
                {
                    return Blocked(ColorMigrationBlock.NestedPrefabColorOverride,
                        $"overrides {where}'s colour with "
                        + $"{string.Join(", ", overridden.Select(c => $"{c.SwatchName}.{c.ColorId}"))}; "
                        + "migrating the source will not carry that override");
                }

                return Blocked(ColorMigrationBlock.PartOfNestedPrefab,
                    $"belongs to {where}, which is migrated on its own");
            }

            // Resolved through the alias map rather than compared as raw pairs: two components naming
            // Default.Text and Text.100 are the same colour, and blocking those would be a false conflict.
            var distinctTokens = legacyComponents
                .Select(c => themeSet.ResolveLegacyToken(new LegacyColorKey(c.SwatchName, c.ColorId)))
                .Distinct()
                .ToList();

            if (distinctTokens.Count > 1)
            {
                return Blocked(ColorMigrationBlock.ConflictingLegacyComponents,
                    $"{count} ColorID components naming different colours "
                    + $"({string.Join(", ", legacyComponents.Select(c => $"{c.SwatchName}.{c.ColorId}"))}); "
                    + "delete the redundant one and re-plan");
            }

            if (string.IsNullOrEmpty(canonical))
            {
                return Blocked(ColorMigrationBlock.NoCanonicalToken,
                    $"'{key}' has no authored alias in the installed theme set");
            }

            foreach (var legacy in legacyComponents)
            {
                if (referenced.Contains(legacy))
                {
                    return Blocked(ColorMigrationBlock.ReferencedByAnotherComponent,
                        "another component drives this ColorID at runtime");
                }
            }

            if (owner.GetComponent<ColorThemeBinding>() != null)
                return Blocked(ColorMigrationBlock.AlreadyMigrated);

            var targets = ResolveTargets(legacyComponents);
            if (targets.Count == 0) return Blocked(ColorMigrationBlock.NoColorTargets);

            return new ColorMigrationSite(assetPath, objectPath, key, canonical, targets.Count,
                ColorMigrationBlock.None, null, count);
        }

        /// <summary>
        /// The components a migrated binding would write, with their authored alpha.
        /// </summary>
        /// <remarks>
        /// Prefers the component's own serialized target list, because that is where per-target alpha
        /// lives. Falls back to discovery only when the list is empty — which is the state a
        /// <see cref="ColorID"/> ships in when it has never been refreshed in the editor, and where the
        /// runtime would have discovered targets itself. <c>ApplyToChildren</c> is honoured in that
        /// fallback so the migrated object covers the same components the legacy one did.
        /// </remarks>
        private static List<(Component component, bool useAlpha, float customAlpha)> ResolveTargets(
            IReadOnlyList<ColorID> legacyComponents)
        {
            var results = new List<(Component, bool, float)>();

            // Deduplicated by component: redundant ColorIDs on one object list the same targets, and two
            // bindings writing the same component would each apply the same colour twice per switch.
            var seen = new HashSet<Component>();

            void Add(Component component, bool useAlpha, float customAlpha)
            {
                if (component == null || !seen.Add(component)) return;
                results.Add((component, useAlpha, customAlpha));
            }

            foreach (var legacy in legacyComponents)
            {
                foreach (var target in legacy.ColorTargets)
                {
                    if (target?.Component == null) continue;
                    Add(target.Component, target.UseAlpha, target.CustomAlpha);
                }
            }

            if (results.Count > 0) return results;

            // Fallback for a component that has never been refreshed in the editor, where the runtime
            // would have discovered its own targets. ApplyToChildren is honoured so the migrated object
            // covers the same components the legacy one did.
            foreach (var legacy in legacyComponents)
            {
                foreach (var component in ColorThemeBindingAuthoring.DiscoverColorTargets(legacy.gameObject))
                    Add(component, true, 1f);

                if (!legacy.ApplyToChildren) continue;

                foreach (var child in legacy.GetComponentsInChildren<Transform>(true))
                {
                    if (child == legacy.transform) continue;
                    foreach (var component in ColorThemeBindingAuthoring.DiscoverColorTargets(child.gameObject))
                        Add(component, true, 1f);
                }
            }

            return results;
        }

        /// <summary>
        /// Every <see cref="ColorID"/> that some other component in the same asset points at.
        /// </summary>
        /// <remarks>
        /// Walks serialized data rather than reflecting over known types, so a project's own component
        /// holding a <see cref="ColorID"/> reference is protected without this file knowing it exists.
        /// A <see cref="ColorID"/> referencing itself does not count.
        /// </remarks>
        private static HashSet<ColorID> CollectReferencedColorIds(IReadOnlyList<GameObject> roots)
        {
            var referenced = new HashSet<ColorID>();

            foreach (var root in roots)
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;

                    using (var serialized = new SerializedObject(component))
                    {
                        var property = serialized.GetIterator();
                        while (property.NextVisible(true))
                        {
                            if (property.propertyType != SerializedPropertyType.ObjectReference) continue;

                            if (property.objectReferenceValue is ColorID referencedColorId
                                && !ReferenceEquals(referencedColorId, component))
                            {
                                referenced.Add(referencedColorId);
                            }
                        }
                    }
                }
            }

            return referenced;
        }

        /// <summary>
        /// Whether this instance's colour differs from the prefab it came from.
        /// </summary>
        /// <remarks>
        /// Compares against the corresponding source object rather than reading the instance's
        /// modification list, because a modification entry is matched by property-path string and would
        /// have to know both serialized spellings of the fields. Comparing values answers the question
        /// directly. A component with no source is treated as not overriding — it was added on the
        /// instance, which the nested-prefab refusal already covers.
        /// </remarks>
        private static bool OverridesItsSourceColor(ColorID instance)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(instance);
            if (source == null) return false;

            return !new LegacyColorKey(instance.SwatchName, instance.ColorId)
                .Equals(new LegacyColorKey(source.SwatchName, source.ColorId));
        }

        private static bool FileMentionsLegacyColor(string assetPath)
        {
            try
            {
                string text = File.ReadAllText(assetPath);

                // Both shapes. A ColorID serialized in this file writes "colorId:"; one carried as a
                // prefab-instance override writes "propertyPath: _colorId" instead, and matching only the
                // first would skip an asset whose every legacy colour is an override — exactly the case
                // this project turned out to have.
                return text.IndexOf("colorId:", StringComparison.Ordinal) >= 0
                       || text.IndexOf("_colorId", StringComparison.Ordinal) >= 0
                       || text.IndexOf("_swatchName", StringComparison.Ordinal) >= 0;
            }
            catch (IOException)
            {
                // Unreadable here means "cannot cheaply reject", so it falls through to a full load, where
                // the failure is reported as a plan error rather than silently dropping the asset.
                return true;
            }
        }

        /// <summary>
        /// An unambiguous locator for a transform within its asset.
        /// </summary>
        /// <remarks>
        /// Each level carries its sibling index, because identically named siblings are ordinary in real
        /// content — one panel here has five children called <c>Divider</c>. A name-only path would let the
        /// apply step address the wrong one, and the plan a reviewer approved would not be the plan that
        /// ran.
        /// </remarks>
        private static string HierarchyPath(Transform transform)
        {
            var parts = new List<string>();
            for (var current = transform; current != null; current = current.parent)
                parts.Add($"{current.name}[{current.GetSiblingIndex()}]");
            parts.Reverse();
            return string.Join("/", parts);
        }

        #endregion

        #region Applying

        private static int ApplyToPrefab(string assetPath, IReadOnlyList<ColorMigrationSite> sites,
            ColorContentMigrationOptions options, out int bindingCount)
        {
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                int migrated = MigrateRoots(new[] { root }, sites, options, out bindingCount);
                if (migrated > 0) PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                return migrated;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static int ApplyToScene(string assetPath, IReadOnlyList<ColorMigrationSite> sites,
            ColorContentMigrationOptions options, out int bindingCount)
        {
            var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            try
            {
                int migrated = MigrateRoots(scene.GetRootGameObjects(), sites, options, out bindingCount);
                if (migrated > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
                return migrated;
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static int MigrateRoots(IReadOnlyList<GameObject> roots,
            IReadOnlyList<ColorMigrationSite> sites, ColorContentMigrationOptions options,
            out int bindingCount)
        {
            bindingCount = 0;

            // Planned sites are addressed by hierarchy path, which is how a plan reviewed against one
            // loaded copy of a prefab applies to the freshly loaded copy here.
            var wanted = new Dictionary<string, ColorMigrationSite>(StringComparer.Ordinal);
            foreach (var site in sites) wanted[site.ObjectPath] = site;

            int migrated = 0;
            var toRemove = new List<ColorID>();

            foreach (var root in roots)
            {
                // Grouped by GameObject, matching how the plan was built. One object becomes one binding
                // component no matter how many ColorIDs it carried.
                foreach (var group in root.GetComponentsInChildren<ColorID>(true).GroupBy(c => c.gameObject))
                {
                    if (!wanted.TryGetValue(HierarchyPath(group.Key.transform), out var site)) continue;

                    var legacyComponents = group.ToList();

                    // Re-derived rather than trusted from the plan: the plan records counts for review, but
                    // the component references themselves must come from this freshly loaded copy.
                    var targets = ResolveTargets(legacyComponents);
                    if (targets.Count == 0) continue;

                    var binding = group.Key.GetComponent<ColorThemeBinding>();
                    if (binding == null) binding = group.Key.AddComponent<ColorThemeBinding>();

                    binding.ClearBindings();
                    var token = new ColorTokenReference(site.CanonicalTokenId);
                    foreach (var (component, useAlpha, customAlpha) in targets)
                    {
                        binding.AddBinding(new ColorBinding(token, component, ColorChannels.Color,
                            useAlpha ? ColorAlphaPolicy.UseTokenAlpha : ColorAlphaPolicy.Explicit,
                            customAlpha));
                        bindingCount++;
                    }

                    migrated++;
                    if (options.RemoveLegacyComponent) toRemove.AddRange(legacyComponents);
                }
            }

            // Deferred: destroying a component while enumerating GetComponentsInChildren's result is safe,
            // but destroying one whose targets another pending site still reads is not.
            foreach (var legacy in toRemove) UnityEngine.Object.DestroyImmediate(legacy, true);

            return migrated;
        }

        #endregion
    }
}
#endif
