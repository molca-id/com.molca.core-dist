namespace Molca.Editor.Tabular
{
    /// <summary>
    /// Options controlling how an <see cref="ITabularReader"/> parses a source into a
    /// <see cref="TabularDocument"/>. A default-constructed value (<c>default</c>) is equivalent to
    /// <see cref="Default"/>: header on, no row cap, first/active sheet.
    /// </summary>
    public struct TabularReadOptions
    {
        /// <summary>
        /// Sheet to read, by name, for multi-sheet formats (XLSX). Null/blank selects the first (or active)
        /// sheet. Ignored by single-sheet formats (CSV/TSV) — a non-matching value is not an error there.
        /// </summary>
        public string Sheet { get; set; }

        /// <summary>
        /// When true (the default), the first record is treated as column names. When false, columns are
        /// named with spreadsheet-style letters (A, B, …, Z, AA, …) and the first record is data.
        /// </summary>
        public bool HasHeader { get; set; }

        /// <summary>
        /// Maximum number of data rows to return. <c>0</c> means no cap. When a cap truncates the source,
        /// <see cref="TabularDocument.TotalRowCount"/> still reflects the full count and
        /// <see cref="TabularDocument.Truncated"/> is true, so truncation is never silent.
        /// </summary>
        public int MaxRows { get; set; }

        /// <summary>Header on, no row cap, first/active sheet.</summary>
        public static TabularReadOptions Default => new TabularReadOptions { HasHeader = true, MaxRows = 0 };
    }
}
