using System.IO;
using System.Linq;
using Molca.Editor.Tabular;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_sheet_read</c> tool: parses a tabular file (CSV/TSV and Excel .xlsx/.xlsm) into
        /// columns + rows so the assistant can understand a sheet's structure. Read-only. Rows are capped by
        /// <c>maxRows</c> and truncation is always reported, so a large sheet never silently floods the
        /// context. When no reader handles the extension the tool returns a helpful error listing the formats
        /// that <em>are</em> available, never a transport failure.
        /// </summary>
        private static McpToolDefinition CreateSheetReadTool() => new McpToolDefinition(
            name: "molca_sheet_read",
            description: "Reads a spreadsheet/CSV file into columns and rows so you can understand its "
                       + "contents. Supports .csv, .tsv, and Excel .xlsx/.xlsm. Use 'sheet' to pick a "
                       + "worksheet by name (Excel). Rows are capped by maxRows (default 200) and truncation "
                       + "is reported. Read-only — to apply sheet values onto scene objects or assets, use "
                       + "molca_data_apply_plan then molca_data_apply.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Path to the file (absolute or project-relative).\"}," +
                "\"sheet\":{\"type\":\"string\",\"description\":\"Sheet name for multi-sheet formats (XLSX). Omit for the first sheet; ignored for CSV.\"}," +
                "\"hasHeader\":{\"type\":\"boolean\",\"description\":\"Treat the first row as column names (default true). If false, columns are named A, B, C, …\"}," +
                "\"maxRows\":{\"type\":\"integer\",\"description\":\"Maximum data rows to return (default 200; 0 = no cap).\"}," +
                "\"columns\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Optional subset of column names to return, in order.\"}" +
                "},\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteSheetRead,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteSheetRead(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var path = (string)args["path"];
            if (string.IsNullOrWhiteSpace(path))
                return Error("'path' is required.");

            if (!TabularReaderRegistry.TryGetReader(path, out var reader))
            {
                var ext = Path.GetExtension(path);
                return new JObject
                {
                    ["error"] = string.IsNullOrEmpty(ext)
                        ? "No file extension on 'path'; cannot pick a reader."
                        : $"No reader registered for '{ext.ToLowerInvariant()}'. "
                          + "Convert the file to one of the supported formats.",
                    ["supported"] = new JArray(TabularReaderRegistry.SupportedExtensions.Cast<object>().ToArray())
                }.ToString(Newtonsoft.Json.Formatting.None);
            }

            var options = new TabularReadOptions
            {
                Sheet = (string)args["sheet"],
                HasHeader = args["hasHeader"] == null || (bool)args["hasHeader"],
                MaxRows = args["maxRows"] != null ? (int)args["maxRows"] : 200
            };

            TabularDocument doc;
            try
            {
                doc = reader.Read(path, options);
            }
            catch (TabularReadException ex)
            {
                return Error(ex.Message);
            }
            catch (System.Exception ex)
            {
                return Error($"Failed to read '{path}': {ex.Message}");
            }

            // Optional column projection: keep only the requested columns (by name, case-insensitive),
            // preserving the requested order. Unmatched requests are reported so the model can correct.
            var requested = (args["columns"] as JArray)?.Select(t => (string)t).Where(s => !string.IsNullOrEmpty(s)).ToList();
            int[] projection;
            var missing = new JArray();
            if (requested != null && requested.Count > 0)
            {
                var indices = new System.Collections.Generic.List<int>();
                foreach (var name in requested)
                {
                    int idx = doc.IndexOfColumn(name);
                    if (idx < 0) missing.Add(name);
                    else indices.Add(idx);
                }
                projection = indices.ToArray();
            }
            else
            {
                projection = Enumerable.Range(0, doc.Columns.Count).ToArray();
            }

            var columnsArr = new JArray();
            foreach (var i in projection)
                columnsArr.Add(doc.Columns[i].Name);

            var rowsArr = new JArray();
            foreach (var row in doc.Rows)
            {
                var cells = new JArray();
                foreach (var i in projection)
                    cells.Add(i < row.Count ? row[i] : string.Empty);
                rowsArr.Add(cells);
            }

            var result = new JObject
            {
                ["sheet"] = doc.SheetName,
                ["sheetNames"] = new JArray(doc.SheetNames.Cast<object>().ToArray()),
                ["columns"] = columnsArr,
                ["rows"] = rowsArr,
                ["rowCount"] = doc.Rows.Count,
                ["totalRowCount"] = doc.TotalRowCount,
                ["truncated"] = doc.Truncated
            };
            if (missing.Count > 0) result["missingColumns"] = missing;
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
