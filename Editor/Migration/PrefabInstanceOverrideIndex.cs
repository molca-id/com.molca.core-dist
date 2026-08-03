using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Molca.Editor.Migration
{
    /// <summary>
    /// One property a prefab instance overrides on the source object it came from.
    /// </summary>
    /// <remarks>
    /// Read straight out of the containing asset's serialized form, so it describes the file rather than
    /// Unity's in-memory reconstruction of it. That matters here: the whole point of the index is to see
    /// overrides that name fields a migration is about to stop reading, and those are exactly the ones
    /// Unity's object model shows as ordinary values or hides entirely.
    /// </remarks>
    public sealed class PrefabInstanceOverride
    {
        /// <summary>Project-relative path of the prefab or scene that holds the instance.</summary>
        public string ContainingAssetPath { get; }

        /// <summary>The <c>PrefabInstance</c>'s own local file id inside that asset.</summary>
        public long InstanceFileId { get; }

        /// <summary>GUID of the prefab the instance was created from.</summary>
        public string SourcePrefabGuid { get; }

        /// <summary>GUID of the prefab that <i>defines</i> the overridden object.</summary>
        /// <remarks>
        /// Not always <see cref="SourcePrefabGuid"/>. An override on an object that the source itself
        /// nests names the deeper prefab, which is what makes this the right key to match a migration
        /// site on: the site is planned against the asset that owns the object.
        /// </remarks>
        public string TargetGuid { get; }

        /// <summary>Local file id of the overridden object inside <see cref="TargetGuid"/>.</summary>
        public long TargetFileId { get; }

        /// <summary>The overridden serialized property path, exactly as written.</summary>
        public string PropertyPath { get; }

        /// <summary>The overriding value, or empty for an object-reference override.</summary>
        public string Value { get; }

        /// <summary>Whether the containing asset is a scene rather than a prefab.</summary>
        public bool ContainerIsScene =>
            ContainingAssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);

        internal PrefabInstanceOverride(string containingAssetPath, long instanceFileId,
            string sourcePrefabGuid, string targetGuid, long targetFileId, string propertyPath, string value)
        {
            ContainingAssetPath = containingAssetPath;
            InstanceFileId = instanceFileId;
            SourcePrefabGuid = sourcePrefabGuid;
            TargetGuid = targetGuid;
            TargetFileId = targetFileId;
            PropertyPath = propertyPath;
            Value = value;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"{ContainingAssetPath} overrides {PropertyPath} = '{Value}'";
    }

    /// <summary>Every instance override in the project that a migration might invalidate.</summary>
    public sealed class PrefabInstanceOverrideSnapshot
    {
        private readonly Dictionary<string, List<PrefabInstanceOverride>> _byTarget;

        /// <summary>Every override the scan matched.</summary>
        public IReadOnlyList<PrefabInstanceOverride> All { get; }

        /// <summary>Assets that were read but could not be parsed, with the reason.</summary>
        /// <remarks>
        /// Surfaced rather than swallowed. An unreadable asset means the index cannot answer the
        /// question for that file, and a migrator that treats "no overrides found" and "could not look"
        /// as the same answer is exactly the silent failure this whole mechanism exists to remove.
        /// </remarks>
        public IReadOnlyList<string> UnreadableAssets { get; }

        internal PrefabInstanceOverrideSnapshot(List<PrefabInstanceOverride> all, List<string> unreadable)
        {
            All = all;
            UnreadableAssets = unreadable;

            _byTarget = new Dictionary<string, List<PrefabInstanceOverride>>(StringComparer.Ordinal);
            foreach (var entry in all)
            {
                if (!_byTarget.TryGetValue(entry.TargetGuid, out var list))
                    _byTarget[entry.TargetGuid] = list = new List<PrefabInstanceOverride>();
                list.Add(entry);
            }
        }

        /// <summary>Overrides that name an object defined by the prefab with this GUID.</summary>
        /// <param name="targetGuid">The defining prefab's GUID.</param>
        /// <returns>The overrides; never <c>null</c>.</returns>
        public IReadOnlyList<PrefabInstanceOverride> ForTarget(string targetGuid) =>
            !string.IsNullOrEmpty(targetGuid) && _byTarget.TryGetValue(targetGuid, out var list)
                ? list
                : Array.Empty<PrefabInstanceOverride>();

        /// <summary>Overrides on one specific object of one prefab.</summary>
        /// <param name="targetGuid">The defining prefab's GUID.</param>
        /// <param name="targetFileId">The object's local file id inside that prefab.</param>
        public IEnumerable<PrefabInstanceOverride> ForObject(string targetGuid, long targetFileId) =>
            ForTarget(targetGuid).Where(o => o.TargetFileId == targetFileId);
    }

    /// <summary>
    /// Finds the prefab-instance overrides that a component-level schema migration would silently drop.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Migration/</c>.
    /// <b>Shape:</b> editor-only static service. Read-only — nothing here writes, dirties or saves.
    /// <para/>
    /// <b>Why this exists.</b> A migrator that rewrites a component rewrites the prefab that owns it, and
    /// Unity's prefab instances are not copies. An instance overriding a migrated field keeps overriding
    /// the <i>old</i> field, which the new schema no longer reads: the source migrates, the instance
    /// reverts to the source's value, and nothing errors, because a modification naming an unread field
    /// is inert rather than invalid. Every migrator that writes to a component needs to be able to ask
    /// this question, so it is asked in one place.
    /// <para/>
    /// <b>Why it reads YAML.</b> An override is serialized as a <c>propertyPath</c>/<c>value</c> pair
    /// under the instance, not as a field on any object. Loading the containing asset and inspecting it
    /// through <c>SerializedObject</c> shows the reconstructed result, in which an override of a field
    /// the migration is about to orphan is indistinguishable from an ordinary value. The file is the only
    /// place the distinction survives. It is also far cheaper: no asset is loaded, no nested prefab is
    /// materialized, and nothing can be accidentally dirtied by looking.
    /// </remarks>
    public static class PrefabInstanceOverrideIndex
    {
        /// <summary>Document header of a serialized <c>PrefabInstance</c>, with its local file id.</summary>
        private static readonly Regex InstanceHeader =
            new Regex(@"^1001 &(-?\d+)", RegexOptions.Compiled);

        /// <summary>The <c>m_SourcePrefab</c> line inside a <c>PrefabInstance</c>.</summary>
        private static readonly Regex SourcePrefab =
            new Regex(@"^\s*m_SourcePrefab:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})",
                RegexOptions.Compiled);

        /// <summary>The <c>- target:</c> line that opens one modification entry.</summary>
        private static readonly Regex ModificationTarget =
            new Regex(@"^\s*-\s*target:\s*\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-f]{32})",
                RegexOptions.Compiled);

        private static readonly Regex ModificationPropertyPath =
            new Regex(@"^\s*propertyPath:\s*(.*)$", RegexOptions.Compiled);

        private static readonly Regex ModificationValue =
            new Regex(@"^\s*value:\s*(.*)$", RegexOptions.Compiled);

        /// <summary>
        /// Scans the project for instance overrides whose property path the caller cares about.
        /// </summary>
        /// <param name="propertyPathMatches">
        /// Decides whether a property path is one the migration would orphan. Required — an index built
        /// without a predicate would hold every override in the project for no purpose.
        /// </param>
        /// <param name="propertyHints">
        /// Substrings that must appear somewhere in an asset's text for it to be worth parsing. Purely an
        /// optimization: a hint that is too narrow silently shrinks the answer, so pass the raw spellings
        /// the predicate accepts, or <c>null</c> to parse everything.
        /// </param>
        /// <param name="containingAssetFilter">
        /// Limits which prefabs and scenes are read. <c>null</c> reads all of them.
        /// </param>
        /// <returns>The snapshot; never <c>null</c>.</returns>
        /// <exception cref="ArgumentNullException">
        /// When <paramref name="propertyPathMatches"/> is <c>null</c>.
        /// </exception>
        public static PrefabInstanceOverrideSnapshot Scan(
            Func<string, bool> propertyPathMatches,
            IReadOnlyList<string> propertyHints = null,
            Func<string, bool> containingAssetFilter = null)
        {
            if (propertyPathMatches == null) throw new ArgumentNullException(nameof(propertyPathMatches));

            var found = new List<PrefabInstanceOverride>();
            var unreadable = new List<string>();

            foreach (string assetPath in ContainingAssets(containingAssetFilter))
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

                // A file with no instance modifications at all cannot contribute, and most of a project's
                // prefabs are in that state.
                if (text.IndexOf("m_Modifications:", StringComparison.Ordinal) < 0) continue;
                if (propertyHints != null && propertyHints.Count > 0
                    && !propertyHints.Any(hint => text.IndexOf(hint, StringComparison.Ordinal) >= 0))
                {
                    continue;
                }

                try
                {
                    ParseAsset(assetPath, text, propertyPathMatches, found);
                }
                catch (Exception exception)
                {
                    unreadable.Add($"{assetPath}: {exception.Message}");
                }
            }

            return new PrefabInstanceOverrideSnapshot(found, unreadable);
        }

        /// <summary>
        /// Maps every component of one type in a prefab asset to the local file id an override names it by.
        /// </summary>
        /// <typeparam name="T">The component type to map.</typeparam>
        /// <param name="assetPath">Project-relative path of the prefab.</param>
        /// <returns>File id keyed by component; empty when the asset is not a loadable prefab.</returns>
        /// <remarks>
        /// The other half of matching an override to a migration site. An override names its target as
        /// <c>(guid, fileID)</c>; a plan names it as a hierarchy path. This is read from the prefab
        /// <i>asset</i> rather than from <c>LoadPrefabContents</c>, because only the asset representation
        /// carries the persistent ids — the loaded copy's objects are scene objects with none.
        /// </remarks>
        public static Dictionary<T, long> MapComponentFileIds<T>(string assetPath) where T : UnityEngine.Component
        {
            var map = new Dictionary<T, long>();

            var root = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(assetPath);
            if (root == null) return map;

            foreach (var component in root.GetComponentsInChildren<T>(true))
            {
                if (component == null) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component, out _, out long fileId))
                    map[component] = fileId;
            }

            return map;
        }

        private static IEnumerable<string> ContainingAssets(Func<string, bool> filter)
        {
            return AssetDatabase.FindAssets("t:Prefab t:Scene")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                               || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Where(path => filter == null || filter(path))
                .OrderBy(path => path, StringComparer.Ordinal);
        }

        /// <summary>
        /// Walks one asset's YAML documents, collecting matching modifications per <c>PrefabInstance</c>.
        /// </summary>
        /// <remarks>
        /// Two passes over each instance document rather than one, because <c>m_SourcePrefab</c> is
        /// serialized <i>after</i> <c>m_Modifications</c> and every modification needs it.
        /// </remarks>
        private static void ParseAsset(string assetPath, string text,
            Func<string, bool> propertyPathMatches, List<PrefabInstanceOverride> found)
        {
            foreach (var (instanceFileId, body) in InstanceDocuments(text))
            {
                var pending = new List<(string TargetGuid, long TargetFileId, string Path, string Value)>();
                string sourceGuid = null;

                var lines = body.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    var sourceMatch = SourcePrefab.Match(line);
                    if (sourceMatch.Success)
                    {
                        sourceGuid = sourceMatch.Groups[1].Value;
                        continue;
                    }

                    var targetMatch = ModificationTarget.Match(line);
                    if (!targetMatch.Success) continue;

                    // A modification is three consecutive lines. Reading them positionally rather than by
                    // searching keeps one malformed entry from stealing the next entry's value.
                    if (i + 1 >= lines.Length) continue;
                    var pathMatch = ModificationPropertyPath.Match(lines[i + 1]);
                    if (!pathMatch.Success) continue;

                    string propertyPath = pathMatch.Groups[1].Value.Trim();
                    if (!propertyPathMatches(propertyPath)) continue;

                    string value = string.Empty;
                    if (i + 2 < lines.Length)
                    {
                        var valueMatch = ModificationValue.Match(lines[i + 2]);
                        if (valueMatch.Success) value = Unquote(valueMatch.Groups[1].Value.Trim());
                    }

                    long targetFileId = long.Parse(targetMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    pending.Add((targetMatch.Groups[2].Value, targetFileId, propertyPath, value));
                }

                if (pending.Count == 0) continue;

                foreach (var entry in pending)
                {
                    found.Add(new PrefabInstanceOverride(assetPath, instanceFileId, sourceGuid ?? string.Empty,
                        entry.TargetGuid, entry.TargetFileId, entry.Path, entry.Value));
                }
            }
        }

        /// <summary>Yields the body of every <c>PrefabInstance</c> document, with its local file id.</summary>
        private static IEnumerable<(long FileId, string Body)> InstanceDocuments(string text)
        {
            // Unity writes one YAML document per object, each opened by "--- !u!<class> &<fileID>".
            string[] documents = text.Split(new[] { "--- !u!" }, StringSplitOptions.None);

            for (int i = 1; i < documents.Length; i++)
            {
                var header = InstanceHeader.Match(documents[i]);
                if (!header.Success) continue;

                yield return (long.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture), documents[i]);
            }
        }

        /// <summary>Strips the quoting Unity applies to values that need it.</summary>
        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("''", "'");

            return value;
        }
    }
}
