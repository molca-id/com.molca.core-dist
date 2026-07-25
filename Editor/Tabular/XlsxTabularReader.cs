using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Tabular
{
    /// <summary>
    /// Reads Excel <c>.xlsx</c>/<c>.xlsm</c> workbooks into a <see cref="TabularDocument"/> by adapting the
    /// existing BCL-only OOXML parser (<see cref="SpreadsheetTableLoader"/>). Discovered by
    /// <see cref="TabularReaderRegistry"/> via <see cref="UnityEditor.TypeCache"/> exactly like any other
    /// reader.
    /// </summary>
    /// <remarks>
    /// XLSX support ships in Core because the parser is BCL-only (<c>System.IO.Compression</c> +
    /// <c>System.Xml</c>, no third-party/encumbered dependency) and already lives in the <c>Molca.Editor</c>
    /// assembly, where <c>CsvStepImporterWindow</c> depends on it. The <see cref="ITabularReader"/> seam is
    /// still the extension point for genuinely heavy or licence-encumbered future formats — those remain
    /// opt-in add-on packages; this reader simply happens to live in Core. Editor-only.
    /// </remarks>
    internal sealed class XlsxTabularReader : ITabularReader
    {
        /// <inheritdoc/>
        public IEnumerable<string> SupportedExtensions => new[] { ".xlsx", ".xlsm" };

        /// <inheritdoc/>
        public TabularDocument Read(string path, TabularReadOptions options = default)
        {
            // Sheet names are only known after a load, so read the first sheet to discover them, then
            // re-read the requested sheet if it isn't index 0. Worksheets are small; the extra read is cheap.
            var rows = SpreadsheetTableLoader.LoadFromPath(path, 0, out var error);
            if (rows == null) throw new TabularReadException(error ?? $"Could not read '{path}'.");

            var sheetNames = new List<string>(SpreadsheetTableLoader.LastXlsxSheetNames);
            int sheetIndex = 0;

            if (!string.IsNullOrWhiteSpace(options.Sheet))
            {
                sheetIndex = sheetNames.FindIndex(n =>
                    string.Equals(n, options.Sheet, StringComparison.OrdinalIgnoreCase));
                if (sheetIndex < 0)
                    throw new TabularReadException(
                        $"No sheet named '{options.Sheet}'. Available: {string.Join(", ", sheetNames)}.");

                if (sheetIndex != 0)
                {
                    rows = SpreadsheetTableLoader.LoadFromPath(path, sheetIndex, out error);
                    if (rows == null) throw new TabularReadException(error ?? $"Could not read '{path}'.");
                }
            }

            return BuildDocument(path, sheetNames, sheetIndex, rows, options);
        }

        /// <summary>
        /// Maps the loader's raw <c>List&lt;string[]&gt;</c> (header row included when present) into the neutral
        /// document, applying header handling and the <see cref="TabularReadOptions.MaxRows"/> cap while still
        /// reporting the full <see cref="TabularDocument.TotalRowCount"/>.
        /// </summary>
        private static TabularDocument BuildDocument(
            string path, List<string> sheetNames, int sheetIndex, List<string[]> rows, TabularReadOptions options)
        {
            int width = rows.Count > 0 ? rows.Max(r => r.Length) : 0;

            bool hasHeader = options.HasHeader && rows.Count > 0;
            var columns = new List<TabularColumn>(width);
            for (int i = 0; i < width; i++)
            {
                string name = hasHeader && i < rows[0].Length && !string.IsNullOrWhiteSpace(rows[0][i])
                    ? rows[0][i]
                    : ColumnLetter(i);
                columns.Add(new TabularColumn(i, name));
            }

            int firstData = hasHeader ? 1 : 0;
            int totalDataRows = Math.Max(0, rows.Count - firstData);
            int cap = options.MaxRows > 0 ? Math.Min(options.MaxRows, totalDataRows) : totalDataRows;

            var dataRows = new List<IReadOnlyList<string>>(cap);
            for (int r = 0; r < cap; r++)
            {
                var src = rows[firstData + r];
                var row = new string[width];
                for (int c = 0; c < width; c++)
                    row[c] = c < src.Length ? (src[c] ?? string.Empty) : string.Empty;
                dataRows.Add(row);
            }

            string sheetName = sheetIndex >= 0 && sheetIndex < sheetNames.Count ? sheetNames[sheetIndex] : null;
            return new TabularDocument(path, sheetName, sheetNames, columns, dataRows, totalDataRows);
        }

        /// <summary>Spreadsheet-style column name for a zero-based index (0→A, 25→Z, 26→AA, …).</summary>
        private static string ColumnLetter(int index)
        {
            var sb = new System.Text.StringBuilder();
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }
    }
}
