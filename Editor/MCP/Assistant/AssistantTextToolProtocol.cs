using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Result of parsing a text/XML tool call out of an assistant message.
    /// </summary>
    internal readonly struct TextToolCallParseResult
    {
        /// <summary>The assistant text after the parsed tool-call block has been removed.</summary>
        public string VisibleText { get; }

        /// <summary>The parsed tool call, or <c>null</c> when no complete known call was found.</summary>
        public LlmToolCall ToolCall { get; }

        /// <summary>True when <see cref="ToolCall"/> contains a parsed call.</summary>
        public bool HasToolCall => ToolCall != null;

        /// <summary>Creates a parsed text-tool result.</summary>
        /// <param name="visibleText">Assistant text safe to show in the transcript.</param>
        /// <param name="toolCall">Parsed tool call, or <c>null</c>.</param>
        public TextToolCallParseResult(string visibleText, LlmToolCall toolCall)
        {
            VisibleText = visibleText ?? string.Empty;
            ToolCall = toolCall;
        }
    }

    /// <summary>
    /// Sprint-69 text/XML tool protocol for local models: renders flat tool specs into the prompt, parses
    /// one XML call from assistant text, and encodes tool results as normal user-role text.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/MCP/Assistant/</c>. Base class: static helper.
    /// Registration: <see cref="AssistantChatController"/> uses it when
    /// <see cref="AssistantSettings.UseTextToolProtocol"/> is true.
    /// </remarks>
    internal static class AssistantTextToolProtocol
    {
        private const int MaxTurnReminderTools = 8;

        private static readonly Regex ToolBlockRegex = new Regex(
            @"<(?<name>[A-Za-z_][A-Za-z0-9_\-.]*)\b[^>]*>(?<body>[\s\S]*?)</\k<name>>",
            RegexOptions.CultureInvariant);

        private static readonly Regex SelfClosingToolRegex = new Regex(
            @"<(?<name>[A-Za-z_][A-Za-z0-9_\-.]*)\b[^>]*/>",
            RegexOptions.CultureInvariant);

        private static readonly Regex NumberRegex = new Regex(
            @"^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$",
            RegexOptions.CultureInvariant);

        private static readonly Regex WordRegex = new Regex(
            @"[A-Za-z0-9]+",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Builds the system prompt for text-tool transport by appending the XML grammar, ReAct rules, and a
        /// compact flat listing of available tools.
        /// </summary>
        /// <param name="basePrompt">The normal assistant system prompt.</param>
        /// <param name="tools">Flat tool specs exposed to this text transport.</param>
        /// <returns>The prompt sent to the model for text-tool rounds.</returns>
        internal static string BuildSystemPrompt(string basePrompt, IReadOnlyList<LlmToolSpec> tools)
        {
            var sb = new StringBuilder(basePrompt ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## Text Tool Protocol");
            sb.AppendLine("Tools are called in plain assistant text. When you need a tool, write exactly one XML block and no final answer in that message:");
            sb.AppendLine("<tool_name>");
            sb.AppendLine("  <param_name>value</param_name>");
            sb.AppendLine("</tool_name>");
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- Call at most one tool per assistant message.");
            sb.AppendLine("- Use the exact tool name and parameter tag names. Each request includes the exact parameters and an XML template for the tools relevant to it — follow those.");
            sb.AppendLine("- For object or array parameters, put compact JSON inside that parameter tag.");
            sb.AppendLine("- For tools with no parameters, write <tool_name></tool_name>.");
            sb.AppendLine("- Never put a placeholder or example value (like [target], 'Your Path Here', or a guessed name) in a call. If you don't know a real value such as a GameObject's hierarchy path or instance id, first call a discovery tool (e.g. molca_unity_scene_objects) to find it, then call the action with the real value.");
            sb.AppendLine("- When the user refers to something you just created or changed (\"it\", \"the sphere you made\"), reuse the exact path or instance id from the previous tool result — do not guess a different name.");
            sb.AppendLine("- Tool results come back as user messages beginning with [tool: name]. After a tool returns a result, tell the user the outcome. Never claim a tool is unavailable after it has returned a result.");
            AppendRoutingHints(sb, tools);
            sb.AppendLine();
            sb.AppendLine("Available tools (name: purpose — parameters are provided with each request):");

            if (tools == null || tools.Count == 0)
            {
                sb.AppendLine("- none");
                return sb.ToString();
            }

            // Names + one-line purpose only (Sprint 69.8): the per-request reminder carries the exact params +
            // XML template for the relevant subset, so dumping every tool's full param spec here is redundant
            // noise that a small model wades through on every turn.
            foreach (var tool in tools.Where(t => t != null).OrderBy(t => t.Name, StringComparer.Ordinal))
                sb.Append("- ").Append(tool.Name).Append(": ").Append(OneLine(tool.Description)).AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// Builds a short, request-local reminder for common Unity actions that weak local models often
        /// overlook in the longer system tool list. The controller appends this to the current user message
        /// copy only; it is not persisted in chat history.
        /// </summary>
        /// <param name="userText">The current user request.</param>
        /// <param name="tools">The flat text-mode tool set available for this request.</param>
        /// <returns>A reminder block, or an empty string when no relevant route is detected.</returns>
        internal static string BuildTurnToolReminder(string userText, IReadOnlyList<LlmToolSpec> tools)
        {
            var toolMap = BuildToolMap(tools);
            if (toolMap.Count == 0 || string.IsNullOrWhiteSpace(userText)) return string.Empty;

            var candidates = RankRelevantTools(userText, toolMap, MaxTurnReminderTools);
            if (candidates.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[Relevant available tools]");
            sb.AppendLine("These are the best available tool matches for the current request. If one matches the user's intent, call it with XML; do not claim the capability is unavailable.");
            for (var i = 0; i < candidates.Count; i++)
            {
                var tool = candidates[i].Tool;
                sb.Append(i + 1).Append(". ").Append(tool.Name).Append(": ").Append(OneLine(tool.Description)).AppendLine();
                sb.Append("   params: ").AppendLine(RenderParameterSpec(tool.InputSchemaJson));
                sb.Append("   xml: ").AppendLine(RenderXmlTemplate(tool, userText));
            }
            var lowered = userText.ToLowerInvariant();
            if (ContainsAny(lowered, "delete", "remove", "destroy")
                && candidates.Any(c => ContainsAny(c.Tool.Name.ToLowerInvariant(), "delete", "remove", "destroy")))
                sb.AppendLine("For deletion/removal requests, call the matching delete/remove tool directly; selection/navigation tools are not deletion.");

            return "[Available tool reminder]\n"
                 + sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Appends a request-local text-tool reminder to the current user message when a relevant available
        /// tool can be inferred from the request.
        /// </summary>
        /// <param name="userText">The current user request.</param>
        /// <param name="tools">The flat text-mode tool set available for this request.</param>
        /// <returns>The original text plus a reminder block when relevant.</returns>
        internal static string AppendTurnToolReminder(string userText, IReadOnlyList<LlmToolSpec> tools)
        {
            var reminder = BuildTurnToolReminder(userText, tools);
            return string.IsNullOrWhiteSpace(reminder)
                ? userText ?? string.Empty
                : (userText ?? string.Empty).TrimEnd() + "\n\n" + reminder;
        }

        private static void AppendRoutingHints(StringBuilder sb, IReadOnlyList<LlmToolSpec> tools)
        {
            var names = new HashSet<string>(
                (tools ?? Array.Empty<LlmToolSpec>())
                    .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Name))
                    .Select(t => t.Name),
                StringComparer.Ordinal);
            if (names.Count == 0) return;

            var emitted = false;
            void EmitHeader()
            {
                if (emitted) return;
                sb.AppendLine();
                sb.AppendLine("Common Unity tool routes:");
                sb.AppendLine("- If one of these routes matches the user's request, call the named tool instead of saying the capability is missing.");
                emitted = true;
            }

            if (names.Contains("molca_unity_gameobject_create"))
            {
                EmitHeader();
                sb.AppendLine("- Add/create a primitive sphere, cube, capsule, cylinder, plane, or quad in the active scene: use molca_unity_gameobject_create.");
                sb.AppendLine("  Example: <molca_unity_gameobject_create><name>Sphere</name><primitive>Sphere</primitive></molca_unity_gameobject_create>");
            }

            if (names.Contains("molca_unity_gameobject_set_transform"))
            {
                EmitHeader();
                sb.AppendLine("- Move, rotate, or scale a GameObject after resolving its path or instance id: use molca_unity_gameobject_set_transform.");
                sb.AppendLine("  Example: <molca_unity_gameobject_set_transform><target>Sphere</target><position>[0,5,0]</position></molca_unity_gameobject_set_transform>");
            }

            if (names.Contains("molca_unity_gameobject_delete"))
            {
                EmitHeader();
                sb.AppendLine("- Delete/remove/destroy a GameObject by path or instance id: use molca_unity_gameobject_delete, not molca_unity_select.");
                sb.AppendLine("  Example: <molca_unity_gameobject_delete><target>Sphere</target></molca_unity_gameobject_delete>");
            }

            if (names.Contains("molca_unity_prefab_instantiate"))
            {
                EmitHeader();
                sb.AppendLine("- Place a prefab/model asset in the active scene: use molca_unity_prefab_instantiate.");
            }

            if (names.Contains("molca_unity_gameobject_add_component"))
            {
                EmitHeader();
                sb.AppendLine("- Add a Component to an existing GameObject: use molca_unity_gameobject_add_component.");
            }
        }

        private static IReadOnlyList<ToolCandidate> RankRelevantTools(
            string userText, IReadOnlyDictionary<string, LlmToolSpec> toolMap, int limit)
        {
            var lowered = userText.ToLowerInvariant();
            var queryTerms = BuildExpandedSearchTerms(lowered);
            var candidates = new List<ToolCandidate>();
            foreach (var tool in toolMap.Values)
            {
                var searchable = BuildSearchableToolText(tool);
                var nameText = NormalizeIdentifier(tool.Name);
                var mentioned = userText.IndexOf(tool.Name, StringComparison.OrdinalIgnoreCase) >= 0;
                var score = mentioned ? 10000 : 0;
                foreach (var term in queryTerms)
                {
                    if (term.Length == 0) continue;
                    if (nameText.IndexOf(term, StringComparison.Ordinal) >= 0) score += 24;
                    if (searchable.IndexOf(term, StringComparison.Ordinal) >= 0) score += 8;
                    if (ToolParameterNames(tool.InputSchemaJson).Any(p => p.IndexOf(term, StringComparison.Ordinal) >= 0))
                        score += 12;
                }

                if (score > 0) candidates.Add(new ToolCandidate(tool, score, mentioned));
            }

            if (candidates.Count == 0) return Array.Empty<ToolCandidate>();

            candidates = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Tool.Name, StringComparer.Ordinal)
                .ToList();

            var best = candidates[0].Score;
            var threshold = Math.Max(24, best / 3);
            return candidates
                .Where(c => c.Mentioned || c.Score >= threshold)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private static HashSet<string> BuildExpandedSearchTerms(string lowered)
        {
            var terms = ExtractTerms(lowered);
            if (ContainsAny(lowered, "move", "position", "translate", "transform", "up", "down", "left", "right", "forward", "back"))
                AddTerms(terms, "set", "transform", "position", "local", "gameobject");
            if (ContainsAny(lowered, "rotate", "rotation", "euler"))
                AddTerms(terms, "set", "transform", "rotation", "euler", "gameobject");
            if (ContainsAny(lowered, "scale", "resize", "size"))
                AddTerms(terms, "set", "transform", "scale", "gameobject");
            if (ContainsAny(lowered, "delete", "remove", "destroy"))
                AddTerms(terms, "delete", "remove", "destroy", "gameobject", "target");
            if (ContainsAny(lowered, "add", "create", "make", "spawn", "place", "instantiate"))
                AddTerms(terms, "add", "create", "instantiate", "gameobject", "prefab", "asset");
            if (ContainsAny(lowered, "find", "list", "show", "inspect", "what", "where"))
                AddTerms(terms, "list", "inspect", "scene", "objects", "selection", "components", "fields", "assets");
            if (ContainsAny(lowered, "color", "material", "shader", "renderer", "render"))
                AddTerms(terms, "renderer", "material", "color", "property");
            if (ContainsAny(lowered, "rigidbody", "physics", "collider", "mass", "gravity"))
                AddTerms(terms, "rigidbody", "collider", "physics");
            if (ContainsAny(lowered, "camera", "light", "lighting"))
                AddTerms(terms, "camera", "light", "render");
            if (ContainsAny(lowered, "ui", "canvas", "rect", "panel", "button", "uidocument", "uitk"))
                AddTerms(terms, "ui", "uitk", "canvas", "rect", "panel", "document");
            if (ContainsAny(lowered, "scene"))
                AddTerms(terms, "scene", "scenes", "active", "save", "open");
            if (ContainsAny(lowered, "build"))
                AddTerms(terms, "build", "target", "scenes");
            if (ContainsAny(lowered, "addressable", "addressables"))
                AddTerms(terms, "addressable", "address", "label", "group");
            if (ContainsAny(lowered, "sequence", "step", "auxiliary"))
                AddTerms(terms, "sequence", "step", "auxiliary");
            if (ContainsAny(lowered, "network", "http", "request", "api"))
                AddTerms(terms, "network", "http", "request");
            if (ContainsAny(lowered, "localization", "language", "text"))
                AddTerms(terms, "localization", "language", "text");
            return terms;
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (var needle in needles)
                if (text.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        private static HashSet<string> ExtractTerms(string text)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in WordRegex.Matches(text ?? string.Empty))
            {
                var term = match.Value.ToLowerInvariant();
                if (term.Length < 3 && term != "x" && term != "y" && term != "z") continue;
                result.Add(term);
                var stem = Stem(term);
                if (stem.Length > 0) result.Add(stem);
            }
            return result;
        }

        private static void AddTerms(HashSet<string> terms, params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                terms.Add(value);
                var stem = Stem(value);
                if (stem.Length > 0) terms.Add(stem);
            }
        }

        private static string Stem(string term)
        {
            if (string.IsNullOrEmpty(term) || term.Length < 5) return term ?? string.Empty;
            if (term.EndsWith("ing", StringComparison.Ordinal)) return term.Substring(0, term.Length - 3);
            if (term.EndsWith("ed", StringComparison.Ordinal)) return term.Substring(0, term.Length - 2);
            if (term.EndsWith("es", StringComparison.Ordinal)) return term.Substring(0, term.Length - 2);
            if (term.EndsWith("s", StringComparison.Ordinal)) return term.Substring(0, term.Length - 1);
            return term;
        }

        private static string BuildSearchableToolText(LlmToolSpec tool)
        {
            var sb = new StringBuilder();
            sb.Append(NormalizeIdentifier(tool.Name)).Append(' ');
            sb.Append(NormalizeIdentifier(tool.Description)).Append(' ');
            foreach (var parameter in ReadParameterInfos(tool.InputSchemaJson))
            {
                sb.Append(NormalizeIdentifier(parameter.Name)).Append(' ');
                sb.Append(parameter.Type).Append(' ');
            }
            return sb.ToString().ToLowerInvariant();
        }

        private static IEnumerable<string> ToolParameterNames(string schemaJson)
            => ReadParameterInfos(schemaJson).Select(p => p.Name.ToLowerInvariant());

        private static string NormalizeIdentifier(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var spaced = Regex.Replace(text, @"([a-z0-9])([A-Z])", "$1 $2", RegexOptions.CultureInvariant);
            spaced = spaced.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
            return spaced.ToLowerInvariant();
        }

        /// <summary>
        /// Parses the first complete known XML tool-call block from <paramref name="text"/>.
        /// </summary>
        /// <param name="text">Raw assistant text from the model.</param>
        /// <param name="tools">Tools valid for this round; unknown XML tags are ignored.</param>
        /// <param name="callSequence">Stable per-turn sequence used for the synthetic call id.</param>
        /// <returns>Visible text plus the parsed call when one was found.</returns>
        internal static TextToolCallParseResult ParseToolCall(
            string text, IReadOnlyList<LlmToolSpec> tools, int callSequence)
        {
            text ??= string.Empty;
            var toolMap = BuildToolMap(tools);
            if (toolMap.Count == 0) return new TextToolCallParseResult(NormalizeVisibleText(text), null);

            foreach (Match match in ToolBlockRegex.Matches(text))
            {
                var name = match.Groups["name"].Value;
                if (!toolMap.TryGetValue(name, out var spec)) continue;

                var args = ParseArguments(match.Groups["body"].Value, spec.InputSchemaJson);
                var visible = StripKnownToolBlocks(text, toolMap);
                var id = "text_tool_" + Math.Max(1, callSequence).ToString(CultureInfo.InvariantCulture);
                return new TextToolCallParseResult(visible, new LlmToolCall(id, name, args));
            }

            foreach (Match match in SelfClosingToolRegex.Matches(text))
            {
                var name = match.Groups["name"].Value;
                if (!toolMap.ContainsKey(name)) continue;

                var visible = StripKnownToolBlocks(text, toolMap);
                var id = "text_tool_" + Math.Max(1, callSequence).ToString(CultureInfo.InvariantCulture);
                return new TextToolCallParseResult(visible, new LlmToolCall(id, name, "{}"));
            }

            return new TextToolCallParseResult(NormalizeVisibleText(text), null);
        }

        /// <summary>
        /// Builds a user-role text message carrying a tool result for the text protocol.
        /// </summary>
        /// <param name="call">The tool call being answered.</param>
        /// <param name="result">The tool result to return to the model.</param>
        /// <returns>A user-role text message with no structured tool-result payload.</returns>
        internal static LlmMessage BuildToolResultMessage(LlmToolCall call, LlmToolResult result)
            => LlmMessage.UserText(FormatToolResult(call, result));

        /// <summary>
        /// Formats one tool result as plain text for text-tool transport.
        /// </summary>
        /// <param name="call">The call being answered.</param>
        /// <param name="result">The result payload.</param>
        /// <returns>Plain text suitable for a user-role message.</returns>
        internal static string FormatToolResult(LlmToolCall call, LlmToolResult result)
        {
            var name = call?.Name ?? "unknown";
            var content = result?.Content ?? string.Empty;
            // Label the outcome explicitly (Sprint 69.8): weak local models otherwise read a bare "result:" as
            // ambiguous and have been observed declaring a tool "unavailable" right after it succeeded. A clear
            // SUCCEEDED/FAILED token plus a "report it" nudge keeps them from confabulating failure.
            if (result != null && result.IsError)
                return $"[tool: {name}] FAILED:\n{content}";
            return $"[tool: {name}] SUCCEEDED — the action is done. Tell the user this outcome; do not say the " +
                   $"tool is unavailable.\nResult:\n{content}";
        }

        /// <summary>The marker rendered for an unresolved required identifier, also treated as a placeholder.</summary>
        internal const string ReplaceMarker = "REPLACE_WITH_REAL_VALUE";

        /// <summary>Exact (case-insensitive) argument values that are example/template tokens, never real input.</summary>
        private static readonly HashSet<string> PlaceholderTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ReplaceMarker, "componenttype", "assets/path", "[x,y,z]", "todo", "tbd", "example",
        };

        /// <summary>
        /// Detects when a text-parsed tool call carries a placeholder/example argument the model invented
        /// instead of a real value (e.g. <c>[Your Target Path Here]</c>) (Sprint 69.8). Returns a corrective
        /// error-result JSON the controller feeds back instead of running the doomed call — steering the model
        /// to resolve the value (typically via a discovery tool) and retry — or <c>null</c> when the arguments
        /// look concrete.
        /// </summary>
        /// <param name="call">The text-parsed tool call to inspect.</param>
        /// <returns>Corrective error JSON, or <c>null</c> when no placeholder argument is found.</returns>
        internal static string DetectPlaceholderArguments(LlmToolCall call)
        {
            if (call == null || string.IsNullOrWhiteSpace(call.ArgumentsJson)) return null;
            JObject args;
            try { args = JObject.Parse(call.ArgumentsJson); }
            catch { return null; }

            foreach (var property in args.Properties())
            {
                if (!ValueLooksLikePlaceholder(property.Value)) continue;
                var shown = OneLine(property.Value.ToString(Newtonsoft.Json.Formatting.None));
                return new JObject
                {
                    ["error"] =
                        $"The '{property.Name}' argument is a placeholder/example value (\"{shown}\"), not a real " +
                        "value, so the call was not run. Do not guess or copy example text. If you don't know the " +
                        "real value (e.g. a GameObject's hierarchy path or instance id), first call a discovery " +
                        "tool such as molca_unity_scene_objects to find it, then retry this call with the real value."
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            return null;
        }

        /// <summary>
        /// Detects a required string argument that is missing or empty/whitespace-only before an Action tool
        /// runs (Sprint 72). Unlike <see cref="DetectPlaceholderArguments"/> (a Text-transport phrase check),
        /// this reads the tool's JSON-schema <c>required</c> list and works on <b>both</b> transports — the
        /// blank <c>targets:""</c> that reached FunctionCalling is exactly the gap it closes. Returns a
        /// corrective error-result JSON steering the model to discover the real value, or <c>null</c> when
        /// every required string argument is present and non-blank.
        /// </summary>
        /// <param name="inputSchemaJson">The tool's input JSON schema (properties + required array).</param>
        /// <param name="argumentsJson">The model-supplied arguments JSON for the call.</param>
        /// <returns>Corrective error JSON, or <c>null</c> when no required string argument is blank.</returns>
        internal static string DetectBlankRequiredArgument(string inputSchemaJson, string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(inputSchemaJson)) return null;
            JObject schema;
            try { schema = JObject.Parse(inputSchemaJson); }
            catch { return null; }

            if (schema["required"] is not JArray required || required.Count == 0) return null;
            var properties = schema["properties"] as JObject;

            JObject args = null;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
                try { args = JObject.Parse(argumentsJson); } catch { args = null; }

            foreach (var req in required)
            {
                var name = (string)req;
                if (string.IsNullOrEmpty(name)) continue;

                // Only guard required *string* arguments; a missing object/array/number is a different error
                // the tool itself reports, and an empty string is the specific "doomed call" case we saw.
                var propType = (string)properties?[name]?["type"];
                if (!string.Equals(propType, "string", StringComparison.Ordinal)) continue;

                var value = args?[name];
                var blank = value == null
                    || value.Type == JTokenType.Null
                    || (value.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)value));
                if (!blank) continue;

                return new JObject
                {
                    ["error"] =
                        $"The required '{name}' argument was empty, so the action was not run. Do not call an " +
                        "action with a blank target. First find the real value with a discovery tool (e.g. " +
                        "molca_unity_scene_objects, optionally filtered by nameContains/componentType), then " +
                        $"retry this call with a concrete '{name}'."
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            return null;
        }

        private static bool ValueLooksLikePlaceholder(JToken value)
        {
            if (value == null) return false;
            if (value.Type == JTokenType.String) return StringLooksLikePlaceholder(value.Value<string>());
            if (value.Type == JTokenType.Array) return value.Children().Any(ValueLooksLikePlaceholder);
            return false;
        }

        private static bool StringLooksLikePlaceholder(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var trimmed = value.Trim();
            if (PlaceholderTokens.Contains(trimmed)) return true;
            var lower = trimmed.ToLowerInvariant();
            if (lower.Contains("your ") && lower.Contains("here")) return true;       // "[Your Target Path Here]"
            if (lower.StartsWith("replace_with", StringComparison.Ordinal)) return true;
            // A bracketed phrase containing letters (e.g. "[target]", "[Your Path]") — numeric arrays like
            // "[0,5,0]" parse to a JSON array, not a string, so they never reach here.
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]") && Regex.IsMatch(trimmed, "[A-Za-z]")) return true;
            if (trimmed.StartsWith("<") && trimmed.EndsWith(">")) return true;          // "<placeholder>"
            return false;
        }

        private static Dictionary<string, LlmToolSpec> BuildToolMap(IReadOnlyList<LlmToolSpec> tools)
        {
            var map = new Dictionary<string, LlmToolSpec>(StringComparer.Ordinal);
            if (tools == null) return map;
            foreach (var tool in tools)
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Name)) continue;
                if (!map.ContainsKey(tool.Name)) map.Add(tool.Name, tool);
            }
            return map;
        }

        private static string StripKnownToolBlocks(string text, IReadOnlyDictionary<string, LlmToolSpec> toolMap)
        {
            var withoutBlocks = ToolBlockRegex.Replace(text ?? string.Empty, m =>
                toolMap.ContainsKey(m.Groups["name"].Value) ? string.Empty : m.Value);
            withoutBlocks = SelfClosingToolRegex.Replace(withoutBlocks, m =>
                toolMap.ContainsKey(m.Groups["name"].Value) ? string.Empty : m.Value);
            return NormalizeVisibleText(withoutBlocks);
        }

        private static string NormalizeVisibleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n", RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
            return normalized.Trim();
        }

        private static string ParseArguments(string body, string inputSchemaJson)
        {
            var args = new JObject();
            var propertyTypes = ReadPropertyTypes(inputSchemaJson);
            foreach (Match match in ToolBlockRegex.Matches(body ?? string.Empty))
            {
                var name = match.Groups["name"].Value;
                var rawValue = WebUtility.HtmlDecode(match.Groups["body"].Value ?? string.Empty).Trim();
                propertyTypes.TryGetValue(name, out var schemaType);
                args[name] = CoerceValue(rawValue, schemaType);
            }
            return args.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static Dictionary<string, string> ReadPropertyTypes(string inputSchemaJson)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(inputSchemaJson)) return result;
            try
            {
                if (JObject.Parse(inputSchemaJson)["properties"] is not JObject props) return result;
                foreach (var property in props.Properties())
                {
                    var type = property.Value["type"];
                    if (type is JArray arr)
                        result[property.Name] = arr.Values<string>().FirstOrDefault(v => v != "null") ?? string.Empty;
                    else
                        result[property.Name] = type?.Value<string>() ?? string.Empty;
                }
            }
            catch
            {
                // Malformed schemas should not make text parsing fail; fall back to best-effort strings.
            }
            return result;
        }

        private static JToken CoerceValue(string value, string schemaType)
        {
            schemaType = schemaType ?? string.Empty;
            if (schemaType == "integer" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                return new JValue(integer);
            if (schemaType == "number" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return new JValue(number);
            if (schemaType == "boolean" && bool.TryParse(value, out var boolean))
                return new JValue(boolean);
            if ((schemaType == "object" || schemaType == "array") && TryParseJson(value, out var structured))
                return structured;

            if (string.IsNullOrEmpty(schemaType) && LooksLikeJsonLiteral(value) && TryParseJson(value, out var inferred))
                return inferred;

            return new JValue(value ?? string.Empty);
        }

        private static bool LooksLikeJsonLiteral(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var trimmed = value.Trim();
            return trimmed.StartsWith("{", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal)
                || trimmed.StartsWith("\"", StringComparison.Ordinal)
                || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)
                || NumberRegex.IsMatch(trimmed);
        }

        private static bool TryParseJson(string value, out JToken token)
        {
            token = null;
            try
            {
                token = JToken.Parse(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string RenderParameterSpec(string inputSchemaJson)
        {
            var parameters = ReadParameterInfos(inputSchemaJson);
            return parameters.Count == 0
                ? "none"
                : string.Join("; ", parameters.Select(p => p.Required
                    ? $"{p.Name} ({p.Type}, required)"
                    : $"{p.Name} ({p.Type})"));
        }

        private static string RenderXmlTemplate(LlmToolSpec tool, string userText)
        {
            var parameters = ReadParameterInfos(tool.InputSchemaJson);
            if (parameters.Count == 0) return $"<{tool.Name}></{tool.Name}>";

            var selected = parameters
                .OrderByDescending(p => p.Required)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Take(5)
                .ToList();

            var sb = new StringBuilder();
            sb.Append('<').Append(tool.Name).Append('>');
            foreach (var parameter in selected)
            {
                sb.Append('<').Append(parameter.Name).Append('>')
                  .Append(PlaceholderForParameter(parameter, userText))
                  .Append("</").Append(parameter.Name).Append('>');
            }
            sb.Append("</").Append(tool.Name).Append('>');
            return sb.ToString();
        }

        private static IReadOnlyList<TextToolParameter> ReadParameterInfos(string inputSchemaJson)
        {
            try
            {
                var schema = JObject.Parse(string.IsNullOrWhiteSpace(inputSchemaJson)
                    ? "{\"type\":\"object\",\"properties\":{}}"
                    : inputSchemaJson);
                if (schema["properties"] is not JObject props || props.Count == 0)
                    return Array.Empty<TextToolParameter>();

                var required = new HashSet<string>(
                    schema["required"] is JArray arr ? arr.Values<string>() : Array.Empty<string>(),
                    StringComparer.Ordinal);
                var parameters = new List<TextToolParameter>();
                foreach (var property in props.Properties())
                {
                    var type = PropertyTypeLabel(property.Value);
                    parameters.Add(new TextToolParameter(property.Name, type, required.Contains(property.Name)));
                }
                return parameters;
            }
            catch
            {
                return Array.Empty<TextToolParameter>();
            }
        }

        private static string PlaceholderForParameter(TextToolParameter parameter, string userText)
        {
            var lower = (parameter.Name ?? string.Empty).ToLowerInvariant();
            if (lower == "target" || lower == "newparent" || lower == "parent")
                return DetectTargetHint(userText);
            if (lower == "name")
                return DetectObjectNameHint(userText);
            if (lower == "primitive")
                return DetectPrimitiveHint(userText);
            if (lower == "position")
                return DetectPositionHint(userText);
            if (lower == "eulerangles" || lower == "rotation")
                return "[x,y,z]";
            if (lower == "scale")
                return "[x,y,z]";
            if (lower == "type")
                return "ComponentType";
            // No real value can be inferred from the request: emit an explicit marker the placeholder guard
            // catches, rather than a plausible-looking token a weak model would copy verbatim (Sprint 69.8).
            if (lower == "path" || lower.EndsWith("path", StringComparison.Ordinal))
                return ReplaceMarker;
            if (parameter.Type.Contains("integer")) return "0";
            if (parameter.Type.Contains("number")) return "0";
            if (parameter.Type.Contains("boolean")) return "true";
            if (parameter.Type.Contains("array")) return "[]";
            if (parameter.Type.Contains("object")) return "{}";
            return ReplaceMarker;
        }

        private static string DetectTargetHint(string userText)
        {
            var instanceId = Regex.Match(userText ?? string.Empty,
                @"(?:instance\s*id|instanceId)\s*[:=]?\s*(-?\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (instanceId.Success) return instanceId.Groups[1].Value;
            return DetectObjectNameHint(userText);
        }

        private static string DetectObjectNameHint(string userText)
        {
            var primitive = DetectPrimitiveHint(userText);
            return primitive != "Cube" || ContainsAny((userText ?? string.Empty).ToLowerInvariant(), "cube")
                ? primitive
                : ReplaceMarker; // no real name inferable — emit the guard-caught marker, not a copyable token
        }

        private static string DetectPrimitiveHint(string userText)
        {
            var lowered = (userText ?? string.Empty).ToLowerInvariant();
            if (lowered.Contains("sphere")) return "Sphere";
            if (lowered.Contains("capsule")) return "Capsule";
            if (lowered.Contains("cylinder")) return "Cylinder";
            if (lowered.Contains("plane")) return "Plane";
            if (lowered.Contains("quad")) return "Quad";
            return "Cube";
        }

        private static string DetectPositionHint(string userText)
        {
            var text = userText ?? string.Empty;
            var y = Regex.Match(text, @"\by(?:\s+position)?\s*(?:to|=)?\s*(-?\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (y.Success) return $"[0,{y.Groups[1].Value},0]";

            var up = Regex.Match(text, @"\bup\s+(?:by\s+)?(-?\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (up.Success) return $"[0,{up.Groups[1].Value},0]";

            return "[x,y,z]";
        }

        private static string PropertyTypeLabel(JToken propertySchema)
        {
            var type = propertySchema?["type"];
            if (type is JArray arr)
            {
                var values = arr.Values<string>().Where(v => v != "null").ToArray();
                return values.Length == 0 ? "value" : string.Join("|", values);
            }
            return type?.Value<string>() ?? "value";
        }

        private static string OneLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var clean = Regex.Replace(text.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
            const int max = 180;
            return clean.Length <= max ? clean : clean.Substring(0, max - 1) + "...";
        }

        private sealed class ToolCandidate
        {
            public LlmToolSpec Tool { get; }
            public int Score { get; }
            public bool Mentioned { get; }

            public ToolCandidate(LlmToolSpec tool, int score, bool mentioned)
            {
                Tool = tool;
                Score = score;
                Mentioned = mentioned;
            }
        }

        private readonly struct TextToolParameter
        {
            public string Name { get; }
            public string Type { get; }
            public bool Required { get; }

            public TextToolParameter(string name, string type, bool required)
            {
                Name = name ?? string.Empty;
                Type = string.IsNullOrWhiteSpace(type) ? "value" : type;
                Required = required;
            }
        }
    }
}
