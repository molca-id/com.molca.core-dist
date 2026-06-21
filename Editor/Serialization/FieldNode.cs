using System.Collections.Generic;

namespace Molca.Editor
{
    /// <summary>
    /// A neutral, JSON-free structured value handed to <see cref="SerializedFieldCoercion"/> so a field
    /// write can address nested composite objects and lists-of-objects — not just flat scalars. A node is
    /// exactly one of: a <see cref="Scalar"/> leaf, an ordered <see cref="Elements"/> list (array/list
    /// fields), or a named <see cref="Members"/> map (composite serializable types such as
    /// <c>DynamicLocalization</c>). Keeping this independent of Newtonsoft preserves the editor services'
    /// deliberate freedom from any JSON dependency — the MCP layer builds the tree from its own JSON.
    /// </summary>
    /// <remarks>
    /// Lives under <c>Editor/Serialization/</c> (Sprint 25 follow-up): it and
    /// <see cref="SerializedFieldCoercion"/> are general-purpose serialized-property helpers with no
    /// dependency on the sequence system, reusable by any editor tooling in the <c>Molca.Editor</c> assembly.
    /// </remarks>
    internal sealed class FieldNode
    {
        /// <summary>Leaf value in the string form the scalar coercion path parses; null for non-leaves.</summary>
        public string Scalar { get; }

        /// <summary>Ordered child elements for an array/list value; null when this is not a list.</summary>
        public IReadOnlyList<FieldNode> Elements { get; }

        /// <summary>Named child members for a composite object value; null when this is not composite.</summary>
        public IReadOnlyDictionary<string, FieldNode> Members { get; }

        private FieldNode(string scalar, IReadOnlyList<FieldNode> elements,
            IReadOnlyDictionary<string, FieldNode> members)
        {
            Scalar = scalar;
            Elements = elements;
            Members = members;
        }

        /// <summary>True when this node is a scalar leaf.</summary>
        public bool IsScalar => Elements == null && Members == null;

        /// <summary>True when this node is an ordered list of elements.</summary>
        public bool IsList => Elements != null;

        /// <summary>True when this node is a named composite object.</summary>
        public bool IsComposite => Members != null;

        /// <summary>Creates a scalar leaf node.</summary>
        public static FieldNode FromScalar(string scalar) => new FieldNode(scalar, null, null);

        /// <summary>Creates a list node from ordered child elements.</summary>
        public static FieldNode FromList(IReadOnlyList<FieldNode> elements) => new FieldNode(null, elements, null);

        /// <summary>Creates a composite node from named child members.</summary>
        public static FieldNode FromMembers(IReadOnlyDictionary<string, FieldNode> members)
            => new FieldNode(null, null, members);
    }
}
