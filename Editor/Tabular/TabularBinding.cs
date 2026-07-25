using System.Collections.Generic;

namespace Molca.Editor.Tabular
{
    /// <summary>How a mapping's key column selects the entity each row applies to.</summary>
    internal enum TargetSelectorKind
    {
        /// <summary>Key is a Ref Id resolved against live <c>IReferenceable</c> components in the loaded scene(s).</summary>
        RefId,

        /// <summary>Key is a GameObject name or '/'-separated hierarchy path in the loaded scene(s).</summary>
        Scene,

        /// <summary>Key is a project asset path (e.g. a ScriptableObject) loaded via <c>AssetDatabase</c>.</summary>
        AssetPath
    }

    /// <summary>One column → target-field binding within a mapping.</summary>
    internal readonly struct TabularBindingField
    {
        /// <summary>Source column name whose cell value is written.</summary>
        public string Column { get; }

        /// <summary>
        /// Target field. For scene targets: <c>"name"</c> (the GameObject's name) or
        /// <c>"ComponentType/fieldPath"</c>. For asset targets: a serialized field path on the asset.
        /// A <c>SceneObjectReference</c> field is set by putting the destination Ref Id in the cell.
        /// </summary>
        public string Target { get; }

        /// <summary>Creates a binding.</summary>
        public TabularBindingField(string column, string target)
        {
            Column = column;
            Target = target;
        }
    }

    /// <summary>
    /// A resolved mapping instruction: rows of cell data, the key column that selects each target, how to
    /// interpret that key, and the column → field bindings to write. Source-agnostic — the rows may come
    /// from a sheet, a query result, or anywhere else.
    /// </summary>
    internal sealed class TabularBindingSpec
    {
        /// <summary>Row data, each row a column-name → cell-value map.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

        /// <summary>Column whose value selects the target entity for each row.</summary>
        public string KeyColumn { get; }

        /// <summary>How <see cref="KeyColumn"/> values are resolved to entities.</summary>
        public TargetSelectorKind Selector { get; }

        /// <summary>The column → field bindings applied to each resolved target.</summary>
        public IReadOnlyList<TabularBindingField> Bindings { get; }

        /// <summary>Creates a mapping spec.</summary>
        public TabularBindingSpec(
            IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
            string keyColumn,
            TargetSelectorKind selector,
            IReadOnlyList<TabularBindingField> bindings)
        {
            Rows = rows;
            KeyColumn = keyColumn;
            Selector = selector;
            Bindings = bindings;
        }
    }

    /// <summary>One successfully-resolved-and-coerced change (planned or applied).</summary>
    internal readonly struct BindingChange
    {
        public string RowKey { get; }
        public string Target { get; }
        public string Field { get; }
        public string OldValue { get; }
        public string NewValue { get; }

        public BindingChange(string rowKey, string target, string field, string oldValue, string newValue)
        {
            RowKey = rowKey;
            Target = target;
            Field = field;
            OldValue = oldValue;
            NewValue = newValue;
        }
    }

    /// <summary>One binding that could not be applied, with the reason.</summary>
    internal readonly struct BindingReject
    {
        public string RowKey { get; }
        public string Target { get; }
        public string Field { get; }
        public string Reason { get; }

        public BindingReject(string rowKey, string target, string field, string reason)
        {
            RowKey = rowKey;
            Target = target;
            Field = field;
            Reason = reason;
        }
    }

    /// <summary>The outcome of a plan or apply: what changed (or would change) and what was rejected.</summary>
    internal sealed class BindingResult
    {
        public List<BindingChange> Applied { get; } = new List<BindingChange>();
        public List<BindingReject> Rejected { get; } = new List<BindingReject>();
    }
}
