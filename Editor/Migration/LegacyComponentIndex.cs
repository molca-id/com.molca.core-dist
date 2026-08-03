using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Migration
{
    /// <summary>One component of a retired type, as it exists in a serialized asset.</summary>
    public sealed class LegacyComponentRecord
    {
        /// <summary>The prefab or scene holding it.</summary>
        public string AssetPath { get; }

        /// <summary>The component's local file id.</summary>
        public long FileId { get; }

        /// <summary>The local file id of the GameObject that owns it.</summary>
        public long GameObjectFileId { get; }

        /// <summary>
        /// The component's index in its GameObject's <c>m_Component</c> list, or -1 when unknown.
        /// </summary>
        /// <remarks>
        /// The handle removal needs. Once the type is deleted the component reads as a null entry in that
        /// array and is indistinguishable from any other missing script, so the index has to be learned
        /// here, while the file still says which slot it occupies.
        /// </remarks>
        public int ComponentIndex { get; }

        /// <summary>Serialized field values, keyed by field name with any leading underscore stripped.</summary>
        public IReadOnlyDictionary<string, string> Fields { get; }

        /// <summary>The raw YAML document, for fields this index does not flatten.</summary>
        public string Body { get; }

        /// <summary>Whether the component is an added override on a prefab instance.</summary>
        /// <remarks>
        /// Read from the file rather than from <c>PrefabUtility</c>, which needs the type to resolve the
        /// component at all. An added component belongs to this asset even though its GameObject does not.
        /// </remarks>
        public bool IsAddedComponent { get; }

        /// <summary>Creates a record.</summary>
        public LegacyComponentRecord(string assetPath, long fileId, long gameObjectFileId,
            int componentIndex, IReadOnlyDictionary<string, string> fields, string body,
            bool isAddedComponent)
        {
            AssetPath = assetPath;
            FileId = fileId;
            GameObjectFileId = gameObjectFileId;
            ComponentIndex = componentIndex;
            Fields = fields ?? new Dictionary<string, string>();
            Body = body;
            IsAddedComponent = isAddedComponent;
        }

        /// <summary>Reads one serialized field, accepting either field-name spelling.</summary>
        /// <param name="name">The field name without its underscore, e.g. <c>swatchName</c>.</param>
        /// <returns>The value, or <c>null</c>.</returns>
        public string Field(string name) =>
            Fields.TryGetValue(name, out string value) ? value : null;

        /// <inheritdoc/>
        public override string ToString() =>
            $"{Path.GetFileName(AssetPath)}:&{FileId}[{ComponentIndex}]";
    }

    /// <summary>A scan for components of a retired type; never <c>null</c> members.</summary>
    public sealed class LegacyComponentSnapshot
    {
        /// <summary>Everything found.</summary>
        public IReadOnlyList<LegacyComponentRecord> All { get; }

        /// <summary>Assets that could not be read; a non-empty list makes the scan a lower bound.</summary>
        public IReadOnlyList<string> UnreadableAssets { get; }

        /// <summary>Whether the scan can be trusted to have seen everything.</summary>
        public bool IsConclusive => UnreadableAssets.Count == 0;

        /// <summary>Records grouped by the asset holding them.</summary>
        public IEnumerable<IGrouping<string, LegacyComponentRecord>> ByAsset =>
            All.GroupBy(r => r.AssetPath);

        /// <summary>Creates a snapshot.</summary>
        public LegacyComponentSnapshot(IReadOnlyList<LegacyComponentRecord> all,
            IReadOnlyList<string> unreadable)
        {
            All = all ?? Array.Empty<LegacyComponentRecord>();
            UnreadableAssets = unreadable ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Finds components of a type that no longer exists, by the script GUID content still references.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Migration/</c>.
    /// <b>Shape:</b> editor-only static service. Read-only — nothing here dirties, saves, or loads
    /// prefab contents.
    /// <para/>
    /// <b>Why a GUID and not a <see cref="Type"/>.</b> A migration that ships in the release which
    /// *deletes* the type cannot name it. Content references a MonoBehaviour by its script GUID, and that
    /// reference survives the class being deleted — the component simply becomes a missing script. The
    /// GUID is therefore the durable identity of a retired type, and the only handle a migrator has left.
    /// This is what lets an upgrade path outlive the thing it migrates away from, instead of dying with
    /// it.
    /// <para/>
    /// <b>Both field-name spellings are folded together.</b> Shipped data mostly carries pre-<c>_camelCase</c>
    /// names that <c>FormerlySerializedAs</c> resolves at load, and Unity only rewrites a file's field
    /// names when it re-saves that file. Keys are normalized without the underscore so a caller asks once.
    /// <para/>
    /// Pairs with <see cref="PrefabInstanceOverrideIndex"/>, which does the same for instance overrides;
    /// this one covers the components themselves.
    /// </remarks>
    public static class LegacyComponentIndex
    {
        private static readonly Regex DocumentHeader = new Regex(
            @"^--- !u!(?<classId>\d+) &(?<fileId>-?\d+)(?<stripped>\s+stripped)?",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ScriptGuid = new Regex(
            @"^\s+m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*(?<guid>[0-9a-fA-F]{32})",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex GameObjectRef = new Regex(
            @"^\s+m_GameObject:\s*\{fileID:\s*(?<fileId>-?\d+)\}",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Matches a scalar field line, capturing the name without its leading underscore.</summary>
        private static readonly Regex ScalarField = new Regex(
            @"^  _?(?<name>[A-Za-z]\w*):\s?(?<value>[^\n]*)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Scans prefabs and scenes for components whose script is <paramref name="scriptGuid"/>.
        /// </summary>
        /// <param name="scriptGuid">The retired type's script GUID, 32 hex characters.</param>
        /// <param name="assetFilter">Limits which assets are read; <c>null</c> reads all of them.</param>
        /// <returns>The snapshot; never <c>null</c>.</returns>
        /// <exception cref="ArgumentException">When the GUID is not 32 hex characters.</exception>
        public static LegacyComponentSnapshot Scan(string scriptGuid, Func<string, bool> assetFilter = null)
        {
            if (string.IsNullOrWhiteSpace(scriptGuid) || scriptGuid.Length != 32)
                throw new ArgumentException("A script GUID is 32 hex characters.", nameof(scriptGuid));

            var found = new List<LegacyComponentRecord>();
            var unreadable = new List<string>();

            foreach (string assetPath in ContainingAssets(assetFilter))
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

                // Most of a project cannot contribute, and reading the GUID out of the raw text is far
                // cheaper than splitting every asset into documents.
                if (text.IndexOf(scriptGuid, StringComparison.OrdinalIgnoreCase) < 0) continue;

                try
                {
                    ParseAsset(assetPath, text, scriptGuid, found);
                }
                catch (Exception exception)
                {
                    unreadable.Add($"{assetPath}: {exception.Message}");
                }
            }

            return new LegacyComponentSnapshot(found, unreadable);
        }

        private static void ParseAsset(string assetPath, string text, string scriptGuid,
            List<LegacyComponentRecord> found)
        {
            var documents = Documents(text).ToList();

            // GameObject file id -> its m_Component list, in file order. The index of a component within
            // this list is what removal needs once the type is gone.
            var componentOrder = new Dictionary<long, List<long>>();
            foreach (var (fileId, classId, stripped, body) in documents)
            {
                if (classId != 1 || stripped) continue;   // 1 = GameObject
                componentOrder[fileId] = ComponentFileIds(body).ToList();
            }

            var addedComponents = AddedComponentFileIds(text);

            foreach (var (fileId, classId, stripped, body) in documents)
            {
                if (stripped) continue;
                if (classId != 114) continue;            // 114 = MonoBehaviour

                var script = ScriptGuid.Match(body);
                if (!script.Success
                    || !string.Equals(script.Groups["guid"].Value, scriptGuid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var owner = GameObjectRef.Match(body);
                long ownerId = owner.Success
                    ? long.Parse(owner.Groups["fileId"].Value, CultureInfo.InvariantCulture)
                    : 0;

                int index = -1;
                if (componentOrder.TryGetValue(ownerId, out var order))
                    index = order.IndexOf(fileId);

                found.Add(new LegacyComponentRecord(assetPath, fileId, ownerId, index,
                    ReadFields(body), body, addedComponents.Contains(fileId)));
            }
        }

        /// <summary>Every scalar field on a document, keyed without its leading underscore.</summary>
        /// <remarks>
        /// Two-space indentation only, which is the document's own top level — nested list entries such as
        /// a colour target's <c>- _targetType:</c> are deeper and deliberately excluded, so a nested key
        /// cannot shadow a top-level one of the same name. Callers wanting those read
        /// <see cref="LegacyComponentRecord.Body"/>.
        /// </remarks>
        private static Dictionary<string, string> ReadFields(string body)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Match match in ScalarField.Matches(body))
            {
                string name = match.Groups["name"].Value;
                string value = Unquote(match.Groups["value"].Value.Trim());

                // An underscored spelling is the current one, so it wins over a legacy duplicate if a file
                // somehow carries both.
                bool underscored = match.Value.TrimStart().StartsWith("_", StringComparison.Ordinal);
                if (underscored || !fields.ContainsKey(name)) fields[name] = value;
            }

            return fields;
        }

        /// <summary>The component file ids a GameObject document lists, in order.</summary>
        private static IEnumerable<long> ComponentFileIds(string body)
        {
            int start = body.IndexOf("m_Component:", StringComparison.Ordinal);
            if (start < 0) yield break;

            foreach (Match match in Regex.Matches(body.Substring(start),
                         @"^\s+- component:\s*\{fileID:\s*(-?\d+)\}", RegexOptions.Multiline))
            {
                if (long.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long id))
                {
                    yield return id;
                }
            }
        }

        /// <summary>File ids that appear in some <c>m_AddedComponents</c> block.</summary>
        private static HashSet<long> AddedComponentFileIds(string text)
        {
            var ids = new HashSet<long>();

            foreach (Match block in Regex.Matches(text,
                         @"m_AddedComponents:\n(?<body>(?:\s{4,}[^\n]*\n)*)", RegexOptions.Multiline))
            {
                foreach (Match reference in Regex.Matches(block.Groups["body"].Value,
                             @"addedObject:\s*\{fileID:\s*(-?\d+)\}"))
                {
                    if (long.TryParse(reference.Groups[1].Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out long id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        /// <summary>Splits an asset into YAML documents.</summary>
        private static IEnumerable<(long FileId, int ClassId, bool Stripped, string Body)> Documents(
            string text)
        {
            var matches = DocumentHeader.Matches(text);
            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;

                if (!long.TryParse(matches[i].Groups["fileId"].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long fileId))
                {
                    continue;
                }

                if (!int.TryParse(matches[i].Groups["classId"].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int classId))
                {
                    continue;
                }

                yield return (fileId, classId, matches[i].Groups["stripped"].Success,
                    text.Substring(start, end - start));
            }
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
