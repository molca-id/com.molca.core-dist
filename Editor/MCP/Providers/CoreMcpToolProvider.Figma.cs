using System.Collections.Generic;
using Molca.Settings.Integration;
using Molca.Settings.Integration.Figma;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // === molca_figma_list_files (Sprint 30.3) ======================================================

        /// <summary>
        /// The <c>molca_figma_list_files</c> tool: lists the Figma files in the configured team (optionally a
        /// single project). Read-only; requires a connected Figma provider.
        /// </summary>
        private static McpToolDefinition CreateFigmaListFilesTool() => new McpToolDefinition(
            name: "molca_figma_list_files",
            description: "Lists Figma files for the configured team (or a given projectId). Returns projects "
                       + "with their files (key, name, lastModified). Requires the Figma integration to be "
                       + "configured with a token and team id (Hub → Integrations).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"teamId\":{\"type\":\"string\",\"description\":\"Override the configured Figma team id.\"}," +
                "\"projectId\":{\"type\":\"string\",\"description\":\"Restrict to a single project id.\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteFigmaListFiles,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteFigmaListFiles(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var provider = ResolveFigmaProvider(out string error);
            if (provider == null) return FigmaError(error);

            var client = provider.CreateClient();
            if (client == null) return FigmaError("Figma is not connected — add a token in Hub → Integrations.");

            string teamId = args.Value<string>("teamId");
            if (string.IsNullOrWhiteSpace(teamId)) teamId = provider.TeamId;
            string projectFilter = args.Value<string>("projectId");

            var projectsOut = new JArray();

            if (!string.IsNullOrWhiteSpace(projectFilter))
            {
                var files = await client.GetProjectFilesAsync(projectFilter);
                projectsOut.Add(new JObject
                {
                    ["id"] = projectFilter,
                    ["name"] = files?.name,
                    ["files"] = FilesToJson(files)
                });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(teamId))
                    return FigmaError("No team id configured. Set it in Hub → Integrations or pass 'teamId'.");

                var projects = await client.GetTeamProjectsAsync(teamId);
                if (projects?.projects == null)
                    return FigmaError("Could not list projects — check the team id and token.");

                foreach (var project in projects.projects)
                {
                    var files = await client.GetProjectFilesAsync(project.id);
                    projectsOut.Add(new JObject
                    {
                        ["id"] = project.id,
                        ["name"] = project.name,
                        ["files"] = FilesToJson(files)
                    });
                }
            }

            return new JObject { ["projects"] = projectsOut }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JArray FilesToJson(FigmaModels.FilesResponse files)
        {
            var arr = new JArray();
            if (files?.files != null)
            {
                foreach (var file in files.files)
                {
                    arr.Add(new JObject
                    {
                        ["key"] = file.key,
                        ["name"] = file.name,
                        ["lastModified"] = file.last_modified
                    });
                }
            }
            return arr;
        }

        // === molca_figma_list_frames (Sprint 30.3) =====================================================

        /// <summary>
        /// The <c>molca_figma_list_frames</c> tool: lists the top-level frames in a file (id, name, bounds,
        /// child count). Read-only; requires a connected Figma provider.
        /// </summary>
        private static McpToolDefinition CreateFigmaListFramesTool() => new McpToolDefinition(
            name: "molca_figma_list_frames",
            description: "Lists the frames in a Figma file: id, name, type, page, containing section, "
                       + "absoluteBoundingBox, and child count. Descends into SECTION/GROUP containers, so frames "
                       + "grouped under sections are found (not only a canvas's direct children). Pass 'fileKey' or "
                       + "rely on the configured default. Use a frame id with molca_figma_build_frame to scaffold "
                       + "it into UI Toolkit.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"fileKey\":{\"type\":\"string\",\"description\":\"The Figma file key OR a full Figma file URL (the key is parsed from it); omit to use the configured default.\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteFigmaListFrames,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteFigmaListFrames(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var provider = ResolveFigmaProvider(out string error);
            if (provider == null) return FigmaError(error);

            var client = provider.CreateClient();
            if (client == null) return FigmaError("Figma is not connected — add a token in Hub → Integrations.");

            string fileKey = args.Value<string>("fileKey");
            if (string.IsNullOrWhiteSpace(fileKey)) fileKey = provider.DefaultFileKey;
            if (string.IsNullOrWhiteSpace(fileKey))
                return FigmaError("No file key — pass 'fileKey' or set a default in Hub → Integrations.");
            fileKey = FigmaUrl.ResolveFileKey(fileKey);

            // depth=4 covers document→canvas→section→frame (and one extra level of nesting); frames grouped
            // under SECTION/GROUP containers are otherwise invisible at the legacy depth=2.
            var file = await client.GetFileFramesAsync(fileKey, 4);
            if (file == null) return FigmaError("Could not fetch the file — check the file key and token.");

            var frames = new JArray();
            var canvases = file["document"]?["children"] as JArray;
            if (canvases != null)
            {
                foreach (var canvas in canvases)
                    CollectFrames(canvas["children"] as JArray, canvas.Value<string>("name"), null, frames);
            }

            return new JObject { ["fileKey"] = fileKey, ["frames"] = frames }
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        // Node types that are themselves screens to list (and not descended into).
        private static readonly HashSet<string> FrameLikeTypes = new HashSet<string> { "FRAME", "COMPONENT" };

        // Pure-organizational containers to descend through when hunting for frames.
        private static readonly HashSet<string> ContainerTypes =
            new HashSet<string> { "SECTION", "GROUP", "COMPONENT_SET" };

        /// <summary>
        /// Recursively collects frame-like nodes (FRAME/COMPONENT) from a node list, descending through
        /// organizational containers (SECTION/GROUP/COMPONENT_SET) so frames grouped under sections are found.
        /// A frame-like node is recorded and not descended into (its children are its own content, not siblings).
        /// </summary>
        /// <param name="children">The child node array to scan (may be <c>null</c>).</param>
        /// <param name="page">The owning canvas (page) name, for context.</param>
        /// <param name="section">The nearest enclosing SECTION name, or <c>null</c> at the page root.</param>
        /// <param name="output">The accumulator the discovered frames are appended to.</param>
        private static void CollectFrames(JArray children, string page, string section, JArray output)
        {
            if (children == null) return;

            foreach (var node in children)
            {
                string type = node.Value<string>("type");
                if (FrameLikeTypes.Contains(type))
                {
                    output.Add(new JObject
                    {
                        ["id"] = node.Value<string>("id"),
                        ["name"] = node.Value<string>("name"),
                        ["type"] = type,
                        ["page"] = page,
                        ["section"] = section,
                        ["absoluteBoundingBox"] = node["absoluteBoundingBox"],
                        ["childCount"] = (node["children"] as JArray)?.Count ?? 0
                    });
                }
                else if (ContainerTypes.Contains(type))
                {
                    // Track the nearest SECTION as context; GROUP/COMPONENT_SET keep the inherited section.
                    string nextSection = type == "SECTION" ? node.Value<string>("name") : section;
                    CollectFrames(node["children"] as JArray, page, nextSection, output);
                }
            }
        }

        // === molca_figma_build_frame (Sprint 30.6) =====================================================

        /// <summary>
        /// The <c>molca_figma_build_frame</c> tool: fetches a frame node, runs the UI Toolkit translator and
        /// asset pipeline, writes <c>.uxml</c>/<c>.uss</c> (+ sprites), and returns a summary including the
        /// explicit unsupported-node report. Action tool (writes files); Edit mode.
        /// </summary>
        private static McpToolDefinition CreateFigmaBuildFrameTool() => new McpToolDefinition(
            name: "molca_figma_build_frame",
            description: "Scaffolds a Figma frame into UI Toolkit: writes <FrameName>.uxml + .uss (+ imported "
                       + "sprites) under the target folder. Pass 'nodeId' (required) and optionally 'fileKey' "
                       + "and 'folder'. Returns the written assets AND an explicit list of unsupported/dropped "
                       + "nodes (vectors, boolean ops, masks, blend modes) so the fidelity ceiling is visible.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"nodeId\":{\"type\":\"string\",\"description\":\"The frame node id (from molca_figma_list_frames), OR a full Figma URL whose node-id is used. Optional if 'fileKey' is a URL containing a node-id.\"}," +
                "\"fileKey\":{\"type\":\"string\",\"description\":\"The Figma file key OR a full Figma file URL (key and node-id are parsed from it); omit to use the configured default.\"}," +
                "\"folder\":{\"type\":\"string\",\"description\":\"Project-relative output folder; omit to use the configured default.\"}}," +
                "\"required\":[\"nodeId\"],\"additionalProperties\":false}",
            executeAsync: ExecuteFigmaBuildFrame,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteFigmaBuildFrame(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var provider = ResolveFigmaProvider(out string error);
            if (provider == null) return FigmaError(error);

            var client = provider.CreateClient();
            if (client == null) return FigmaError("Figma is not connected — add a token in Hub → Integrations.");

            string nodeIdArg = args.Value<string>("nodeId");
            string fileKeyArg = args.Value<string>("fileKey");

            // Accept a pasted Figma URL in either argument: derive the node id from whichever carries node-id,
            // so a single link (which embeds both key and node) is enough.
            string nodeId = FigmaUrl.ResolveNodeId(nodeIdArg);
            if (string.IsNullOrWhiteSpace(nodeId) && FigmaUrl.LooksLikeUrl(fileKeyArg))
                nodeId = FigmaUrl.ResolveNodeId(fileKeyArg);
            if (string.IsNullOrWhiteSpace(nodeId))
                return FigmaError("Provide a 'nodeId' (or a Figma URL containing a node-id).");

            string fileKey = !string.IsNullOrWhiteSpace(fileKeyArg) ? fileKeyArg
                : FigmaUrl.LooksLikeUrl(nodeIdArg) ? nodeIdArg
                : provider.DefaultFileKey;
            if (string.IsNullOrWhiteSpace(fileKey))
                return FigmaError("No file key — pass 'fileKey' or set a default in Hub → Integrations.");
            fileKey = FigmaUrl.ResolveFileKey(fileKey);

            string folder = args.Value<string>("folder");
            if (string.IsNullOrWhiteSpace(folder)) folder = provider.OutputFolder;

            var response = await client.GetFileNodesAsync(fileKey, new[] { nodeId });
            var document = ExtractDocument(response, nodeId);
            if (document == null) return FigmaError($"Node '{nodeId}' was not found in file '{fileKey}'.");

            var translator = new FigmaToUiToolkitTranslator();
            var result = translator.Translate(document);

            string frameFile = SanitizeFileName(result.FrameName);

            // Export image fills AND rasterized vector/geometry nodes (icons, shapes) — both render via the
            // images endpoint and become background-image rules, so the scaffold keeps the design's graphics.
            var exportNodes = new List<FigmaToUiToolkitTranslator.ImageFill>(result.ImageFills);
            exportNodes.AddRange(result.RasterizedNodes);

            var sprites = await FigmaAssetImporter.ExportImageFillsAsync(
                client, fileKey, exportNodes, folder, frameFile);
            string uss = FigmaAssetImporter.AppendImageRules(result.Uss, sprites);

            // Link the generated USS into the UXML, otherwise UI Toolkit applies no styles and every element
            // collapses to default size/flow — the document renders as a bare vertical list of labels. The USS
            // is written next to the UXML, so a same-folder relative src resolves.
            string linkedUxml = InsertStylesheetReference(result.Uxml, $"{frameFile}.uss");

            // Write the USS first: importing the UXML validates its <Style src> reference, so the stylesheet
            // asset must already exist or Unity logs "invalid asset" for the (not-yet-written) .uss.
            string ussPath = FigmaAssetImporter.WriteTextAsset(folder, $"{frameFile}.uss", uss);
            string uxmlPath = FigmaAssetImporter.WriteTextAsset(folder, $"{frameFile}.uxml", linkedUxml);
            UnityEditor.AssetDatabase.SaveAssets();

            var unsupported = new JArray();
            foreach (var node in result.Unsupported)
            {
                unsupported.Add(new JObject
                {
                    ["id"] = node.Id,
                    ["name"] = node.Name,
                    ["type"] = node.Type,
                    ["reason"] = node.Reason
                });
            }

            var fontWarnings = new JArray();
            foreach (var warning in result.FontWarnings) fontWarnings.Add(warning);

            // Surface the report in the Console too, so a run outside MCP is not silent (Sprint 30.6).
            if (result.Unsupported.Count > 0 || result.FontWarnings.Count > 0)
            {
                Debug.LogWarning($"[Figma] Built '{frameFile}' with {result.Unsupported.Count} unsupported node(s) "
                               + $"and {result.FontWarnings.Count} font warning(s). See the tool result for details.");
            }
            else
            {
                Debug.Log($"[Figma] Built '{frameFile}' → {uxmlPath} (+ {sprites.Count} sprite(s)).");
            }

            var spritesOut = new JArray();
            foreach (var sprite in sprites) spritesOut.Add(sprite.AssetPath);

            var summary = new JObject
            {
                ["frameName"] = result.FrameName,
                ["uxmlPath"] = uxmlPath,
                ["ussPath"] = ussPath,
                ["spritesWritten"] = spritesOut,
                ["rasterizedNodes"] = result.RasterizedNodes.Count,
                ["unsupportedNodes"] = unsupported,
                ["fontWarnings"] = fontWarnings
            };
            return summary.ToString(Newtonsoft.Json.Formatting.None);
        }

        // === molca_figma_build_panel (Phase 3 — Figma → live UI Toolkit panel) =========================

        /// <summary>
        /// The <c>molca_figma_build_panel</c> tool: end-to-end Figma scaffolding. Builds a frame into UXML/USS
        /// (delegating to <see cref="ExecuteFigmaBuildFrame"/>, so URL parsing, depth, and sprite export are
        /// reused), then creates/reuses a themed <see cref="PanelSettings"/> and a <see cref="UIDocument"/> in the
        /// open scene so the frame renders. Action tool (writes assets + a scene object); Edit mode.
        /// </summary>
        private static McpToolDefinition CreateFigmaBuildPanelTool() => new McpToolDefinition(
            name: "molca_figma_build_panel",
            description: "End-to-end: builds a Figma frame into UI Toolkit (UXML/USS/sprites) AND wires it into a "
                       + "rendering UIDocument in the open scene. Takes the same 'nodeId'/'fileKey' as "
                       + "molca_figma_build_frame (a Figma URL works), plus optional 'folder', 'panelSettings' "
                       + "(reuse an existing PanelSettings path; a themed one is created next to the UXML if "
                       + "omitted), 'parent', 'name', 'sortingOrder', and 'themeStyleSheet'. Writes assets "
                       + "(irreversible); the GameObject is one undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"nodeId\":{\"type\":\"string\",\"description\":\"The frame node id (from molca_figma_list_frames), OR a full Figma URL whose node-id is used.\"}," +
                "\"fileKey\":{\"type\":\"string\",\"description\":\"The Figma file key OR a full Figma file URL; omit to use the configured default.\"}," +
                "\"folder\":{\"type\":\"string\",\"description\":\"Project-relative output folder for the generated assets; omit to use the configured default.\"}," +
                "\"panelSettings\":{\"type\":\"string\",\"description\":\"Reuse an existing PanelSettings asset path; a themed one is created if omitted.\"}," +
                "\"parent\":{\"type\":\"string\",\"description\":\"Parent GameObject hierarchy path or instance id (root if omitted).\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Name for the new GameObject (defaults to the frame name).\"}," +
                "\"sortingOrder\":{\"type\":\"number\",\"description\":\"Panel sorting order (default 0).\"}," +
                "\"themeStyleSheet\":{\"type\":\"string\",\"description\":\"Path to a .tss to assign instead of the Molca default theme (only when creating a PanelSettings).\"}}," +
                "\"required\":[\"nodeId\"],\"additionalProperties\":false}",
            executeAsync: ExecuteFigmaBuildPanel,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteFigmaBuildPanel(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            // 1) Build the frame by delegating to the existing tool (reuses URL parsing, depth, sprite export).
            var buildArgs = new JObject();
            if (args["nodeId"] != null) buildArgs["nodeId"] = args["nodeId"];
            if (args["fileKey"] != null) buildArgs["fileKey"] = args["fileKey"];
            if (args["folder"] != null) buildArgs["folder"] = args["folder"];

            var build = JObject.Parse(await ExecuteFigmaBuildFrame(buildArgs.ToString(Newtonsoft.Json.Formatting.None)));
            if (build["error"] != null) return build.ToString(Newtonsoft.Json.Formatting.None); // propagate verbatim

            string uxmlPath = build.Value<string>("uxmlPath");
            if (string.IsNullOrEmpty(uxmlPath)) return FigmaError("frame build did not produce a UXML path.");
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (uxml == null) return FigmaError($"could not load the generated UXML at '{uxmlPath}'.");

            // 2) Resolve or create the PanelSettings (created next to the generated UXML when not supplied).
            string panelPath = args.Value<string>("panelSettings");
            PanelSettings panel;
            string themeSource = null, themeWarning = null;
            if (!string.IsNullOrWhiteSpace(panelPath))
            {
                panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
                if (panel == null)
                    return FigmaError($"panelSettings '{panelPath}' did not resolve to a PanelSettings asset.");
            }
            else
            {
                int slash = uxmlPath.LastIndexOf('/');
                string folder = slash > 0 ? uxmlPath.Substring(0, slash) : "Assets";
                if (!UiToolkitAuthoringService.CreatePanelSettings(
                        folder, "PanelSettings", args.Value<string>("themeStyleSheet"), out var psr, out var psError))
                    return FigmaError(psError);
                panel = psr.Panel;
                panelPath = psr.AssetPath;
                themeSource = psr.ThemeSource;
                themeWarning = psr.ThemeWarning;
            }

            // 3) Create the UIDocument GameObject.
            GameObject parent = null;
            var parentArg = args.Value<string>("parent");
            if (!string.IsNullOrWhiteSpace(parentArg))
            {
                parent = GameObjectEditingService.Resolve(parentArg, out var parentError);
                if (parent == null) return FigmaError(parentError);
            }

            string goName = args.Value<string>("name");
            if (string.IsNullOrWhiteSpace(goName)) goName = build.Value<string>("frameName");
            float sortingOrder = (float)(args.Value<float?>("sortingOrder") ?? 0f);

            var go = UiToolkitAuthoringService.CreateUiDocument(
                goName, parent, uxml, panel, sortingOrder, out var document, out var docError);
            if (go == null) return FigmaError(docError);

            var summary = new JObject
            {
                ["frameName"] = build.Value<string>("frameName"),
                ["uxmlPath"] = uxmlPath,
                ["ussPath"] = build.Value<string>("ussPath"),
                ["spritesWritten"] = build["spritesWritten"]?.DeepClone(),
                ["unsupportedNodes"] = build["unsupportedNodes"]?.DeepClone(),
                ["fontWarnings"] = build["fontWarnings"]?.DeepClone(),
                ["panelSettings"] = panelPath,
                ["themeSource"] = themeSource,
                ["gameObject"] = GameObjectEditingService.GetHierarchyPath(go),
                ["instanceId"] = go.GetInstanceID(),
                ["sortingOrder"] = document.sortingOrder
            };
            if (themeWarning != null) summary["themeWarning"] = themeWarning;
            return summary.ToString(Newtonsoft.Json.Formatting.None);
        }

        // --- helpers -----------------------------------------------------------------------------------

        private static FigmaIntegrationProvider ResolveFigmaProvider(out string error)
        {
            error = null;
            var settings = IntegrationSettings.FindSettings();
            var provider = settings != null ? settings.GetProvider<FigmaIntegrationProvider>() : null;
            if (provider == null)
            {
                error = "No Figma integration is registered. Add one in Hub → Integrations (+ Add integration).";
                return null;
            }
            if (!provider.HasToken)
            {
                error = "Figma is not connected — add a token in Hub → Integrations.";
                return null;
            }
            return provider;
        }

        // The /nodes response keys by the requested id, but Figma may normalize id punctuation; fall back to
        // the first returned node so a build still succeeds when the key differs cosmetically.
        private static JToken ExtractDocument(JObject response, string nodeId)
        {
            if (response?["nodes"] is not JObject nodes) return null;

            if (nodes[nodeId]?["document"] is JToken exact) return exact;
            foreach (var pair in nodes)
            {
                if (pair.Value?["document"] is JToken doc) return doc;
            }
            return null;
        }

        /// <summary>
        /// Inserts a <c>&lt;ui:Style src="..."/&gt;</c> as the first child of the root UXML element so the
        /// generated stylesheet is actually applied. Without it UI Toolkit renders unstyled, collapsed elements.
        /// </summary>
        /// <param name="uxml">The generated UXML text (opening <c>&lt;ui:UXML ...&gt;</c> on the first line).</param>
        /// <param name="ussFileName">The USS file name, relative to the UXML (same folder).</param>
        /// <returns>The UXML with the Style reference injected, or the input unchanged if it has no newline.</returns>
        private static string InsertStylesheetReference(string uxml, string ussFileName)
        {
            int firstNewline = uxml.IndexOf('\n');
            if (firstNewline < 0) return uxml;
            string styleLine = $"    <ui:Style src=\"{ussFileName}\" />\n";
            return uxml.Insert(firstNewline + 1, styleLine);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Frame";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static string FigmaError(string message)
            => new JObject { ["error"] = message }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
