using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Molca.Editor
{
    /// <summary>
    /// Generates a new <c>McpToolProvider</c> subclass stub (with one read-only tool) into the project
    /// from a convention-following template, so an assistant can introduce a brand-new MCP tool that does
    /// not yet exist. This is the tooling-side analogue of <see cref="StepScriptGenerationService"/>:
    /// Core MCP tools live in the read-only <c>com.molca.core</c> package, so new tools are added by
    /// scaffolding a fork provider in the working area — never by editing Core (architecture.md layer
    /// model, applied to tooling and mirrored by <see cref="Molca.Editor.Mcp.McpToolProvider"/> docs).
    /// </summary>
    /// <remarks>
    /// Source generation here is GUI- and AssetDatabase-free (pure string building + file write) so it is
    /// unit-testable; the MCP tool layer imports the asset and warns that the new provider type only
    /// becomes usable after the ensuing domain reload — and that the caller must then create the provider
    /// asset and add it to the MCP Settings provider list before its tools are exposed.
    /// </remarks>
    public static class McpToolScriptGenerationService
    {
        /// <summary>Default folder for generated provider stubs (per the framework working-area model).</summary>
        public const string DefaultProviderFolder = "Assets/YourProject/Scripts/Mcp";

        /// <summary>Convention suffix every generated provider class name must carry.</summary>
        public const string ProviderSuffix = "McpToolProvider";

        /// <summary>Outcome of a generation request.</summary>
        public readonly struct GenerationResult
        {
            /// <summary>Project-relative path of the written file, or null on failure.</summary>
            public string Path { get; }

            /// <summary>Failure reason, or null on success.</summary>
            public string Error { get; }

            private GenerationResult(string path, string error) { Path = path; Error = error; }

            internal static GenerationResult Ok(string path) => new GenerationResult(path, null);
            internal static GenerationResult Fail(string error) => new GenerationResult(null, error);
        }

        /// <summary>
        /// Writes a new <c>McpToolProvider</c> subclass named <paramref name="providerClassName"/>, owning
        /// namespace <paramref name="toolNamespace"/> and contributing a single read-only tool named
        /// <paramref name="toolName"/>.
        /// </summary>
        /// <param name="providerClassName">Class name; must be a valid identifier ending in "McpToolProvider".</param>
        /// <param name="toolNamespace">The provider's owned <c>Namespace</c> string (e.g. "molca.vr"); the tool name should be prefixed with it.</param>
        /// <param name="toolName">Fully-qualified name of the first tool (e.g. "molca_vr_status").</param>
        /// <param name="folder">Target project folder; defaults to <see cref="DefaultProviderFolder"/>.</param>
        /// <param name="csharpNamespace">Optional C# namespace to wrap the class in.</param>
        /// <param name="typeExists">Predicate reporting whether a type of this simple name already exists.</param>
        public static GenerationResult CreateProviderScript(
            string providerClassName, string toolNamespace, string toolName, string folder,
            string csharpNamespace, Func<string, bool> typeExists)
        {
            if (!IsValidIdentifier(providerClassName))
                return GenerationResult.Fail($"'{providerClassName}' is not a valid C# type name.");
            if (!providerClassName.EndsWith(ProviderSuffix, StringComparison.Ordinal))
                return GenerationResult.Fail(
                    $"By convention the provider name must end in '{ProviderSuffix}' (e.g. 'Vr{ProviderSuffix}').");
            if (string.IsNullOrWhiteSpace(toolNamespace))
                return GenerationResult.Fail("A provider namespace (e.g. 'molca.vr') is required.");
            if (string.IsNullOrWhiteSpace(toolName))
                return GenerationResult.Fail("A tool name (e.g. 'molca_vr_status') is required.");
            if (typeExists != null && typeExists(providerClassName))
                return GenerationResult.Fail($"A type named '{providerClassName}' already exists.");

            folder = string.IsNullOrWhiteSpace(folder) ? DefaultProviderFolder : folder;
            var path = $"{folder.TrimEnd('/')}/{providerClassName}.cs";
            if (File.Exists(path))
                return GenerationResult.Fail($"A file already exists at '{path}'.");

            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, BuildProviderScript(providerClassName, toolNamespace, toolName, csharpNamespace));
            }
            catch (Exception e)
            {
                return GenerationResult.Fail($"Could not write '{path}': {e.Message}");
            }
            return GenerationResult.Ok(path);
        }

        /// <summary>Builds the source for a new <c>McpToolProvider</c> subclass with one read-only tool stub.</summary>
        public static string BuildProviderScript(
            string providerClassName, string toolNamespace, string toolName, string csharpNamespace)
        {
            var method = ToPascalCase(toolName);
            var menuName = $"Molca/Editor/MCP/{providerClassName}";

            var body = new StringBuilder()
                .Append("/// <summary>\n")
                .Append($"/// TODO: describe the tools {providerClassName} contributes. Register this asset in the\n")
                .Append("/// MCP Settings provider list (Action tools also need the action allowlist).\n")
                .Append("/// </summary>\n")
                .Append($"[CreateAssetMenu(fileName = \"{providerClassName}\", menuName = \"{menuName}\")]\n")
                .Append($"public partial class {providerClassName} : McpToolProvider\n")
                .Append("{\n")
                .Append("    /// <inheritdoc/>\n")
                .Append($"    public override string Namespace => \"{toolNamespace}\";\n\n")
                .Append("    // Tools are discovered by convention from the Create*Tool() factories on this type\n")
                .Append("    // (and its partial files) — no GetTools() override needed. Add another tool by\n")
                .Append("    // declaring a new Create<Tool>Tool() factory, here or in a partial file.\n\n")
                .Append($"    private static McpToolDefinition Create{method}Tool() => new McpToolDefinition(\n")
                .Append($"        name: \"{toolName}\",\n")
                .Append($"        description: \"TODO: describe what {toolName} does.\",\n")
                .Append("        inputSchemaJson: \"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{}}\",\n")
                .Append($"        execute: Execute{method},\n")
                .Append("        mode: McpToolMode.Any,\n")
                .Append("        kind: McpToolKind.ReadOnly);\n\n")
                .Append("    // For an Action (mutating) tool, set kind: McpToolKind.Action plus the matching\n")
                .Append("    // McpToolReversibility, add it to the MCP Settings action allowlist, and gate as needed.\n")
                .Append($"    private static string Execute{method}(string argumentsJson)\n")
                .Append("    {\n")
                .Append("        // TODO: implement. Parse argumentsJson (JSON) and return a JSON string result.\n")
                .Append("        var args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson);\n")
                .Append("        return new JObject { [\"ok\"] = true }.ToString(Newtonsoft.Json.Formatting.None);\n")
                .Append("    }\n")
                .Append("}\n")
                .ToString();

            const string usings =
                "using Molca.Editor.Mcp;\n" +
                "using Newtonsoft.Json.Linq;\n" +
                "using UnityEngine;\n";

            return Wrap(usings, csharpNamespace, body);
        }

        /// <summary>
        /// Writes a tool-only <b>partial</b> file that adds one read-only tool factory
        /// (<c>Create&lt;Tool&gt;Tool()</c> + <c>Execute&lt;Tool&gt;</c>) to an <i>existing</i>
        /// <c>McpToolProvider</c> subclass named <paramref name="providerClassName"/>. The new tool is
        /// surfaced automatically by convention discovery (Sprint 34) — no <c>GetTools()</c> edit. The
        /// file is named <c>&lt;Provider&gt;.&lt;Method&gt;.cs</c> so a duplicate tool produces a path
        /// collision that is rejected here.
        /// </summary>
        /// <param name="providerClassName">The existing provider type's simple name (validated, must end in the convention suffix).</param>
        /// <param name="toolName">Fully-qualified name of the tool to add (e.g. "molca_vr_status").</param>
        /// <param name="folder">Folder the existing provider lives in (the caller resolves it from the type's script path).</param>
        /// <param name="csharpNamespace">The existing provider's C# namespace, or null for the global namespace — must match the original so the partials combine.</param>
        public static GenerationResult CreateToolPartial(
            string providerClassName, string toolName, string folder, string csharpNamespace)
        {
            if (!IsValidIdentifier(providerClassName))
                return GenerationResult.Fail($"'{providerClassName}' is not a valid C# type name.");
            if (string.IsNullOrWhiteSpace(toolName))
                return GenerationResult.Fail("A tool name (e.g. 'molca_vr_status') is required.");
            if (string.IsNullOrWhiteSpace(folder))
                return GenerationResult.Fail("The existing provider's folder could not be determined.");

            var method = ToPascalCase(toolName);
            var path = $"{folder.TrimEnd('/')}/{providerClassName}.{method}.cs";
            if (File.Exists(path))
                return GenerationResult.Fail(
                    $"A file already exists at '{path}' — a tool factory 'Create{method}Tool' is likely already defined.");

            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, BuildToolPartial(providerClassName, toolName, csharpNamespace));
            }
            catch (Exception e)
            {
                return GenerationResult.Fail($"Could not write '{path}': {e.Message}");
            }
            return GenerationResult.Ok(path);
        }

        /// <summary>Builds the source for a tool-only partial adding one read-only tool to an existing provider.</summary>
        public static string BuildToolPartial(string providerClassName, string toolName, string csharpNamespace)
        {
            var method = ToPascalCase(toolName);

            var body = new StringBuilder()
                .Append($"public partial class {providerClassName}\n")
                .Append("{\n")
                .Append($"    private static McpToolDefinition Create{method}Tool() => new McpToolDefinition(\n")
                .Append($"        name: \"{toolName}\",\n")
                .Append($"        description: \"TODO: describe what {toolName} does.\",\n")
                .Append("        inputSchemaJson: \"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{}}\",\n")
                .Append($"        execute: Execute{method},\n")
                .Append("        mode: McpToolMode.Any,\n")
                .Append("        kind: McpToolKind.ReadOnly);\n\n")
                .Append("    // For an Action (mutating) tool, set kind: McpToolKind.Action plus the matching\n")
                .Append("    // McpToolReversibility, add it to the MCP Settings action allowlist, and gate as needed.\n")
                .Append($"    private static string Execute{method}(string argumentsJson)\n")
                .Append("    {\n")
                .Append("        // TODO: implement. Parse argumentsJson (JSON) and return a JSON string result.\n")
                .Append("        var args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson);\n")
                .Append("        return new JObject { [\"ok\"] = true }.ToString(Newtonsoft.Json.Formatting.None);\n")
                .Append("    }\n")
                .Append("}\n")
                .ToString();

            const string usings =
                "using Molca.Editor.Mcp;\n" +
                "using Newtonsoft.Json.Linq;\n";

            return Wrap(usings, csharpNamespace, body);
        }

        private static string Wrap(string usings, string namespaceName, string body)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
                return usings + "\n" + body;

            var indented = "    " + body.TrimEnd('\n').Replace("\n", "\n    ");
            return usings + "\n" + $"namespace {namespaceName}\n{{\n" + indented + "\n}\n";
        }

        /// <summary>Converts a tool name like "molca_vr_status" into a method-safe "MolcaVrStatus".</summary>
        private static string ToPascalCase(string toolName)
        {
            var parts = Regex.Split(toolName ?? string.Empty, @"[^A-Za-z0-9]+");
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1) sb.Append(part.Substring(1));
            }
            var result = sb.ToString();
            // Guarantee a valid identifier start even for names beginning with a digit.
            if (result.Length == 0 || char.IsDigit(result[0])) result = "Tool" + result;
            return result;
        }

        private static bool IsValidIdentifier(string name) =>
            !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");
    }
}
