using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Molca.ColorID;
using Molca.Editor.Migration;
using UnityEditor;
using UnityEngine;

namespace Molca.App.UI.Editor
{
    /// <summary>What happened to one serialized colour field.</summary>
    public enum ColorTokenFieldOutcome
    {
        /// <summary>The legacy pair translated to a canonical token.</summary>
        Migrated,

        /// <summary>The theme set has no alias for the pair, so nothing was written.</summary>
        Refused,

        /// <summary>The field was never assigned a legacy pair; there is nothing to carry.</summary>
        NothingToDo,
    }

    /// <summary>One serialized <c>ColorIDReference</c> field, and what the plan intends to do with it.</summary>
    public sealed class ColorTokenFieldPlan
    {
        /// <summary>The prefab or scene that physically holds the value.</summary>
        public string ContainingAssetPath { get; }

        /// <summary>
        /// The prefab holding the component definition, or the same as
        /// <see cref="ContainingAssetPath"/> for a source-phase field.
        /// </summary>
        public string SourceAssetPath { get; }

        /// <summary>The component's local file id in <see cref="SourceAssetPath"/>.</summary>
        public long ComponentFileId { get; }

        /// <summary>
        /// The <c>PrefabInstance</c>'s local file id, or 0 for a source-phase field.
        /// </summary>
        public long InstanceFileId { get; }

        /// <summary>The serialized field name, e.g. <c>normalColor</c>.</summary>
        public string FieldName { get; }

        /// <summary>The effective legacy pair, after composing any partial override over its base.</summary>
        public LegacyColorKey LegacyKey { get; }

        /// <summary>The canonical token the pair translates to, or <c>null</c> when it has no alias.</summary>
        public string CanonicalTokenId { get; }

        /// <summary>What the plan intends to do.</summary>
        public ColorTokenFieldOutcome Outcome { get; }

        /// <summary>Why, when the outcome is not <see cref="ColorTokenFieldOutcome.Migrated"/>.</summary>
        public string Reason { get; }

        /// <summary>Whether this field lives on a prefab instance rather than on a source component.</summary>
        public bool IsInstanceOverride => InstanceFileId != 0;

        /// <summary>Creates a field plan.</summary>
        public ColorTokenFieldPlan(string containingAssetPath, string sourceAssetPath, long componentFileId,
            long instanceFileId, string fieldName, LegacyColorKey legacyKey, string canonicalTokenId,
            ColorTokenFieldOutcome outcome, string reason)
        {
            ContainingAssetPath = containingAssetPath;
            SourceAssetPath = sourceAssetPath;
            ComponentFileId = componentFileId;
            InstanceFileId = instanceFileId;
            FieldName = fieldName;
            LegacyKey = legacyKey;
            CanonicalTokenId = canonicalTokenId;
            Outcome = outcome;
            Reason = reason;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"{Path.GetFileName(ContainingAssetPath)} {FieldName}: {LegacyKey.SwatchName}.{LegacyKey.ColorId}"
            + (CanonicalTokenId != null ? $" -> {CanonicalTokenId}" : string.Empty)
            + $" [{Outcome}]";
    }

    /// <summary>The whole migration, decided before anything is written.</summary>
    public sealed class ColorTokenReferencePlan
    {
        /// <summary>Every field the scan found, in both phases.</summary>
        public IReadOnlyList<ColorTokenFieldPlan> Fields { get; }

        /// <summary>Assets that could not be read; a non-empty list makes the plan inconclusive.</summary>
        public IReadOnlyList<string> UnreadableAssets { get; }

        /// <summary>Fields that will be written.</summary>
        public IEnumerable<ColorTokenFieldPlan> Migrated =>
            Fields.Where(f => f.Outcome == ColorTokenFieldOutcome.Migrated);

        /// <summary>Fields left alone because no alias exists for their pair.</summary>
        public IEnumerable<ColorTokenFieldPlan> Refused =>
            Fields.Where(f => f.Outcome == ColorTokenFieldOutcome.Refused);

        /// <summary>Fields with nothing to carry.</summary>
        public IEnumerable<ColorTokenFieldPlan> NothingToDo =>
            Fields.Where(f => f.Outcome == ColorTokenFieldOutcome.NothingToDo);

        /// <summary>
        /// Whether the plan can be trusted. A floor cannot prove absence: an unreadable asset means the
        /// scan is a lower bound, not an answer.
        /// </summary>
        public bool IsConclusive => UnreadableAssets.Count == 0;

        /// <summary>Creates a plan.</summary>
        public ColorTokenReferencePlan(IReadOnlyList<ColorTokenFieldPlan> fields,
            IReadOnlyList<string> unreadableAssets)
        {
            Fields = fields ?? Array.Empty<ColorTokenFieldPlan>();
            UnreadableAssets = unreadableAssets ?? Array.Empty<string>();
        }
    }

    /// <summary>The outcome of applying a <see cref="ColorTokenReferencePlan"/>.</summary>
    public sealed class ColorTokenReferenceResult
    {
        /// <summary>Source-phase fields written.</summary>
        public int SourceFieldsWritten { get; }

        /// <summary>Instance-phase fields written.</summary>
        public int OverrideFieldsWritten { get; }

        /// <summary>Assets that were saved.</summary>
        public IReadOnlyList<string> WrittenAssets { get; }

        /// <summary>One message per site that could not be written.</summary>
        public IReadOnlyList<string> Failures { get; }

        /// <summary>Creates a result.</summary>
        public ColorTokenReferenceResult(int sourceFieldsWritten, int overrideFieldsWritten,
            IReadOnlyList<string> writtenAssets, IReadOnlyList<string> failures)
        {
            SourceFieldsWritten = sourceFieldsWritten;
            OverrideFieldsWritten = overrideFieldsWritten;
            WrittenAssets = writtenAssets ?? Array.Empty<string>();
            Failures = failures ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Migrates serialized <c>ColorIDReference</c> pairs on <see cref="ColorIDButton"/> and
    /// <see cref="ButtonState"/> to <see cref="ColorTokenReference"/>, resolving every token through the
    /// theme set's alias map.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/AppUI/Migration/</c> — it lives in
    /// <c>Molca.App.Editor</c> because it names the App components, and that assembly is the one already
    /// referencing both <c>Molca.App</c> and <c>Molca.Editor</c>.
    /// <b>Shape:</b> editor-only static service. Plan first, apply second; nothing decides while writing.
    /// <para/>
    /// <b>The plan is read from YAML text, not from <see cref="SerializedObject"/>.</b> This migration runs
    /// in the same commit that retypes the fields, so by the time it executes the C# side already declares
    /// <c>_tokenId</c> and the object model no longer exposes <c>_swatchName</c>/<c>_colorId</c> at all —
    /// the legacy values survive only as orphaned lines in the asset file until something re-saves it.
    /// Reading the text is the only way to see them. This is the same reason
    /// <see cref="PrefabInstanceOverrideIndex"/> parses YAML rather than walking properties.
    /// <para/>
    /// <b>Both field-name spellings are matched.</b> Shipped data carries the pre-<c>_camelCase</c> names
    /// resolved by <c>FormerlySerializedAs</c>, and instance modifications routinely carry <i>both</i>
    /// spellings as separate entries on the same instance. Matching one spelling finds a fraction of the
    /// real usage, and removing one leaves the other behind to be read instead.
    /// <para/>
    /// <b>Overrides are composed against their source.</b> An instance frequently overrides only
    /// <c>colorId</c> and inherits the swatch, so the override alone is not a resolvable pair. The base is
    /// captured at plan time, before the source phase rewrites it away.
    /// <para/>
    /// The instance phase runs strictly after every source has been written, and goes through
    /// <see cref="PrefabInstanceOverrideWriter"/> — never <c>SaveAsPrefabAsset</c> or
    /// <c>LoadPrefabContents</c>.
    /// </remarks>
    public static class ColorTokenReferenceMigration
    {
        /// <summary>Serialized field names carrying a colour reference, keyed by component type name.</summary>
        private static readonly Dictionary<string, string[]> FieldsByComponent =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [nameof(ColorIDButton)] = new[]
                {
                    "normalColor", "highlightedColor", "pressedColor", "selectedColor", "disabledColor",
                },
                [nameof(ButtonState)] = new[] { "onColor", "offColor" },
            };

        /// <summary>Every colour field name across every migrated component.</summary>
        private static readonly string[] AllFields =
            FieldsByComponent.Values.SelectMany(f => f).Distinct().ToArray();

        /// <summary>
        /// Matches a legacy override path, e.g. <c>normalColor._swatchName</c> or <c>onColor.colorId</c>.
        /// </summary>
        private static readonly Regex LegacyOverridePath = new Regex(
            @"^(?<field>" + string.Join("|", AllFields) + @")\._?(?<part>swatchName|colorId)$",
            RegexOptions.Compiled);

        /// <summary>Matches one YAML document header, capturing the object's local file id.</summary>
        private static readonly Regex DocumentHeader =
            new Regex(@"^--- !u!\d+ &(?<fileId>-?\d+)", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Matches the script reference that identifies which component a document is.</summary>
        private static readonly Regex ScriptGuid =
            new Regex(@"^\s+m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*(?<guid>[0-9a-fA-F]{32})",
                RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Decides what to do with every legacy colour field in the project.
        /// </summary>
        /// <param name="themeSet">The theme set whose alias map translates each pair.</param>
        /// <returns>The plan; never <c>null</c>.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="themeSet"/> is <c>null</c>.</exception>
        /// <remarks>
        /// Read-only. Nothing here dirties, saves, or loads prefab contents.
        /// </remarks>
        public static ColorTokenReferencePlan Plan(ColorThemeSet themeSet)
        {
            if (themeSet == null) throw new ArgumentNullException(nameof(themeSet));

            var scriptGuids = ResolveScriptGuids();
            var fields = new List<ColorTokenFieldPlan>();
            var unreadable = new List<string>();

            // (assetGuid, componentFileId, fieldName) -> base pair, needed to compose partial overrides.
            var sourcePairs = new Dictionary<(string Guid, long FileId, string Field), LegacyColorKey>();

            foreach (string assetPath in ContainingAssets())
            {
                string text;
                try
                {
                    text = File.ReadAllText(assetPath);
                }
                catch (Exception exception) when (exception is IOException
                                                  || exception is UnauthorizedAccessException)
                {
                    unreadable.Add($"{assetPath}: {exception.Message}");
                    continue;
                }

                if (!scriptGuids.Keys.Any(guid => text.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0)
                    && !AllFields.Any(f => text.IndexOf(f + ".", StringComparison.Ordinal) >= 0))
                {
                    continue;
                }

                string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);

                try
                {
                    ReadSourceComponents(assetPath, assetGuid, text, scriptGuids, themeSet, fields,
                        sourcePairs);
                }
                catch (Exception exception)
                {
                    unreadable.Add($"{assetPath}: {exception.Message}");
                }
            }

            // The instance phase is planned only after every source pair is known: a partial override
            // resolves against its base, and a base discovered later would silently be missed.
            var overrideSnapshot = PrefabInstanceOverrideIndex.Scan(
                path => LegacyOverridePath.IsMatch(path),
                AllFields.Select(f => f + ".").ToArray());

            foreach (string message in overrideSnapshot.UnreadableAssets) unreadable.Add(message);

            PlanOverrides(overrideSnapshot, sourcePairs, themeSet, fields);

            return new ColorTokenReferencePlan(fields, unreadable);
        }

        /// <summary>
        /// Applies a plan: sources first, then prefab instances.
        /// </summary>
        /// <param name="plan">The plan to apply.</param>
        /// <returns>What was written.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="plan"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">When the plan is inconclusive.</exception>
        public static ColorTokenReferenceResult Apply(ColorTokenReferencePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.IsConclusive)
            {
                throw new InvalidOperationException(
                    "Refusing to apply an inconclusive plan: "
                    + $"{plan.UnreadableAssets.Count} asset(s) could not be read, so the scan is a lower "
                    + "bound rather than an answer. " + string.Join("; ", plan.UnreadableAssets));
            }

            var failures = new List<string>();
            var written = new List<string>();

            var migrated = plan.Migrated.ToList();

            int sourceWritten = ApplySources(
                migrated.Where(f => !f.IsInstanceOverride).ToList(), written, failures);

            // Strictly after every source is written: by now the components an override names may have been
            // reshaped, which is exactly why the instance phase addresses them by file id.
            int overridesWritten = ApplyOverrides(
                migrated.Where(f => f.IsInstanceOverride).ToList(), written, failures);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return new ColorTokenReferenceResult(sourceWritten, overridesWritten, written, failures);
        }

        /// <summary>Reads the legacy pairs held directly on components in one asset.</summary>
        private static void ReadSourceComponents(string assetPath, string assetGuid, string text,
            IReadOnlyDictionary<string, string> scriptGuids, ColorThemeSet themeSet,
            List<ColorTokenFieldPlan> fields,
            Dictionary<(string, long, string), LegacyColorKey> sourcePairs)
        {
            foreach (var (fileId, body) in Documents(text))
            {
                var scriptMatch = ScriptGuid.Match(body);
                if (!scriptMatch.Success) continue;
                if (!scriptGuids.TryGetValue(scriptMatch.Groups["guid"].Value.ToLowerInvariant(),
                        out string componentName))
                {
                    continue;
                }

                foreach (string field in FieldsByComponent[componentName])
                {
                    if (!TryReadPair(body, field, out LegacyColorKey key)) continue;

                    sourcePairs[(assetGuid, fileId, field)] = key;
                    fields.Add(Decide(assetPath, assetPath, fileId, 0, field, key, themeSet));
                }
            }
        }

        /// <summary>Composes each override against its source base and decides its outcome.</summary>
        private static void PlanOverrides(PrefabInstanceOverrideSnapshot snapshot,
            IReadOnlyDictionary<(string, long, string), LegacyColorKey> sourcePairs, ColorThemeSet themeSet,
            List<ColorTokenFieldPlan> fields)
        {
            // Both spellings of both parts collapse onto one (instance, component, field) site. Two entries
            // for the same part are the FormerlySerializedAs duplication, not a conflict: they always carry
            // the same value, because Unity wrote them from one field.
            var sites = new Dictionary<(string Containing, long Instance, string TargetGuid, long TargetFileId,
                string Field), Dictionary<string, string>>();

            foreach (var entry in snapshot.All)
            {
                var match = LegacyOverridePath.Match(entry.PropertyPath);
                if (!match.Success) continue;

                var key = (entry.ContainingAssetPath, entry.InstanceFileId, entry.TargetGuid,
                    entry.TargetFileId, match.Groups["field"].Value);

                if (!sites.TryGetValue(key, out var parts))
                {
                    parts = new Dictionary<string, string>(StringComparer.Ordinal);
                    sites[key] = parts;
                }

                parts[match.Groups["part"].Value] = entry.Value;
            }

            foreach (var site in sites)
            {
                var (containing, instance, targetGuid, targetFileId, field) = site.Key;
                sourcePairs.TryGetValue((targetGuid, targetFileId, field), out LegacyColorKey basePair);

                string swatch = site.Value.TryGetValue("swatchName", out string s) ? s : basePair.SwatchName;
                string colorId = site.Value.TryGetValue("colorId", out string c) ? c : basePair.ColorId;

                string sourcePath = AssetDatabase.GUIDToAssetPath(targetGuid);

                if (swatch == null || colorId == null)
                {
                    fields.Add(new ColorTokenFieldPlan(containing, sourcePath, targetFileId, instance, field,
                        new LegacyColorKey(swatch, colorId), null, ColorTokenFieldOutcome.Refused,
                        "the override sets only one half of the pair and its source component's base value "
                        + "could not be read, so the effective pair is unknown"));
                    continue;
                }

                fields.Add(Decide(containing, sourcePath, targetFileId, instance, field,
                    new LegacyColorKey(swatch, colorId), themeSet));
            }
        }

        /// <summary>Translates one effective pair, or explains why it cannot be translated.</summary>
        private static ColorTokenFieldPlan Decide(string containingAssetPath, string sourceAssetPath,
            long componentFileId, long instanceFileId, string field, LegacyColorKey key,
            ColorThemeSet themeSet)
        {
            if (string.IsNullOrEmpty(key.SwatchName) && string.IsNullOrEmpty(key.ColorId))
            {
                return new ColorTokenFieldPlan(containingAssetPath, sourceAssetPath, componentFileId,
                    instanceFileId, field, key, null, ColorTokenFieldOutcome.NothingToDo,
                    "the field was never assigned a legacy pair");
            }

            string token = themeSet.ResolveLegacyToken(key);
            if (string.IsNullOrEmpty(token))
            {
                return new ColorTokenFieldPlan(containingAssetPath, sourceAssetPath, componentFileId,
                    instanceFileId, field, key, null, ColorTokenFieldOutcome.Refused,
                    $"the theme set declares no alias for '{key.SwatchName}.{key.ColorId}', so no canonical "
                    + "token can be chosen without inventing one");
            }

            return new ColorTokenFieldPlan(containingAssetPath, sourceAssetPath, componentFileId,
                instanceFileId, field, key, token, ColorTokenFieldOutcome.Migrated, null);
        }

        /// <summary>Writes the token onto each source component through its serialized object.</summary>
        private static int ApplySources(IReadOnlyList<ColorTokenFieldPlan> plans, List<string> written,
            List<string> failures)
        {
            int applied = 0;

            foreach (var group in plans.GroupBy(p => p.ContainingAssetPath)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(group.Key);
                if (root == null)
                {
                    failures.Add($"{group.Key}: could not be loaded as a prefab");
                    continue;
                }

                var byFileId = ComponentsByFileId(group.Key);
                bool dirty = false;

                foreach (var plan in group)
                {
                    if (!byFileId.TryGetValue(plan.ComponentFileId, out var component))
                    {
                        failures.Add($"{group.Key}: the component recorded as file id "
                                     + $"{plan.ComponentFileId} is no longer present, so its "
                                     + $"'{plan.FieldName}' token was not written");
                        continue;
                    }

                    var serialized = new SerializedObject(component);
                    var property = serialized.FindProperty($"{plan.FieldName}._tokenId");
                    if (property == null)
                    {
                        failures.Add($"{group.Key}: '{plan.FieldName}._tokenId' is not a serialized "
                                     + $"property of {component.GetType().Name}");
                        continue;
                    }

                    property.stringValue = plan.CanonicalTokenId;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(component);
                    dirty = true;
                    applied++;
                }

                if (!dirty) continue;

                EditorUtility.SetDirty(root);
                AssetDatabase.SaveAssetIfDirty(root);
                written.Add(group.Key);
            }

            return applied;
        }

        /// <summary>Rewrites each instance's overrides through the one sanctioned write path.</summary>
        private static int ApplyOverrides(IReadOnlyList<ColorTokenFieldPlan> plans, List<string> written,
            List<string> failures)
        {
            if (plans.Count == 0) return 0;

            var rewrites = new List<PrefabInstanceRewrite>();
            int fieldCount = 0;

            foreach (var group in plans.GroupBy(p => (p.ContainingAssetPath, p.InstanceFileId)))
            {
                var set = new List<(UnityEngine.Object, string, string)>();
                var migratedFields = new HashSet<string>(StringComparer.Ordinal);

                foreach (var plan in group)
                {
                    var componentsByFileId = ComponentsByFileId(plan.SourceAssetPath);
                    if (!componentsByFileId.TryGetValue(plan.ComponentFileId, out var target))
                    {
                        failures.Add($"{plan.ContainingAssetPath}: the override target component "
                                     + $"{plan.ComponentFileId} in {plan.SourceAssetPath} could not be "
                                     + $"resolved, so '{plan.FieldName}' was not carried");
                        continue;
                    }

                    set.Add((target, $"{plan.FieldName}._tokenId", plan.CanonicalTokenId));
                    migratedFields.Add(plan.FieldName);
                    fieldCount++;
                }

                if (set.Count == 0) continue;

                // Only the fields actually migrated are cleaned up. A refused field keeps its legacy
                // modification entries: they are inert once the schema no longer reads them, and deleting
                // them would destroy the record of what the site used to say.
                rewrites.Add(new PrefabInstanceRewrite(group.Key.ContainingAssetPath,
                    group.Key.InstanceFileId, set,
                    modification =>
                    {
                        var match = LegacyOverridePath.Match(modification.propertyPath ?? string.Empty);
                        return match.Success && migratedFields.Contains(match.Groups["field"].Value);
                    }));
            }

            var writtenAssets = PrefabInstanceOverrideWriter.Apply(rewrites, out _, out var writeFailures);

            foreach (string failure in writeFailures) failures.Add(failure);
            foreach (string asset in writtenAssets)
            {
                if (!written.Contains(asset)) written.Add(asset);
            }

            return fieldCount;
        }

        /// <summary>The migrated components of a prefab asset, keyed by the file id an override names.</summary>
        private static Dictionary<long, Component> ComponentsByFileId(string assetPath)
        {
            var byFileId = new Dictionary<long, Component>();
            if (string.IsNullOrEmpty(assetPath)) return byFileId;

            foreach (var pair in PrefabInstanceOverrideIndex.MapComponentFileIds<ColorIDButton>(assetPath))
                byFileId[pair.Value] = pair.Key;

            foreach (var pair in PrefabInstanceOverrideIndex.MapComponentFileIds<ButtonState>(assetPath))
                byFileId[pair.Value] = pair.Key;

            return byFileId;
        }

        /// <summary>
        /// Reads one field's legacy pair out of a component document, accepting either field-name spelling.
        /// </summary>
        private static bool TryReadPair(string body, string field, out LegacyColorKey key)
        {
            key = default;

            var header = new Regex($@"^(?<indent>\s+){Regex.Escape(field)}:\s*$",
                RegexOptions.Multiline).Match(body);
            if (!header.Success) return false;

            // The pair's two lines are the immediately following block; stop at the first line that is not
            // more deeply indented, so a later field with the same child names cannot be read by mistake.
            string indent = header.Groups["indent"].Value;
            string swatch = null;
            string colorId = null;

            foreach (string line in body.Substring(header.Index + header.Length)
                         .Split('\n').Skip(1))
            {
                string trimmedEnd = line.TrimEnd('\r');
                if (trimmedEnd.Length == 0) continue;
                if (!trimmedEnd.StartsWith(indent + " ", StringComparison.Ordinal)) break;

                var child = Regex.Match(trimmedEnd, @"^\s+_?(?<name>swatchName|colorId):\s?(?<value>.*)$");
                if (!child.Success) continue;

                string value = Unquote(child.Groups["value"].Value.Trim());
                if (child.Groups["name"].Value == "swatchName") swatch = value;
                else colorId = value;
            }

            if (swatch == null && colorId == null) return false;

            key = new LegacyColorKey(swatch ?? string.Empty, colorId ?? string.Empty);
            return true;
        }

        /// <summary>Splits an asset's text into YAML documents, keyed by local file id.</summary>
        private static IEnumerable<(long FileId, string Body)> Documents(string text)
        {
            var matches = DocumentHeader.Matches(text);
            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;

                if (long.TryParse(matches[i].Groups["fileId"].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long fileId))
                {
                    yield return (fileId, text.Substring(start, end - start));
                }
            }
        }

        /// <summary>The script GUIDs of the migrated components, keyed lowercase.</summary>
        private static Dictionary<string, string> ResolveScriptGuids()
        {
            var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (type, name) in new[]
                     {
                         (typeof(ColorIDButton), nameof(ColorIDButton)),
                         (typeof(ButtonState), nameof(ButtonState)),
                     })
            {
                var script = MonoScriptOf(type);
                if (script == null) continue;

                string path = AssetDatabase.GetAssetPath(script);
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid)) guids[guid] = name;
            }

            return guids;
        }

        private static MonoScript MonoScriptOf(Type type)
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:MonoScript {type.Name}"))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                if (script != null && script.GetClass() == type) return script;
            }

            return null;
        }

        private static IEnumerable<string> ContainingAssets()
        {
            return AssetDatabase.FindAssets("t:Prefab t:Scene")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                               || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(path => path, StringComparer.Ordinal);
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2
                && ((value[0] == '\'' && value[value.Length - 1] == '\'')
                    || (value[0] == '"' && value[value.Length - 1] == '"')))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}
