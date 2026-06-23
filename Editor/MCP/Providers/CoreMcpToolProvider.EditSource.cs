using System;
using System.IO;
using System.Text;
using Molca.Editor.KnowledgeGraph;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // ── molca_edit_source ────────────────────────────────────────────────────────────────
        //
        // Completes the MCP file loop: molca_read_source (read) + molca_create_mcp_tool (create) +
        // this (edit). A guarded, reversible in-place edit primitive that reuses the Sprint-17
        // guardrails (allowlist + confirmation, via the Action classification) and the McpUndoStack
        // FileSnapshot machinery (same as the codegen siblings) — no new gating or undo machinery.

        /// <summary>
        /// The <c>molca_edit_source</c> tool: a guarded, reversible in-place editor for a single project
        /// file. Pairs with <see cref="CreateReadSourceTool"/> (read first, then edit). Four discriminated
        /// modes — <c>replace</c> (exact-string), <c>insert</c> (after a line), <c>create</c> (new file),
        /// <c>overwrite</c> (whole file). Confined to the project root and refuses the read-only protected
        /// zones (<c>Packages/</c>, <c>Assets/_MolcaSDK/</c>). Every mutating write is snapshotted to
        /// <see cref="McpUndoStack"/> first, so it is byte-for-byte revertible.
        /// </summary>
        /// <remarks>
        /// <see cref="McpToolReversibility.FileSnapshot"/> Action tool: it ships off by default and is inert
        /// until added to the action allowlist; mutation is gated by allowlist + confirmation through the
        /// standard guard. Editing a <c>.cs</c> file triggers a domain reload (<c>requiresDomainReload</c>).
        /// There is intentionally no <c>delete</c> mode — deletion has no in-place backup to restore.
        /// </remarks>
        private static McpToolDefinition CreateEditSourceTool() => new McpToolDefinition(
            name: "molca_edit_source",
            description: "Edits a single text/source file inside the project, in place and reversibly. "
                       + "Pairs with molca_read_source (read the file first so 'oldString' matches exactly). "
                       + "Required 'path' (project-relative, e.g. 'Assets/MyGame/Scripts/Foo.cs', or an "
                       + "in-project absolute path) and 'mode': "
                       + "'replace' (exact 'oldString'->'newString'; must match exactly once unless "
                       + "'replaceAll':true, otherwise it errors and writes nothing — mirrors a careful "
                       + "find/replace), "
                       + "'insert' ('content' after 1-based 'afterLine'; afterLine 0 = top, afterLine = line "
                       + "count = end-of-file), "
                       + "'create' (new file with 'content'; errors if it already exists), "
                       + "'overwrite' (replace the whole file with 'content'; the file must already exist). "
                       + "No 'delete' mode. Refused for read-only zones (Packages/, Assets/_MolcaSDK/) — "
                       + "subclass or work in your own area instead — and cannot escape the project root. "
                       + "Every change to an existing file is backed up first and is byte-for-byte revertible "
                       + "(returns 'undoId'); editing a .cs file recompiles (requiresDomainReload=true). "
                       + "Action tool: must be on the action allowlist and is confirmed before it writes.",
            inputSchemaJson: EditSourceSchema,
            execute: ExecuteEditSource,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        private const string EditSourceSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"path\":{\"type\":\"string\",\"description\":\"Project-relative or in-project absolute file path.\"}," +
            "\"mode\":{\"type\":\"string\",\"enum\":[\"replace\",\"insert\",\"create\",\"overwrite\"],\"description\":\"Edit operation.\"}," +
            "\"oldString\":{\"type\":\"string\",\"description\":\"replace mode: the exact text to find.\"}," +
            "\"newString\":{\"type\":\"string\",\"description\":\"replace mode: the replacement text (may be empty to delete the match).\"}," +
            "\"replaceAll\":{\"type\":\"boolean\",\"description\":\"replace mode: replace every occurrence instead of requiring a single match.\"}," +
            "\"afterLine\":{\"type\":\"integer\",\"description\":\"insert mode: 1-based line to insert after (0 = beginning, line count = end).\"}," +
            "\"content\":{\"type\":\"string\",\"description\":\"insert/create/overwrite mode: the text to write.\"}}," +
            "\"required\":[\"path\",\"mode\"],\"additionalProperties\":false}";

        private static string ExecuteEditSource(string argumentsJson)
        {
            // Never rewrite source while the editor is (about to be) in Play mode (locked decision b).
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Error("Refusing to edit source during Play mode; exit Play mode and retry.");

            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            var mode = args.Value<string>("mode");

            if (string.IsNullOrWhiteSpace(path))
                return Error("Provide a 'path'.");
            if (string.IsNullOrWhiteSpace(mode))
                return Error("Provide a 'mode' (replace | insert | create | overwrite).");

            // Resolve relative paths against the project root; normalize so '..' cannot escape it
            // (identical containment to ExecuteReadSource).
            string fullPath;
            try
            {
                var combined = Path.IsPathRooted(path) ? path : Path.Combine(GraphifyCli.ProjectRoot, path);
                fullPath = Path.GetFullPath(combined);
            }
            catch (Exception ex)
            {
                return Error($"Invalid path: {ex.Message}");
            }

            var root = Path.GetFullPath(GraphifyCli.ProjectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return Error("Path is outside the project root; refused.");

            // Project-relative, forward-slashed — both for the protected-zone check and for AssetDatabase.
            var projectRelative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (IsProtectedPath(projectRelative))
                return Error($"'{projectRelative}' is in a read-only zone (Packages/ or Assets/_MolcaSDK/) and "
                           + "cannot be edited in place. Subclass or work in your own project area instead.");

            switch (mode)
            {
                case "replace": return EditReplace(args, fullPath, projectRelative);
                case "insert": return EditInsert(args, fullPath, projectRelative);
                case "create": return EditCreate(args, fullPath, projectRelative);
                case "overwrite": return EditOverwrite(args, fullPath, projectRelative);
                default:
                    return Error($"Unknown mode '{mode}'. Use replace | insert | create | overwrite.");
            }
        }

        // ── mode handlers ────────────────────────────────────────────────────────────────────
        // Each validates fully and returns an Error with NO mutation on any failure; only on the happy
        // path does it snapshot (for existing files) and write.

        private static string EditReplace(JObject args, string fullPath, string projectRelative)
        {
            if (!File.Exists(fullPath))
                return Error($"File not found: {fullPath}");

            var oldString = args.Value<string>("oldString");
            var newString = args.Value<string>("newString") ?? string.Empty;
            var replaceAll = args.Value<bool?>("replaceAll") ?? false;

            if (string.IsNullOrEmpty(oldString))
                return Error("replace mode requires a non-empty 'oldString'.");
            if (string.Equals(oldString, newString, StringComparison.Ordinal))
                return Error("'oldString' and 'newString' are identical; nothing to change.");

            string original;
            try { original = File.ReadAllText(fullPath); }
            catch (Exception ex) { return Error($"Could not read file: {ex.Message}"); }

            int count = CountOccurrences(original, oldString);
            if (count == 0)
                return Error("'oldString' was not found in the file; no change made.");
            if (count > 1 && !replaceAll)
                return Error($"'oldString' matches {count} times; pass 'replaceAll':true to replace all, "
                           + "or extend 'oldString' so it matches exactly once. No change made.");

            var updated = replaceAll
                ? original.Replace(oldString, newString)
                : ReplaceFirst(original, oldString, newString);

            return WriteAndRespond(fullPath, projectRelative, updated, "replace",
                description: $"replace in {projectRelative}",
                extra: new JObject { ["replacements"] = replaceAll ? count : 1 });
        }

        private static string EditInsert(JObject args, string fullPath, string projectRelative)
        {
            if (!File.Exists(fullPath))
                return Error($"File not found: {fullPath}");

            var content = args.Value<string>("content");
            if (content == null)
                return Error("insert mode requires 'content'.");
            var afterLineToken = args["afterLine"];
            if (afterLineToken == null)
                return Error("insert mode requires 'afterLine' (0 = beginning, line count = end).");
            int afterLine = afterLineToken.Value<int>();
            if (afterLine < 0)
                return Error("'afterLine' must be >= 0.");

            string original;
            try { original = File.ReadAllText(fullPath); }
            catch (Exception ex) { return Error($"Could not read file: {ex.Message}"); }

            var newline = original.Contains("\r\n") ? "\r\n" : "\n";
            // Splitting on '\n' and trimming a trailing '\r' keeps us newline-style-agnostic.
            var lines = original.Length == 0
                ? Array.Empty<string>()
                : original.Replace("\r\n", "\n").Split('\n');
            if (afterLine > lines.Length)
                return Error($"'afterLine' {afterLine} exceeds the file's {lines.Length} line(s).");

            var sb = new StringBuilder();
            for (int i = 0; i < afterLine; i++)
                sb.Append(lines[i]).Append(newline);
            sb.Append(content);
            if (!content.EndsWith("\n") && !content.EndsWith(newline))
                sb.Append(newline);
            for (int i = afterLine; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append(newline);
            }
            // Preserve a trailing newline if the original had one.
            if (original.EndsWith("\n") && !sb.ToString().EndsWith(newline))
                sb.Append(newline);

            return WriteAndRespond(fullPath, projectRelative, sb.ToString(), "insert",
                description: $"insert after line {afterLine} in {projectRelative}");
        }

        private static string EditCreate(JObject args, string fullPath, string projectRelative)
        {
            if (File.Exists(fullPath))
                return Error($"File already exists: {projectRelative}. Use 'overwrite' to replace it.");

            var content = args.Value<string>("content");
            if (content == null)
                return Error("create mode requires 'content'.");

            try
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex) { return Error($"Could not create directory: {ex.Message}"); }

            // A brand-new file has nothing to snapshot; revert by deleting it (undoId is null).
            return WriteAndRespond(fullPath, projectRelative, content, "create",
                description: $"create {projectRelative}", snapshot: false);
        }

        private static string EditOverwrite(JObject args, string fullPath, string projectRelative)
        {
            if (!File.Exists(fullPath))
                return Error($"File not found: {projectRelative}. Use 'create' to make a new file.");

            var content = args.Value<string>("content");
            if (content == null)
                return Error("overwrite mode requires 'content'.");

            return WriteAndRespond(fullPath, projectRelative, content, "overwrite",
                description: $"overwrite {projectRelative}");
        }

        // ── shared write path ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Snapshots (for existing files), writes <paramref name="content"/>, imports it if it is under
        /// <c>Assets/</c>, and returns the standard result JObject. Centralizes the snapshot→write→import
        /// sequence so every mode reverts identically.
        /// </summary>
        private static string WriteAndRespond(
            string fullPath, string projectRelative, string content, string mode,
            string description, JObject extra = null, bool snapshot = true)
        {
            string undoId = null;
            if (snapshot)
                undoId = McpUndoStack.Snapshot(fullPath, "molca_edit_source", description);

            try { File.WriteAllText(fullPath, content); }
            catch (Exception ex)
            {
                if (undoId != null) McpUndoStack.Discard(undoId);
                return Error($"Write failed: {ex.Message}");
            }

            // AssetDatabase only tracks paths under Assets/ (Packages/ is refused above). Files outside
            // Assets/ (e.g. a repo-root doc) are written but not imported.
            bool underAssets = projectRelative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            if (underAssets)
                AssetDatabase.ImportAsset(projectRelative, ImportAssetOptions.ForceSynchronousImport);

            bool requiresDomainReload = underAssets
                && projectRelative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

            var bytesWritten = Encoding.UTF8.GetByteCount(content);

            var result = new JObject
            {
                ["path"] = fullPath,
                ["mode"] = mode,
                ["applied"] = true,
                ["bytesWritten"] = bytesWritten,
                ["undoId"] = undoId,
                ["requiresDomainReload"] = requiresDomainReload,
                ["note"] = undoId != null
                    ? "Change applied; revert with the undo stack (undoId above)."
                    : "File created; revert by deleting it (no in-place backup for a new file)."
            };
            if (extra != null)
                foreach (var p in extra.Properties())
                    result[p.Name] = p.Value;
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                count++;
                i += needle.Length;
            }
            return count;
        }

        private static string ReplaceFirst(string text, string search, string replacement)
        {
            int idx = text.IndexOf(search, StringComparison.Ordinal);
            return idx < 0 ? text : text.Substring(0, idx) + replacement + text.Substring(idx + search.Length);
        }
    }
}
