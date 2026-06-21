using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Molca.Editor
{
    /// <summary>
    /// Loads tabular data from CSV/TSV text or Excel .xlsx (first sheet or by index).
    /// </summary>
    internal static class SpreadsheetTableLoader
    {
        internal enum TextDelimiter
        {
            Comma,
            Tab
        }

        /// <summary>Sheet names in workbook order (empty if not xlsx).</summary>
        public static List<string> LastXlsxSheetNames { get; private set; } = new List<string>();

        public static List<string[]> LoadFromPath(string path, int xlsxSheetIndex, out string errorMessage)
        {
            errorMessage = null;
            LastXlsxSheetNames = new List<string>();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                errorMessage = "File not found.";
                return null;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".xlsx" || ext == ".xlsm")
                    return LoadXlsx(path, xlsxSheetIndex, out errorMessage);

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    var text = reader.ReadToEnd();
                    return LoadFromText(text, ext, out errorMessage);
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        public static List<string[]> LoadFromText(string text, string fileExtension, out string errorMessage)
        {
            errorMessage = null;
            LastXlsxSheetNames = new List<string>();

            if (string.IsNullOrWhiteSpace(text))
            {
                errorMessage = "No text to load.";
                return null;
            }

            var ext = (fileExtension ?? "").ToLowerInvariant();
            TextDelimiter delim;
            if (ext == ".tsv" || ext == ".tab")
                delim = TextDelimiter.Tab;
            else if (ext == ".csv")
                delim = TextDelimiter.Comma;
            else
                delim = DetectDelimiter(text);

            var lines = SplitIntoNonEmptyLines(text);
            if (lines.Count == 0)
            {
                errorMessage = "No non-empty lines.";
                return null;
            }

            var rows = new List<string[]>(lines.Count);
            foreach (var line in lines)
            {
                rows.Add(ParseDelimitedLine(line, delim == TextDelimiter.Tab ? '\t' : ','));
            }

            NormalizeRowWidths(rows);
            return rows;
        }

        private static TextDelimiter DetectDelimiter(string text)
        {
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int tabs = line.Count(c => c == '\t');
                int commas = line.Count(c => c == ',');
                if (tabs > commas) return TextDelimiter.Tab;
                return TextDelimiter.Comma;
            }
            return TextDelimiter.Comma;
        }

        private static List<string> SplitIntoNonEmptyLines(string text)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;
                if (c == '\n')
                {
                    var s = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    sb.Length = 0;
                }
                else sb.Append(c);
            }
            var last = sb.ToString();
            if (!string.IsNullOrWhiteSpace(last)) list.Add(last);
            return list;
        }

        /// <summary>CSV/TSV-style quoted fields.</summary>
        public static string[] ParseDelimitedLine(string line, char delimiter)
        {
            var result = new List<string>();
            bool inQuotes = false;
            int start = 0;

            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (line[i] == delimiter && !inQuotes)
                {
                    result.Add(line.Substring(start, i - start).Trim().Trim('"'));
                    start = i + 1;
                }
            }

            result.Add(line.Substring(start).Trim().Trim('"'));
            return result.ToArray();
        }

        private static void NormalizeRowWidths(List<string[]> rows)
        {
            if (rows.Count == 0) return;
            int max = rows.Max(r => r.Length);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Length >= max) continue;
                var wider = new string[max];
                Array.Copy(rows[i], wider, rows[i].Length);
                rows[i] = wider;
            }
        }

        private static List<string[]> LoadXlsx(string path, int sheetIndex, out string errorMessage)
        {
            errorMessage = null;
            using (var zip = ZipFile.OpenRead(path))
            {
                var shared = ReadSharedStrings(zip);
                if (!TryGetSheetParts(zip, out var sheetNames, out var sheetPaths, out errorMessage))
                    return null;

                LastXlsxSheetNames = sheetNames;
                if (sheetNames.Count == 0)
                {
                    errorMessage = "Workbook has no sheets.";
                    return null;
                }

                int idx = Math.Clamp(sheetIndex, 0, sheetPaths.Count - 1);
                var entry = zip.GetEntry("xl/" + sheetPaths[idx].Replace('\\', '/'));
                if (entry == null)
                {
                    errorMessage = $"Could not read worksheet: xl/{sheetPaths[idx]}";
                    return null;
                }

                using (var stream = entry.Open())
                {
                    var rows = ReadWorksheet(stream, shared);
                    NormalizeRowWidths(rows);
                    return rows;
                }
            }
        }

        private static bool TryGetSheetParts(ZipArchive zip, out List<string> names, out List<string> paths, out string error)
        {
            names = new List<string>();
            paths = new List<string>();
            error = null;

            var wb = zip.GetEntry("xl/workbook.xml");
            var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (wb == null || rels == null)
            {
                error = "Invalid .xlsx: missing workbook or relationships.";
                return false;
            }

            var idToTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var s = rels.Open())
            using (var xr = XmlReader.Create(s))
            {
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element) continue;
                    if (xr.LocalName != "Relationship") continue;
                    var id = xr.GetAttribute("Id") ?? xr.GetAttribute("id");
                    var target = xr.GetAttribute("Target") ?? xr.GetAttribute("target");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                        idToTarget[id] = target.Replace('\\', '/');
                }
            }

            const string relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            using (var s = wb.Open())
            using (var xr = XmlReader.Create(s))
            {
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "sheet") continue;
                    var nm = xr.GetAttribute("name");
                    var rid = xr.GetAttribute("id", relNs);
                    if (string.IsNullOrEmpty(rid))
                        rid = xr.GetAttribute("r:id");

                    if (string.IsNullOrEmpty(rid) || !idToTarget.TryGetValue(rid, out var target))
                        continue;

                    target = target.Replace('\\', '/').TrimStart('/');
                    if (target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                        target = target.Substring(3);

                    names.Add(nm ?? $"Sheet{names.Count + 1}");
                    paths.Add(target);
                }
            }

            return names.Count > 0;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return list;

            using (var stream = entry.Open())
            using (var xr = XmlReader.Create(stream))
            {
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si")
                    {
                        using (var sub = xr.ReadSubtree())
                        {
                            sub.MoveToContent();
                            list.Add(ReadSiText(sub));
                        }
                    }
                }
            }

            return list;
        }

        private static string ReadSiText(XmlReader xr)
        {
            var sb = new StringBuilder();
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                {
                    if (xr.IsEmptyElement) continue;
                    sb.Append(xr.ReadElementContentAsString());
                }
            }
            return sb.ToString();
        }

        private static List<string[]> ReadWorksheet(Stream stream, IReadOnlyList<string> sharedStrings)
        {
            var rowMap = new SortedDictionary<int, SortedDictionary<int, string>>();

            using (var xr = XmlReader.Create(stream))
            {
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "c") continue;

                    var r = xr.GetAttribute("r");
                    if (string.IsNullOrEmpty(r) || !TryParseCellRef(r, out int col0, out int row1))
                        continue;

                    var t = xr.GetAttribute("t");
                    string value = ReadCellValue(xr, t, sharedStrings);

                    if (!rowMap.TryGetValue(row1, out var cols))
                    {
                        cols = new SortedDictionary<int, string>();
                        rowMap[row1] = cols;
                    }
                    cols[col0] = value ?? "";
                }
            }

            if (rowMap.Count == 0)
                return new List<string[]>();

            int maxCol = rowMap.Values.SelectMany(c => c.Keys).DefaultIfEmpty(0).Max();
            int minRow = rowMap.Keys.Min();
            int maxRow = rowMap.Keys.Max();
            var result = new List<string[]>();

            for (int row = minRow; row <= maxRow; row++)
            {
                var cells = new string[maxCol + 1];
                for (int c = 0; c <= maxCol; c++) cells[c] = "";
                if (rowMap.TryGetValue(row, out var colDict))
                {
                    foreach (var kv in colDict)
                        if (kv.Key >= 0 && kv.Key < cells.Length)
                            cells[kv.Key] = kv.Value;
                }
                result.Add(cells);
            }

            return result;
        }

        private static string ReadCellValue(XmlReader xr, string cellType, IReadOnlyList<string> sharedStrings)
        {
            if (xr.IsEmptyElement) return "";

            string vText = null;
            var inlineSb = new StringBuilder();

            using (var sub = xr.ReadSubtree())
            {
                sub.MoveToContent();
                while (sub.Read())
                {
                    if (sub.NodeType != XmlNodeType.Element) continue;
                    if (sub.LocalName == "v" && !sub.IsEmptyElement)
                        vText = sub.ReadElementContentAsString();
                    else if (sub.LocalName == "t" && !sub.IsEmptyElement)
                        inlineSb.Append(sub.ReadElementContentAsString());
                }
            }

            if (cellType == "inlineStr")
                return inlineSb.ToString();

            if (cellType == "s" && vText != null && int.TryParse(vText, out int si) && si >= 0 && si < sharedStrings.Count)
                return sharedStrings[si];

            return vText ?? "";
        }

        private static bool TryParseCellRef(string r, out int col0, out int row1)
        {
            col0 = 0;
            row1 = 0;
            int i = 0;
            while (i < r.Length && char.IsLetter(r[i])) i++;
            if (i == 0 || i >= r.Length) return false;
            var colLetters = r.Substring(0, i);
            if (!int.TryParse(r.Substring(i), out row1)) return false;
            col0 = ColumnLettersToIndex(colLetters);
            return true;
        }

        private static int ColumnLettersToIndex(string letters)
        {
            int n = 0;
            foreach (char c in letters.ToUpperInvariant())
            {
                if (c < 'A' || c > 'Z') return 0;
                n = n * 26 + (c - 'A' + 1);
            }
            return n - 1;
        }
    }
}
