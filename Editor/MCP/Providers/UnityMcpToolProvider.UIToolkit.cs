using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// UI Toolkit MCP tool family (<c>molca_unity_uitk_*</c>): wires generated UXML/USS into a rendering UI by
    /// creating <see cref="PanelSettings"/> assets and <see cref="UIDocument"/> components. Complements the
    /// read-only UGUI tools under <c>molca_unity_ui_*</c>; this family targets UI Toolkit runtime UI.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/MCP/Providers/</c>.
    /// Registration: partial of <see cref="UnityMcpToolProvider"/>; the convention-based discovery picks up the
    /// <c>Create*Tool()</c> factories automatically.
    /// <para>
    /// New panels are assigned the shipped Molca theme (<see cref="MolcaDefaultThemePath"/>) so they render
    /// without manual theme wiring; the family falls back to any project <see cref="ThemeStyleSheet"/> when the
    /// shipped theme is missing, and reports plainly when none can be found.
    /// </para>
    /// </remarks>
    public sealed partial class UnityMcpToolProvider
    {
        // === molca_unity_uitk_create_panel_settings ====================================================

        private static McpToolDefinition CreateUiToolkitCreatePanelSettingsTool() => new McpToolDefinition(
            name: "molca_unity_uitk_create_panel_settings",
            description: "Creates a UI Toolkit PanelSettings asset and assigns the Molca default theme so it "
                       + "renders immediately. Pass 'folder' (project-relative, default 'Assets/UI Toolkit') and "
                       + "optional 'name' and 'themeStyleSheet' (a .tss asset path to override the Molca theme). "
                       + "Writes a new asset (irreversible). Returns the asset path and which theme was assigned.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"folder\":{\"type\":\"string\",\"description\":\"Project-relative folder (default 'Assets/UI Toolkit').\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Asset name without extension (default 'PanelSettings').\"}," +
                "\"themeStyleSheet\":{\"type\":\"string\",\"description\":\"Path to a .tss to assign instead of the Molca default theme.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteUiToolkitCreatePanelSettings,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteUiToolkitCreatePanelSettings(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            if (!UiToolkitAuthoringService.CreatePanelSettings(
                    args.Value<string>("folder"), args.Value<string>("name"), args.Value<string>("themeStyleSheet"),
                    out var psr, out var error))
                return Error(error);

            var result = new JObject
            {
                ["assetPath"] = psr.AssetPath,
                ["themeAssigned"] = psr.Panel.themeStyleSheet != null
                    ? AssetDatabase.GetAssetPath(psr.Panel.themeStyleSheet) : null,
                ["themeSource"] = psr.ThemeSource
            };
            if (psr.ThemeWarning != null) result["warning"] = psr.ThemeWarning;
            return result.ToString(Formatting.None);
        }

        // === molca_unity_uitk_create_uidocument ========================================================

        private static McpToolDefinition CreateUiToolkitCreateUiDocumentTool() => new McpToolDefinition(
            name: "molca_unity_uitk_create_uidocument",
            description: "Creates a GameObject with a UIDocument and wires it up: assigns the source UXML "
                       + "('visualTreeAsset', a .uxml path) and 'panelSettings' (a PanelSettings asset path), with "
                       + "optional 'name', 'parent' (hierarchy path or instance id), and 'sortingOrder'. One undo "
                       + "group; revert with Ctrl+Z. Returns the new object's path and instance id.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"visualTreeAsset\":{\"type\":\"string\",\"description\":\"Path to the source .uxml asset.\"}," +
                "\"panelSettings\":{\"type\":\"string\",\"description\":\"Path to a PanelSettings asset (from molca_unity_uitk_create_panel_settings).\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Name for the new GameObject (default 'UIDocument').\"}," +
                "\"parent\":{\"type\":\"string\",\"description\":\"Parent hierarchy path or instance id (root if omitted).\"}," +
                "\"sortingOrder\":{\"type\":\"number\",\"description\":\"Panel sorting order (default 0).\"}}," +
                "\"required\":[\"visualTreeAsset\",\"panelSettings\"],\"additionalProperties\":false}",
            execute: ExecuteUiToolkitCreateUiDocument,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteUiToolkitCreateUiDocument(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var uxml = LoadAssetAtPath<VisualTreeAsset>(args.Value<string>("visualTreeAsset"),
                "visualTreeAsset", "a .uxml VisualTreeAsset", out var uxmlError);
            if (uxml == null) return Error(uxmlError);

            var panel = LoadAssetAtPath<PanelSettings>(args.Value<string>("panelSettings"),
                "panelSettings", "a PanelSettings asset", out var panelError);
            if (panel == null) return Error(panelError);

            GameObject parent = null;
            var parentArg = args.Value<string>("parent");
            if (!string.IsNullOrWhiteSpace(parentArg))
            {
                parent = GameObjectEditingService.Resolve(parentArg, out var parentResolveError);
                if (parent == null) return Error(parentResolveError);
            }

            float sortingOrder = (float)(args.Value<float?>("sortingOrder") ?? 0f);
            var go = UiToolkitAuthoringService.CreateUiDocument(
                args.Value<string>("name"), parent, uxml, panel, sortingOrder, out var document, out var createError);
            if (go == null) return Error(createError);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["instanceId"] = go.GetInstanceID(),
                ["visualTreeAsset"] = AssetDatabase.GetAssetPath(uxml),
                ["panelSettings"] = AssetDatabase.GetAssetPath(panel),
                ["sortingOrder"] = document.sortingOrder
            }.ToString(Formatting.None);
        }

        // === molca_unity_uitk_set_uidocument ===========================================================

        private static McpToolDefinition CreateUiToolkitSetUiDocumentTool() => new McpToolDefinition(
            name: "molca_unity_uitk_set_uidocument",
            description: "Re-points an existing UIDocument: sets any of 'visualTreeAsset' (.uxml path), "
                       + "'panelSettings' (PanelSettings path), or 'sortingOrder' on the UIDocument found on "
                       + "'target' (hierarchy path or instance id). One undo group; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id carrying a UIDocument.\"}," +
                "\"visualTreeAsset\":{\"type\":\"string\",\"description\":\"New source .uxml asset path.\"}," +
                "\"panelSettings\":{\"type\":\"string\",\"description\":\"New PanelSettings asset path.\"}," +
                "\"sortingOrder\":{\"type\":\"number\",\"description\":\"New panel sorting order.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteUiToolkitSetUiDocument,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteUiToolkitSetUiDocument(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            var document = go.GetComponent<UIDocument>();
            if (document == null) return Error($"'{go.name}' has no UIDocument component.");

            bool hasUxml = args.ContainsKey("visualTreeAsset");
            bool hasPanel = args.ContainsKey("panelSettings");
            bool hasOrder = args.ContainsKey("sortingOrder");
            if (!hasUxml && !hasPanel && !hasOrder)
                return Error("provide at least one of 'visualTreeAsset', 'panelSettings', or 'sortingOrder'.");

            VisualTreeAsset uxml = null;
            if (hasUxml)
            {
                uxml = LoadAssetAtPath<VisualTreeAsset>(args.Value<string>("visualTreeAsset"),
                    "visualTreeAsset", "a .uxml VisualTreeAsset", out var uxmlError);
                if (uxml == null) return Error(uxmlError);
            }

            PanelSettings panel = null;
            if (hasPanel)
            {
                panel = LoadAssetAtPath<PanelSettings>(args.Value<string>("panelSettings"),
                    "panelSettings", "a PanelSettings asset", out var panelError);
                if (panel == null) return Error(panelError);
            }

            Undo.RecordObject(document, "Set UIDocument");
            if (hasUxml) document.visualTreeAsset = uxml;
            if (hasPanel) document.panelSettings = panel;
            if (hasOrder) document.sortingOrder = (float)(args.Value<float?>("sortingOrder") ?? document.sortingOrder);
            EditorUtility.SetDirty(document);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["visualTreeAsset"] = document.visualTreeAsset != null ? AssetDatabase.GetAssetPath(document.visualTreeAsset) : null,
                ["panelSettings"] = document.panelSettings != null ? AssetDatabase.GetAssetPath(document.panelSettings) : null,
                ["sortingOrder"] = document.sortingOrder
            }.ToString(Formatting.None);
        }

        // === molca_unity_uitk_list_documents (read-only) ===============================================

        private static McpToolDefinition CreateUiToolkitListDocumentsTool() => new McpToolDefinition(
            name: "molca_unity_uitk_list_documents",
            description: "Lists UI Toolkit assets in the project grouped by kind: UXML (VisualTreeAsset), USS "
                       + "(StyleSheet), PanelSettings, and ThemeStyleSheet (.tss). Optional 'folder' scopes the "
                       + "search to a project-relative folder.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"folder\":{\"type\":\"string\",\"description\":\"Project-relative folder to scope the search (default: whole project).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteUiToolkitListDocuments,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteUiToolkitListDocuments(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            string folder = args.Value<string>("folder");
            string[] folders = !string.IsNullOrWhiteSpace(folder)
                ? new[] { folder.Replace('\\', '/').TrimEnd('/') }
                : null;

            // ThemeStyleSheet derives from StyleSheet, so the USS list filters by extension to exclude .tss.
            return new JObject
            {
                ["uxml"] = FindUiToolkitAssets("t:VisualTreeAsset", folders),
                ["uss"] = FindUiToolkitAssets("t:StyleSheet", folders, ".uss"),
                ["panelSettings"] = FindUiToolkitAssets("t:PanelSettings", folders),
                ["themes"] = FindUiToolkitAssets("t:ThemeStyleSheet", folders)
            }.ToString(Formatting.None);
        }

        // === molca_unity_uitk_scene_uidocuments (read-only) ============================================

        private static McpToolDefinition CreateUiToolkitSceneUiDocumentsTool() => new McpToolDefinition(
            name: "molca_unity_uitk_scene_uidocuments",
            description: "Lists UIDocument components in the loaded scene(s) with hierarchy path, enabled state, "
                       + "source UXML (visualTreeAsset), assigned PanelSettings, and sort order. The UI Toolkit "
                       + "counterpart to molca_unity_canvases.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteUiToolkitSceneUiDocuments,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteUiToolkitSceneUiDocuments(string argumentsJson)
        {
            var documents = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var document in root.GetComponentsInChildren<UIDocument>(true))
                {
                    documents.Add(new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(document.gameObject),
                        ["instanceId"] = document.GetInstanceID(),
                        ["enabled"] = document.enabled,
                        ["visualTreeAsset"] = document.visualTreeAsset != null
                            ? AssetDatabase.GetAssetPath(document.visualTreeAsset) : null,
                        ["panelSettings"] = document.panelSettings != null
                            ? AssetDatabase.GetAssetPath(document.panelSettings) : null,
                        ["sortingOrder"] = document.sortingOrder
                    });
                }
            }

            return new JObject { ["count"] = documents.Count, ["uiDocuments"] = documents }
                .ToString(Formatting.None);
        }

        // === molca_unity_uitk_inspect_uxml (read-only) =================================================

        private static McpToolDefinition CreateUiToolkitInspectUxmlTool() => new McpToolDefinition(
            name: "molca_unity_uitk_inspect_uxml",
            description: "Parses a .uxml file and reports its top-level elements, referenced stylesheets "
                       + "(<Style>), template definitions (<Template>), and any style/template references that do "
                       + "not resolve to a project asset (the fidelity/wiring gaps). Pass 'uxmlPath'.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"uxmlPath\":{\"type\":\"string\",\"description\":\"Project-relative path to the .uxml file.\"}}," +
                "\"required\":[\"uxmlPath\"],\"additionalProperties\":false}",
            execute: ExecuteUiToolkitInspectUxml,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteUiToolkitInspectUxml(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            string path = args.Value<string>("uxmlPath");
            if (string.IsNullOrWhiteSpace(path)) return Error("'uxmlPath' is required.");
            if (!File.Exists(path)) return Error($"uxml file not found at '{path}'.");

            XDocument doc;
            try { doc = XDocument.Load(path); }
            catch (Exception e) { return Error($"failed to parse UXML: {e.Message}"); }

            var root = doc.Root;
            if (root == null) return Error("UXML has no root element.");

            var styleSheets = new JArray();
            var templates = new JArray();
            var unresolved = new JArray();

            foreach (var style in root.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                string src = (string)style.Attribute("src") ?? (string)style.Attribute("path");
                string resolved = ResolveUxmlSrc(src, path);
                styleSheets.Add(new JObject { ["src"] = src, ["resolved"] = resolved });
                if (src != null && resolved == null)
                    unresolved.Add(new JObject { ["kind"] = "style", ["src"] = src });
            }

            foreach (var template in root.Descendants().Where(e => e.Name.LocalName == "Template"))
            {
                string name = (string)template.Attribute("name");
                string src = (string)template.Attribute("src") ?? (string)template.Attribute("path");
                string resolved = ResolveUxmlSrc(src, path);
                templates.Add(new JObject { ["name"] = name, ["src"] = src, ["resolved"] = resolved });
                if (src != null && resolved == null)
                    unresolved.Add(new JObject { ["kind"] = "template", ["name"] = name, ["src"] = src });
            }

            var rootElements = new JArray();
            foreach (var el in root.Elements())
            {
                var local = el.Name.LocalName;
                if (local == "Style" || local == "Template" || local == "AttributeOverrides") continue;
                rootElements.Add(new JObject
                {
                    ["type"] = local,
                    ["name"] = (string)el.Attribute("name"),
                    ["class"] = (string)el.Attribute("class")
                });
            }

            return new JObject
            {
                ["uxmlPath"] = path,
                ["rootElements"] = rootElements,
                ["styleSheets"] = styleSheets,
                ["templates"] = templates,
                ["unresolved"] = unresolved
            }.ToString(Formatting.None);
        }

        // === molca_unity_uitk_link_stylesheet (action, file-snapshot) ==================================

        private static McpToolDefinition CreateUiToolkitLinkStylesheetTool() => new McpToolDefinition(
            name: "molca_unity_uitk_link_stylesheet",
            description: "Adds a <Style src=...> reference to a .uxml root so a .uss applies to the document. "
                       + "Pass 'uxmlPath' and 'ussPath'. Reports a no-op if already linked. The uxml file is "
                       + "snapshotted first; revert with molca_undo_last_action.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"uxmlPath\":{\"type\":\"string\",\"description\":\"Project-relative path to the .uxml file.\"}," +
                "\"ussPath\":{\"type\":\"string\",\"description\":\"Project-relative path to the .uss StyleSheet to link.\"}}," +
                "\"required\":[\"uxmlPath\",\"ussPath\"],\"additionalProperties\":false}",
            execute: ExecuteUiToolkitLinkStylesheet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        private static string ExecuteUiToolkitLinkStylesheet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            string uxmlPath = args.Value<string>("uxmlPath");
            string ussPath = args.Value<string>("ussPath");
            if (string.IsNullOrWhiteSpace(uxmlPath)) return Error("'uxmlPath' is required.");
            if (string.IsNullOrWhiteSpace(ussPath)) return Error("'ussPath' is required.");
            if (!File.Exists(uxmlPath)) return Error($"uxml file not found at '{uxmlPath}'.");
            if (AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath) == null)
                return Error($"'{ussPath}' did not resolve to a StyleSheet (.uss).");

            XDocument doc;
            try { doc = XDocument.Load(uxmlPath, LoadOptions.PreserveWhitespace); }
            catch (Exception e) { return Error($"failed to parse UXML: {e.Message}"); }

            var root = doc.Root;
            if (root == null) return Error("UXML has no root element.");

            foreach (var style in root.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                string existing = (string)style.Attribute("src") ?? (string)style.Attribute("path");
                if (ResolveUxmlSrc(existing, uxmlPath) == ussPath)
                    return new JObject
                    {
                        ["uxmlPath"] = uxmlPath,
                        ["alreadyLinked"] = true,
                        ["src"] = existing
                    }.ToString(Formatting.None);
            }

            string snapshotId = McpUndoStack.Snapshot(uxmlPath, "molca_unity_uitk_link_stylesheet", $"Link {ussPath}");

            string relSrc = MakeRelativeAssetPath(uxmlPath, ussPath);
            root.AddFirst(new XElement(root.Name.Namespace + "Style", new XAttribute("src", relSrc)));
            doc.Save(uxmlPath);
            AssetDatabase.ImportAsset(uxmlPath);

            return new JObject
            {
                ["uxmlPath"] = uxmlPath,
                ["linked"] = ussPath,
                ["src"] = relSrc,
                ["snapshotId"] = snapshotId
            }.ToString(Formatting.None);
        }

        // --- helpers -----------------------------------------------------------------------------------

        /// <summary>Finds project assets matching a type filter, optionally scoped to a folder and extension.</summary>
        private static JArray FindUiToolkitAssets(string typeFilter, string[] folders, string requiredExtension = null)
        {
            var arr = new JArray();
            var guids = folders != null
                ? AssetDatabase.FindAssets(typeFilter, folders)
                : AssetDatabase.FindAssets(typeFilter);

            foreach (var guid in guids.Distinct())
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (requiredExtension != null && !path.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
                    continue;
                arr.Add(new JObject { ["path"] = path, ["name"] = Path.GetFileNameWithoutExtension(path) });
            }
            return arr;
        }

        /// <summary>
        /// Resolves a UXML <c>src</c>/<c>path</c> reference to a project asset path: handles
        /// <c>project://database/...?guid=...</c> URIs, project-relative paths, and paths relative to the uxml.
        /// </summary>
        /// <returns>The resolved project asset path, or <c>null</c> if it does not resolve.</returns>
        private static string ResolveUxmlSrc(string src, string uxmlPath)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;

            if (src.StartsWith("project://", StringComparison.OrdinalIgnoreCase))
            {
                var guidMatch = Regex.Match(src, "guid=([0-9a-fA-F]{32})");
                if (guidMatch.Success)
                {
                    string byGuid = AssetDatabase.GUIDToAssetPath(guidMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(byGuid)) return byGuid;
                }
                string noQuery = src.Split('?')[0];
                int idx = noQuery.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string embedded = noQuery.Substring(idx);
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(embedded) != null) return embedded;
                }
                return null;
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(src) != null) return src;

            string dir = Path.GetDirectoryName(uxmlPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
            {
                string combined = NormalizeAssetPath($"{dir}/{src}");
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(combined) != null) return combined;
            }
            return null;
        }

        /// <summary>Collapses <c>.</c>/<c>..</c> segments in a forward-slash path.</summary>
        private static string NormalizeAssetPath(string path)
        {
            var parts = new List<string>();
            foreach (var seg in path.Replace('\\', '/').Split('/'))
            {
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (seg != "." && seg.Length > 0) parts.Add(seg);
            }
            return string.Join("/", parts);
        }

        /// <summary>Builds a path to <paramref name="toAsset"/> relative to the folder of <paramref name="fromFile"/>.</summary>
        private static string MakeRelativeAssetPath(string fromFile, string toAsset)
        {
            string fromDir = Path.GetDirectoryName(fromFile)?.Replace('\\', '/') ?? string.Empty;
            var fromParts = fromDir.Length > 0 ? fromDir.Split('/') : Array.Empty<string>();
            var toParts = toAsset.Replace('\\', '/').Split('/');

            int common = 0;
            while (common < fromParts.Length && common < toParts.Length && fromParts[common] == toParts[common])
                common++;

            var rel = new List<string>();
            for (int i = common; i < fromParts.Length; i++) rel.Add("..");
            for (int i = common; i < toParts.Length; i++) rel.Add(toParts[i]);
            return string.Join("/", rel);
        }

        /// <summary>Loads a project asset at <paramref name="path"/>, producing a descriptive error on failure.</summary>
        private static T LoadAssetAtPath<T>(string path, string argName, string expected, out string error)
            where T : UnityEngine.Object
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = $"'{argName}' is required.";
                return null;
            }

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                error = $"'{argName}' path '{path}' did not resolve to {expected}.";
            return asset;
        }
    }
}
