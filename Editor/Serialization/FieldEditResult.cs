using System.Collections.Generic;

namespace Molca.Editor
{
    /// <summary>
    /// Result of writing a set of named serialized fields on a component: which were applied, and which
    /// were rejected (with a reason). Neutral shape — no Sequence coupling — shared by any editor tooling
    /// in the <c>Molca.Editor</c> assembly that edits fields by name; see <see cref="SerializedFieldCoercion"/>.
    /// </summary>
    public readonly struct FieldEditResult
    {
        /// <summary>Names of fields successfully written.</summary>
        public IReadOnlyList<string> Applied { get; }

        /// <summary>Field name → reason for fields that could not be written.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Rejected { get; }

        internal FieldEditResult(List<string> applied, List<KeyValuePair<string, string>> rejected)
        {
            Applied = applied;
            Rejected = rejected;
        }
    }
}
