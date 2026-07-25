using System;
using System.Collections.Generic;

namespace Molca.Editor.Tabular
{
    /// <summary>
    /// A single column in a <see cref="TabularDocument"/>: its zero-based position and its name (from the
    /// header row, or a synthetic spreadsheet letter when the source has no header).
    /// </summary>
    public sealed class TabularColumn
    {
        /// <summary>Zero-based column index.</summary>
        public int Index { get; }

        /// <summary>Column name — header text, or a spreadsheet-style letter (A, B, …) when headerless.</summary>
        public string Name { get; }

        /// <summary>Creates a column.</summary>
        public TabularColumn(int index, string name)
        {
            Index = index;
            Name = name ?? string.Empty;
        }
    }

    /// <summary>
    /// Format-neutral tabular data: a list of named columns and a list of rows, every cell a raw string.
    /// Coercion to typed values happens only at bind time (see <c>TabularBindingService</c>), so the same
    /// serialized-field coercion path is authoritative for every target type and the model stays independent
    /// of any particular source format.
    /// </summary>
    /// <remarks>
    /// Rows are column-aligned: every row has exactly <see cref="Columns"/><c>.Count</c> cells, padded with
    /// empty strings where the source was ragged. Cells are never null.
    /// </remarks>
    public sealed class TabularDocument
    {
        /// <summary>The path the document was read from.</summary>
        public string SourcePath { get; }

        /// <summary>The sheet these rows came from, or null for single-sheet formats (CSV/TSV).</summary>
        public string SheetName { get; }

        /// <summary>All sheet names available in the source (empty/single for single-sheet formats).</summary>
        public IReadOnlyList<string> SheetNames { get; }

        /// <summary>The columns, in order.</summary>
        public IReadOnlyList<TabularColumn> Columns { get; }

        /// <summary>The data rows; each is a column-aligned list of cell strings (never null cells).</summary>
        public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

        /// <summary>
        /// Total data-row count in the source before any <see cref="TabularReadOptions.MaxRows"/> cap.
        /// Equal to <see cref="Rows"/><c>.Count</c> when nothing was truncated.
        /// </summary>
        public int TotalRowCount { get; }

        /// <summary>True when a row cap dropped rows: <see cref="Rows"/><c>.Count &lt;</c> <see cref="TotalRowCount"/>.</summary>
        public bool Truncated => Rows.Count < TotalRowCount;

        /// <summary>Creates a document. See the properties for each argument's meaning.</summary>
        public TabularDocument(
            string sourcePath,
            string sheetName,
            IReadOnlyList<string> sheetNames,
            IReadOnlyList<TabularColumn> columns,
            IReadOnlyList<IReadOnlyList<string>> rows,
            int totalRowCount)
        {
            SourcePath = sourcePath;
            SheetName = sheetName;
            SheetNames = sheetNames ?? Array.Empty<string>();
            Columns = columns ?? Array.Empty<TabularColumn>();
            Rows = rows ?? Array.Empty<IReadOnlyList<string>>();
            TotalRowCount = totalRowCount;
        }

        /// <summary>
        /// The zero-based index of the column named <paramref name="name"/> (case-insensitive), or -1 if
        /// there is no such column.
        /// </summary>
        public int IndexOfColumn(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < Columns.Count; i++)
                if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }
}
