using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Molca.ColorID;
using Molca.Editor.Migration;
using Molca.Editor.Upgrade;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.ColorID.Editor.Upgrade
{
    /// <summary>One colour target a v1 <c>ColorID</c> painted, read from serialized data.</summary>
    public readonly struct LegacyColorTarget
    {
        /// <summary>The painted component's local file id.</summary>
        public long ComponentFileId { get; }

        /// <summary>Whether the token's own alpha is used.</summary>
        public bool UseAlpha { get; }

        /// <summary>The alpha applied when <see cref="UseAlpha"/> is false.</summary>
        public float CustomAlpha { get; }

        /// <summary>Creates a target.</summary>
        public LegacyColorTarget(long componentFileId, bool useAlpha, float customAlpha)
        {
            ComponentFileId = componentFileId;
            UseAlpha = useAlpha;
            CustomAlpha = customAlpha;
        }
    }

    /// <summary>One v1 ColorID and what it becomes.</summary>
    public sealed class LegacyColorSite
    {
        /// <summary>The component as found in serialized data.</summary>
        public LegacyComponentRecord Record { get; }

        /// <summary>The legacy pair it names.</summary>
        public LegacyColorKey LegacyKey { get; }

        /// <summary>The canonical token it translates to, or <c>null</c> when it has no alias.</summary>
        public string CanonicalTokenId { get; }

        /// <summary>The components it painted.</summary>
        public IReadOnlyList<LegacyColorTarget> Targets { get; }

        /// <summary>Why it cannot be migrated, or <c>null</c>.</summary>
        public string Refusal { get; }

        /// <summary>Whether this site can be rewritten.</summary>
        public bool IsMigratable => Refusal == null;

        /// <summary>Creates a site.</summary>
        public LegacyColorSite(LegacyComponentRecord record, LegacyColorKey legacyKey,
            string canonicalTokenId, IReadOnlyList<LegacyColorTarget> targets, string refusal)
        {
            Record = record;
            LegacyKey = legacyKey;
            CanonicalTokenId = canonicalTokenId;
            Targets = targets ?? Array.Empty<LegacyColorTarget>();
            Refusal = refusal;
        }
    }

    /// <summary>The migration, decided before anything is written.</summary>
    public sealed class LegacyColorContentPlan
    {
        /// <summary>Every v1 ColorID found.</summary>
        public IReadOnlyList<LegacyColorSite> Sites { get; }

        /// <summary>Assets that could not be read.</summary>
        public IReadOnlyList<string> UnreadableAssets { get; }

        /// <summary>Whether the scan saw everything.</summary>
        public bool IsConclusive => UnreadableAssets.Count == 0;

        /// <summary>Sites that will be rewritten.</summary>
        public IEnumerable<LegacyColorSite> Migratable => Sites.Where(s => s.IsMigratable);

        /// <summary>Sites needing a person.</summary>
        public IEnumerable<LegacyColorSite> Refused => Sites.Where(s => !s.IsMigratable);

        /// <summary>Creates a plan.</summary>
        public LegacyColorContentPlan(IReadOnlyList<LegacyColorSite> sites,
            IReadOnlyList<string> unreadable)
        {
            Sites = sites ?? Array.Empty<LegacyColorSite>();
            UnreadableAssets = unreadable ?? Array.Empty<string>();
        }
    }

    /// <summary>The outcome of applying a plan.</summary>
    public sealed class LegacyColorContentResult
    {
        /// <summary>ColorIDs replaced by a binding.</summary>
        public int Migrated { get; }

        /// <summary>Bindings written.</summary>
        public int BindingsWritten { get; }

        /// <summary>Assets saved.</summary>
        public IReadOnlyList<string> WrittenAssets { get; }

        /// <summary>One message per site that could not be written.</summary>
        public IReadOnlyList<string> Failures { get; }

        /// <summary>Creates a result.</summary>
        public LegacyColorContentResult(int migrated, int bindingsWritten,
            IReadOnlyList<string> written, IReadOnlyList<string> failures)
        {
            Migrated = migrated;
            BindingsWritten = bindingsWritten;
            WrittenAssets = written ?? Array.Empty<string>();
            Failures = failures ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Migrates v1 <c>ColorID</c> content to <see cref="ColorThemeBinding"/> <b>without the v1 type</b>.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Upgrade/</c> — deliberately not
    /// <c>Transactions/</c>, which holds the v1-era migrator that 2.0 deletes along with the types it
    /// reaches for.
    /// <para/>
    /// <b>Why a second migrator rather than a rewrite of the first.</b> They serve different worlds. The
    /// old one runs on a 1.x project where <c>ColorID</c> still exists, and uses it —
    /// <c>GetComponentsInChildren&lt;ColorID&gt;()</c>, <c>ColorTargets</c>, <c>ApplyToChildren</c>. This
    /// one runs on 2.0, where the class is gone and content carries it as a missing script. Making one
    /// class serve both would mean conditioning every read on whether a type exists; shipping only this
    /// one in 2.0 is simpler and is what the upgrade path actually needs.
    /// <para/>
    /// Reads through <see cref="LegacyComponentIndex"/> (script GUID, YAML fields), resolves target
    /// components through <see cref="ComponentLocatorMap"/>, and removes the retired component through
    /// <see cref="LegacyComponentRemover"/>. Three buckets as always: migrated, refused, nothing-to-do —
    /// a pair with no authored alias is refused rather than guessed at.
    /// </remarks>
    public static class LegacyColorContentMigration
    {
        /// <summary>Matches one entry of the serialized <c>_colorTargets</c> list.</summary>
        /// <remarks>
        /// Anchored on the list-item dash, because these are list entries — an anchored
        /// <c>^\s*targetType:</c> matches none of them, a trap this data has sprung before.
        /// </remarks>
        private static readonly Regex TargetEntry = new Regex(
            @"^\s*- _?targetType:\s*(?<type>-?\d+)\s*\n"
            + @"\s*_?targetComponent:\s*\{fileID:\s*(?<component>-?\d+)\}\s*\n"
            + @"\s*_?useAlpha:\s*(?<useAlpha>\d+)\s*\n"
            + @"\s*_?customAlpha:\s*(?<customAlpha>[\d.]+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Decides what to do with every v1 ColorID in the project.</summary>
        /// <param name="themeSet">The theme set whose alias map translates each pair.</param>
        /// <returns>The plan; never <c>null</c>.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="themeSet"/> is <c>null</c>.</exception>
        public static LegacyColorContentPlan Plan(ColorThemeSet themeSet)
        {
            if (themeSet == null) throw new ArgumentNullException(nameof(themeSet));

            var snapshot = LegacyComponentIndex.Scan(
                LegacyColorContentDetector.ColorIdScriptGuid,
                path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));

            var sites = new List<LegacyColorSite>();

            foreach (var record in snapshot.All)
            {
                var key = new LegacyColorKey(record.Field("swatchName"), record.Field("colorId"));
                var targets = ReadTargets(record.Body);

                string token = themeSet.ResolveLegacyToken(key);
                string refusal = null;

                if (string.IsNullOrEmpty(token))
                {
                    refusal = $"the theme set declares no alias for '{key.SwatchName}.{key.ColorId}', so "
                              + "no canonical token can be chosen without inventing a colour";
                }
                else if (targets.Count == 0)
                {
                    // V1 could discover targets at runtime when the list was empty. Nothing here can
                    // reproduce that without the component, and inventing a target set would paint
                    // components the author never listed.
                    refusal = "the component lists no colour targets, and which components it painted "
                              + "cannot be recovered once the type is gone — add a ColorThemeBinding by "
                              + "hand naming the components it should write";
                }

                sites.Add(new LegacyColorSite(record, key, token, targets, refusal));
            }

            return new LegacyColorContentPlan(sites, snapshot.UnreadableAssets);
        }

        /// <summary>Applies a plan: bindings first, then the retired components.</summary>
        /// <param name="plan">The plan to apply.</param>
        /// <returns>What was written.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="plan"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">When the plan is inconclusive.</exception>
        public static LegacyColorContentResult Apply(LegacyColorContentPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.IsConclusive)
            {
                throw new InvalidOperationException(
                    $"Refusing to apply an inconclusive plan: {plan.UnreadableAssets.Count} asset(s) could "
                    + "not be read, so the scan is a lower bound rather than an answer.");
            }

            var failures = new List<string>();
            var written = new List<string>();
            int migrated = 0;
            int bindings = 0;

            foreach (var group in plan.Migratable.GroupBy(s => s.Record.AssetPath)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var sites = group.ToList();

                int count = group.Key.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    ? ApplyToScene(group.Key, sites, ref bindings, failures)
                    : ApplyToPrefab(group.Key, sites, ref bindings, failures);

                if (count <= 0) continue;

                migrated += count;
                written.Add(group.Key);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new LegacyColorContentResult(migrated, bindings, written, failures);
        }

        private static int ApplyToPrefab(string assetPath, IReadOnlyList<LegacyColorSite> sites,
            ref int bindings, List<string> failures)
        {
            var locators = ComponentLocatorMap.FromAsset(assetPath);
            var ownerPaths = ComponentLocatorMap.GameObjectPathsFromAsset(assetPath);

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                int migrated = WriteBindings(root, assetPath, sites, locators, ownerPaths, ref bindings,
                    failures);
                if (migrated > 0) PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                return migrated;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static int ApplyToScene(string assetPath, IReadOnlyList<LegacyColorSite> sites,
            ref int bindings, List<string> failures)
        {
            var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            try
            {
                int migrated = 0;
                foreach (var root in scene.GetRootGameObjects())
                    migrated += WriteBindings(root, assetPath, sites, null, null, ref bindings, failures);

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

        /// <summary>Adds the replacement binding for every site this hierarchy owns.</summary>
        private static int WriteBindings(GameObject root, string assetPath,
            IReadOnlyList<LegacyColorSite> sites, Dictionary<long, ComponentLocator> locators,
            Dictionary<long, string> ownerPaths, ref int bindings, List<string> failures)
        {
            int migrated = 0;

            foreach (var site in sites)
            {
                var owner = FindOwner(root, site, ownerPaths);
                if (owner == null) continue;

                var targets = new List<Component>();

                foreach (var target in site.Targets)
                {
                    var component = ResolveTarget(root, target.ComponentFileId, locators);
                    if (component == null)
                    {
                        failures.Add($"{assetPath}: '{owner.name}' painted component "
                                     + $"{target.ComponentFileId}, which could not be located; that "
                                     + "binding was not written");
                        continue;
                    }

                    targets.Add(component);
                }

                if (targets.Count == 0)
                {
                    failures.Add($"{assetPath}: '{owner.name}' had no resolvable colour target, so no "
                                 + "binding replaced it and its ColorID was left in place");
                    continue;
                }

                // A new component every time, never a reuse: an inherited binding belongs to the prefab
                // this object came from, and rebuilding it would clear that prefab's own styling.
                var binding = owner.AddComponent<ColorThemeBinding>();
                var token = new ColorTokenReference(site.CanonicalTokenId);

                for (int i = 0; i < targets.Count; i++)
                {
                    var target = site.Targets.ElementAtOrDefault(i);
                    binding.AddBinding(new ColorBinding(token, targets[i], ColorChannels.Color,
                        target.UseAlpha ? ColorAlphaPolicy.UseTokenAlpha : ColorAlphaPolicy.Explicit,
                        target.CustomAlpha));
                    bindings++;
                }

                // Removed in the same working-copy session that added the replacement, so the asset is
                // loaded and saved once. Doing it as a second load/save cycle left the retired script in
                // place: the first save had already rewritten the file, and the reopened copy no longer
                // presented a missing script to remove.
                int retiredOnOwner = sites.Count(s => s.Record.GameObjectFileId
                                                      == site.Record.GameObjectFileId);
                LegacyComponentRemover.RemoveRetiredScripts(owner,
                    LegacyColorContentDetector.ColorIdScriptGuid, retiredOnOwner, assetPath, failures);

                migrated++;
            }

            return migrated;
        }

        /// <summary>The GameObject a site's retired component sat on, inside the working copy.</summary>
        private static GameObject FindOwner(GameObject root, LegacyColorSite site,
            Dictionary<long, string> ownerPaths)
        {
            // A prefab working copy has no persistent ids, so the owner is found by the path the asset
            // representation recorded for that id. In a scene the objects are real and carry theirs.
            return ownerPaths != null
                ? (ownerPaths.TryGetValue(site.Record.GameObjectFileId, out string path)
                    ? ComponentLocatorMap.ResolveGameObject(root, path)
                    : null)
                : FindOwnerInScene(root, site);
        }

        private static GameObject FindOwnerInScene(GameObject root, LegacyColorSite site)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(transform.gameObject, out _,
                        out long fileId) && fileId == site.Record.GameObjectFileId)
                {
                    return transform.gameObject;
                }

                if ((long)GlobalObjectId.GetGlobalObjectIdSlow(transform.gameObject).targetObjectId
                    == site.Record.GameObjectFileId)
                {
                    return transform.gameObject;
                }
            }

            return null;
        }

        private static Component ResolveTarget(GameObject root, long fileId,
            Dictionary<long, ComponentLocator> locators)
        {
            if (locators != null)
            {
                return locators.TryGetValue(fileId, out var locator)
                    ? ComponentLocatorMap.Resolve(root, locator)
                    : null;
            }

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                if ((long)GlobalObjectId.GetGlobalObjectIdSlow(component).targetObjectId == fileId)
                    return component;
            }

            return null;
        }

        /// <summary>Reads the serialized colour-target list out of a component document.</summary>
        internal static List<LegacyColorTarget> ReadTargets(string body)
        {
            var targets = new List<LegacyColorTarget>();
            if (string.IsNullOrEmpty(body)) return targets;

            foreach (Match match in TargetEntry.Matches(body))
            {
                if (!long.TryParse(match.Groups["component"].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long componentId) || componentId == 0)
                {
                    continue;
                }

                float.TryParse(match.Groups["customAlpha"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float customAlpha);

                targets.Add(new LegacyColorTarget(componentId,
                    match.Groups["useAlpha"].Value != "0", customAlpha));
            }

            return targets;
        }
    }
}
