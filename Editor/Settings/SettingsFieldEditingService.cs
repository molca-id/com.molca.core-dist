using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Single Undo-grouped path for reading and writing a settings <see cref="ScriptableObject"/>'s
    /// serialized fields by name — the settings counterpart to <see cref="StepFieldEditingService"/>.
    /// Edit mode only; no GUI dependencies. Field coercion and value reading are delegated to the
    /// general-purpose <see cref="SerializedFieldCoercion"/> (in <c>Editor/Serialization/</c>).
    /// </summary>
    /// <remarks>
    /// These tools edit the asset's authored SerializeFields on disk — i.e. <i>authoring</i>, identical
    /// to editing the asset in the Inspector. This is distinct from the runtime mutation forbidden by the
    /// settings cardinal rule (which concerns play-mode writes via <c>SettingState</c>). Writes go through
    /// Unity's <see cref="Undo"/> stack (Ctrl+Z reverts) and the asset is marked dirty + saved.
    /// </remarks>
    public static class SettingsFieldEditingService
    {
        /// <summary>Result of a <see cref="SetFields"/> call.</summary>
        public readonly struct SetFieldsResult
        {
            /// <summary>Names of fields successfully written.</summary>
            public IReadOnlyList<string> Applied { get; }

            /// <summary>Field name → reason for fields that could not be written.</summary>
            public IReadOnlyList<KeyValuePair<string, string>> Rejected { get; }

            internal SetFieldsResult(List<string> applied, List<KeyValuePair<string, string>> rejected)
            {
                Applied = applied;
                Rejected = rejected;
            }
        }

        /// <summary>One serialized field's current value, for read-back.</summary>
        public readonly struct FieldValue
        {
            /// <summary>Serialized field name.</summary>
            public string Name { get; }

            /// <summary>The field's <see cref="SerializedPropertyType"/> name (e.g. "String", "Float", "Generic").</summary>
            public string Type { get; }

            /// <summary>Current value in the string form the field setters consume.</summary>
            public string Value { get; }

            internal FieldValue(string name, string type, string value)
            {
                Name = name;
                Type = type;
                Value = value;
            }
        }

        /// <summary>
        /// Writes the given serialized fields on <paramref name="asset"/> as one undo group, then marks
        /// the asset dirty and saves it. Unknown fields and the script reference are rejected with a
        /// reason rather than written.
        /// </summary>
        /// <param name="asset">The settings asset to configure.</param>
        /// <param name="fields">Field name → structured value (coerced by <see cref="SerializedFieldCoercion"/>).</param>
        /// <returns>Which fields were applied and which were rejected (and why).</returns>
        internal static SetFieldsResult SetFields(ScriptableObject asset, IReadOnlyDictionary<string, FieldNode> fields)
        {
            var applied = new List<string>();
            var rejected = new List<KeyValuePair<string, string>>();
            if (asset == null || fields == null) return new SetFieldsResult(applied, rejected);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Set Settings Fields");
            int group = Undo.GetCurrentGroup();

            var so = new SerializedObject(asset);
            foreach (var pair in fields)
            {
                if (pair.Key == "m_Script")
                {
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, "the script reference is read-only"));
                    continue;
                }

                var prop = so.FindProperty(pair.Key);
                if (prop == null)
                {
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, "no such serialized field"));
                    continue;
                }
                if (SerializedFieldCoercion.TrySet(prop, pair.Value, out var error))
                    applied.Add(pair.Key);
                else
                    rejected.Add(new KeyValuePair<string, string>(pair.Key, error));
            }

            if (applied.Count > 0)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
            }
            Undo.CollapseUndoOperations(group);

            return new SetFieldsResult(applied, rejected);
        }

        /// <summary>
        /// Lists the writable serialized field names on <paramref name="asset"/> (excludes the script
        /// reference), for surfacing valid options when a write is rejected.
        /// </summary>
        /// <param name="asset">The settings asset to inspect.</param>
        /// <returns>Writable serialized field names.</returns>
        public static List<string> GetWritableFields(ScriptableObject asset)
        {
            var names = new List<string>();
            if (asset == null) return names;

            var iterator = new SerializedObject(asset).GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false; // top-level fields only
                if (iterator.name == "m_Script") continue;
                names.Add(iterator.name);
            }
            return names;
        }

        /// <summary>
        /// Reads the current values of an asset's writable serialized fields — the read counterpart to
        /// <see cref="SetFields"/>, so an assistant can see current values before editing. Excludes the
        /// script reference.
        /// </summary>
        /// <param name="asset">The settings asset to read.</param>
        /// <returns>Each writable field's name, type, and current value.</returns>
        public static List<FieldValue> GetFields(ScriptableObject asset)
        {
            var values = new List<FieldValue>();
            if (asset == null) return values;

            var iterator = new SerializedObject(asset).GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false; // top-level fields only
                if (iterator.name == "m_Script") continue;
                values.Add(new FieldValue(iterator.name, iterator.propertyType.ToString(),
                    SerializedFieldCoercion.ReadValue(iterator)));
            }
            return values;
        }
    }
}
