using System.Collections.Generic;
using Molca.Sequence;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// Single Undo-grouped path for writing a <see cref="Step"/>'s serialized fields by name — the
    /// configuration counterpart to <see cref="StepEditingService"/> (which only creates/restructures
    /// steps). Edit mode only; no GUI dependencies. Field coercion and value reading are delegated to the
    /// general-purpose <see cref="SerializedFieldCoercion"/> (in <c>Editor/Serialization/</c>).
    /// </summary>
    public static class StepFieldEditingService
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

        /// <summary>One serialized field's current value, for read-back (Sprint 25 follow-up).</summary>
        public readonly struct FieldValue
        {
            /// <summary>Serialized field name.</summary>
            public string Name { get; }

            /// <summary>The field's <see cref="SerializedPropertyType"/> name (e.g. "String", "Float", "Generic").</summary>
            public string Type { get; }

            /// <summary>Current value in the string form the field setters consume (composite/array forms are informational).</summary>
            public string Value { get; }

            internal FieldValue(string name, string type, string value)
            {
                Name = name;
                Type = type;
                Value = value;
            }
        }

        /// <summary>
        /// Writes the given serialized fields on <paramref name="step"/> as one undo group. Unknown
        /// fields, the managed <c>auxiliaries</c> list (use <see cref="AuxiliaryEditingService"/>), and
        /// the script reference are rejected with a reason rather than written.
        /// </summary>
        /// <param name="step">The step to configure.</param>
        /// <param name="fields">Field name → string value (coerced by <see cref="SerializedFieldCoercion"/>).</param>
        /// <returns>Which fields were applied and which were rejected (and why).</returns>
        public static SetFieldsResult SetFields(Step step, IReadOnlyDictionary<string, string> fields)
            => SetFields(step, WrapScalars(fields));

        /// <summary>
        /// Structured counterpart to <see cref="SetFields(Step, IReadOnlyDictionary{string, string})"/>:
        /// each value is a <see cref="FieldNode"/>, so a field can carry a nested composite object or a
        /// list of objects (e.g. a <c>DynamicLocalization</c> with per-language <c>translations</c>).
        /// </summary>
        /// <param name="step">The step to configure.</param>
        /// <param name="fields">Field name → structured value (coerced by <see cref="SerializedFieldCoercion"/>).</param>
        /// <returns>Which fields were applied and which were rejected (and why).</returns>
        internal static SetFieldsResult SetFields(Step step, IReadOnlyDictionary<string, FieldNode> fields)
        {
            var applied = new List<string>();
            var rejected = new List<KeyValuePair<string, string>>();
            if (step == null || fields == null) return new SetFieldsResult(applied, rejected);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Set Step Fields");
            int group = Undo.GetCurrentGroup();

            var so = new SerializedObject(step);
            foreach (var pair in fields)
            {
                if (pair.Key == "auxiliaries")
                {
                    rejected.Add(new KeyValuePair<string, string>(pair.Key,
                        "use the auxiliary tools (add/remove/set_auxiliary_fields) for the auxiliaries list"));
                    continue;
                }
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

            if (applied.Count > 0) so.ApplyModifiedProperties();
            step.EnsureAuxiliaryOwnerReferences();
            Undo.CollapseUndoOperations(group);

            return new SetFieldsResult(applied, rejected);
        }

        /// <summary>Wraps a flat string field map as scalar <see cref="FieldNode"/>s for the structured path.</summary>
        internal static Dictionary<string, FieldNode> WrapScalars(IReadOnlyDictionary<string, string> fields)
        {
            var map = new Dictionary<string, FieldNode>();
            if (fields != null)
                foreach (var pair in fields)
                    map[pair.Key] = FieldNode.FromScalar(pair.Value);
            return map;
        }

        /// <summary>
        /// Lists the writable serialized field names on <paramref name="step"/> (excludes the script
        /// reference and the auxiliaries list), for surfacing valid options when a write is rejected.
        /// </summary>
        /// <param name="step">The step to inspect.</param>
        /// <returns>Writable serialized field names.</returns>
        public static List<string> GetWritableFields(Step step)
        {
            var names = new List<string>();
            if (step == null) return names;

            var iterator = new SerializedObject(step).GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false; // top-level fields only
                if (iterator.name == "m_Script" || iterator.name == "auxiliaries") continue;
                names.Add(iterator.name);
            }
            return names;
        }

        /// <summary>
        /// Reads the current values of a step's writable serialized fields (Sprint 25 follow-up) — the
        /// read counterpart to <see cref="SetFields(Step, IReadOnlyDictionary{string, string})"/>, so an
        /// assistant can see current values before editing. Excludes the script reference and the
        /// auxiliaries list (read those via <see cref="AuxiliaryEditingService.GetAuxiliaryFields"/>).
        /// </summary>
        /// <param name="step">The step to read.</param>
        /// <returns>Each writable field's name, type, and current value.</returns>
        public static List<FieldValue> GetFields(Step step)
        {
            var values = new List<FieldValue>();
            if (step == null) return values;

            var iterator = new SerializedObject(step).GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false; // top-level fields only
                if (iterator.name == "m_Script" || iterator.name == "auxiliaries") continue;
                values.Add(new FieldValue(iterator.name, iterator.propertyType.ToString(),
                    SerializedFieldCoercion.ReadValue(iterator)));
            }
            return values;
        }
    }
}
