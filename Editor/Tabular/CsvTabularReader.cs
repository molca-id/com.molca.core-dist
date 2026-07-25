using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Molca.Editor.Tabular
{
    /// <summary>
    /// Zero-dependency RFC 4180 reader for comma-separated (<c>.csv</c>) and tab-separated (<c>.tsv</c>)
    /// files. Ships in Core and is discovered by <see cref="TabularReaderRegistry"/> like any add-on reader,
    /// so CSV works out of the box while heavier formats (XLSX) remain optional packages.
    /// </summary>
    /// <remarks>
    /// Handles quoted fields, escaped quotes (<c>""</c>), delimiters and newlines embedded inside quotes,
    /// CRLF and LF line endings, and an optional trailing newline. The delimiter is chosen by extension
    /// (comma for <c>.csv</c>, tab for <c>.tsv</c>). <see cref="TabularReadOptions.Sheet"/> is ignored (a
    /// single-sheet format). The whole file is parsed to count <see cref="TabularDocument.TotalRowCount"/>
    /// accurately, then <see cref="TabularReadOptions.MaxRows"/> caps the returned rows.
    /// </remarks>
    internal sealed class CsvTabularReader : ITabularReader
    {
        /// <inheritdoc/>
        public IEnumerable<string> SupportedExtensions => new[] { ".csv", ".tsv" };

        /// <inheritdoc/>
        public TabularDocument Read(string path, TabularReadOptions options = default)
        {
            var resolved = ResolvePath(path);
            string text;
            try
            {
                text = File.ReadAllText(resolved);
            }
            catch (Exception ex)
            {
                throw new TabularReadException($"Could not read '{path}': {ex.Message}", ex);
            }

            var delimiter = resolved.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
            var records = ParseRecords(text, delimiter);

            // Build columns from the header (or synthesize letters), then align every data row to the
            // column count. Column count is the widest record so ragged rows never lose trailing cells.
            int width = 0;
            foreach (var rec in records) width = Math.Max(width, rec.Count);

            bool hasHeader = options.HasHeader && records.Count > 0;
            var columns = new List<TabularColumn>(width);
            if (hasHeader)
            {
                var header = records[0];
                for (int i = 0; i < width; i++)
                {
                    var name = i < header.Count && !string.IsNullOrWhiteSpace(header[i])
                        ? header[i]
                        : ColumnLetter(i);
                    columns.Add(new TabularColumn(i, name));
                }
            }
            else
            {
                for (int i = 0; i < width; i++)
                    columns.Add(new TabularColumn(i, ColumnLetter(i)));
            }

            int firstData = hasHeader ? 1 : 0;
            int totalDataRows = Math.Max(0, records.Count - firstData);
            int cap = options.MaxRows > 0 ? Math.Min(options.MaxRows, totalDataRows) : totalDataRows;

            var rows = new List<IReadOnlyList<string>>(cap);
            for (int r = 0; r < cap; r++)
            {
                var src = records[firstData + r];
                var row = new string[width];
                for (int c = 0; c < width; c++)
                    row[c] = c < src.Count ? src[c] : string.Empty;
                rows.Add(row);
            }

            return new TabularDocument(
                sourcePath: resolved,
                sheetName: null,
                sheetNames: Array.Empty<string>(),
                columns: columns,
                rows: rows,
                totalRowCount: totalDataRows);
        }

        /// <summary>
        /// Splits CSV/TSV text into records of fields with a single-pass state machine. A quote toggles
        /// quoted mode; a doubled quote inside quoted mode is a literal quote; delimiters and newlines are
        /// field/record separators only outside quotes. A trailing newline does not emit a spurious empty
        /// record.
        /// </summary>
        private static List<List<string>> ParseRecords(string text, char delimiter)
        {
            var records = new List<List<string>>();
            var field = new StringBuilder();
            var record = new List<string>();
            bool inQuotes = false;
            bool fieldStarted = false; // distinguishes a real empty trailing record from "no content yet"

            void EndField()
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }

            void EndRecord()
            {
                EndField();
                records.Add(record);
                record = new List<string>();
            }

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(ch);
                    continue;
                }

                if (ch == '"') { inQuotes = true; fieldStarted = true; }
                else if (ch == delimiter) { fieldStarted = true; EndField(); }
                else if (ch == '\r')
                {
                    // Swallow a following \n so CRLF is one record boundary.
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    EndRecord();
                }
                else if (ch == '\n') EndRecord();
                else { field.Append(ch); fieldStarted = true; }
            }

            // Flush the final record unless the file ended exactly on a record boundary (no dangling field).
            if (fieldStarted || field.Length > 0 || record.Count > 0)
                EndRecord();

            return records;
        }

        /// <summary>Spreadsheet-style column name for a zero-based index (0→A, 25→Z, 26→AA, …).</summary>
        private static string ColumnLetter(int index)
        {
            var sb = new StringBuilder();
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Resolves a path that may be absolute, working-directory-relative, or project-relative (Unity's
        /// working directory is the project root). Throws <see cref="TabularReadException"/> if no file exists.
        /// </summary>
        private static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new TabularReadException("A file path is required.");
            if (File.Exists(path)) return path;

            if (!Path.IsPathRooted(path))
            {
                var combined = Path.Combine(Directory.GetCurrentDirectory(), path);
                if (File.Exists(combined)) return combined;
            }

            throw new TabularReadException($"File not found: '{path}'.");
        }
    }
}
