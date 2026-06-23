using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Molca.ColorID;
using Molca.Settings;
using Molca.UI.Tokens;
using Molca.Editor.UI.Figma;
using Molca.Editor.Mcp.Assistant;
using Molca.Settings.Integration.Figma;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // ── molca_figma_to_ui_spec ───────────────────────────────────────────────────────────
        //
        // Sprint 58: reads a Figma frame and a Sprint-57 token catalog and emits a token-referential
        // UI Intent Spec (the semantic layer the Sprint-59 materializer will turn into a uGUI prefab).
        // Read-only — it fetches Figma, resolves the catalog, and computes; it writes nothing.

        /// <summary>
        /// The <c>molca_figma_to_ui_spec</c> tool: Figma frame → validated <see cref="UiIntentSpec"/> +
        /// a per-node mapping report. Deterministic pre-pass (CIEDE2000 color snap, text-preset snap,
        /// button/list recognition) optionally refined by the model, always re-validated against the catalog.
        /// </summary>
        private static McpToolDefinition CreateFigmaToUiSpecTool() => new McpToolDefinition(
            name: "molca_figma_to_ui_spec",
            description: "Reads a Figma frame and maps it to a Molca UI Intent Spec — a token-referential, "
                       + "Unity-internal-free description (colors/text/controls are Sprint-57 catalog token "
                       + "ids; unmappable items are flagged '_unmapped', never raw hex). The semantic input "
                       + "for building a uGUI prefab. Pass 'figmaUrlOrNode' (a frame node id or full Figma "
                       + "URL) and optionally 'fileKey', 'catalog' (UI Token Catalog asset name; omit to use "
                       + "the only/first one), 'worldScale' (panel width in metres, default 0.5) and "
                       + "'minHitCm' (default 4) — the VR inputs Figma can't carry. Read-only: builds nothing. "
                       + "Returns { spec, mapping, unmapped, catalog }.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"figmaUrlOrNode\":{\"type\":\"string\",\"description\":\"Frame node id (from molca_figma_list_frames) or a full Figma URL.\"}," +
                "\"fileKey\":{\"type\":\"string\",\"description\":\"Figma file key or URL; omit to use the configured default.\"}," +
                "\"catalog\":{\"type\":\"string\",\"description\":\"UI Token Catalog asset name; omit to use the only/first one in the project.\"}," +
                "\"worldScale\":{\"type\":\"number\",\"description\":\"Panel width in metres (VR). Default 0.5.\"}," +
                "\"minHitCm\":{\"type\":\"number\",\"description\":\"Minimum hit-target size in cm (VR). Default 4.\"}}," +
                "\"required\":[\"figmaUrlOrNode\"],\"additionalProperties\":false}",
            executeAsync: ExecuteFigmaToUiSpec,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteFigmaToUiSpec(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);

            var catalog = ResolveUiTokenCatalog(args.Value<string>("catalog"), out string catalogError);
            if (catalog == null) return Error(catalogError);

            var provider = ResolveFigmaProvider(out string figmaError);
            if (provider == null) return FigmaError(figmaError);
            var client = provider.CreateClient();

            string nodeArg = args.Value<string>("figmaUrlOrNode");
            string nodeId = FigmaUrl.ResolveNodeId(nodeArg);
            if (string.IsNullOrWhiteSpace(nodeId)) return FigmaError("Provide a frame node id or a Figma URL containing one.");

            string fileKey = args.Value<string>("fileKey");
            if (string.IsNullOrWhiteSpace(fileKey))
                fileKey = FigmaUrl.LooksLikeUrl(nodeArg) ? FigmaUrl.ResolveFileKey(nodeArg) : provider.DefaultFileKey;
            fileKey = FigmaUrl.ResolveFileKey(fileKey);
            if (string.IsNullOrWhiteSpace(fileKey)) return FigmaError("No Figma file key (pass 'fileKey' or configure a default).");

            var response = await client.GetFileNodesAsync(fileKey, new[] { nodeId });
            var document = ExtractDocument(response, nodeId);
            if (document == null) return FigmaError($"Node '{nodeId}' was not found in file '{fileKey}'.");

            var frame = FigmaFrameModel.Parse(document);
            var colors = GlobalSettings.GetModule<ColorModule>() as IColorProvider; // active palette (may be null)
            float worldScale = args.Value<float?>("worldScale") ?? 0.5f;
            float minHitCm = args.Value<float?>("minHitCm") ?? 4f;

            var draft = FigmaTokenMapper.BuildDraft(frame, catalog, colors, frame?.Name, worldScale, minHitCm);

            // Optional model refinement; any failure (no key, network, invalid output) falls back to the draft.
            UiIntentSpec spec = draft.Spec;
            try
            {
                var settings = AssistantSettings.GetOrCreateSettings();
                if (settings != null)
                    spec = await FigmaSpecComposer.RefineAsync(
                        draft, catalog, settings.CreateProvider(), settings.Model, CancellationToken.None);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Figma→UI] Model refinement skipped: {ex.Message}");
                spec = draft.Spec;
            }

            var mapping = new JArray();
            var unmapped = new JArray();
            foreach (var entry in draft.Report)
            {
                mapping.Add(new JObject
                {
                    ["path"] = entry.Path,
                    ["kind"] = entry.Kind,
                    ["token"] = entry.TokenId,
                    ["confidence"] = entry.Confidence,
                    ["unmapped"] = entry.Unmapped,
                });
                if (entry.Unmapped) unmapped.Add(entry.Path);
            }

            return new JObject
            {
                ["catalog"] = catalog.name,
                ["spec"] = JObject.Parse(JsonConvert.SerializeObject(spec)),
                ["mapping"] = mapping,
                ["unmapped"] = unmapped,
            }.ToString(Formatting.None);
        }

        /// <summary>
        /// Resolves a <see cref="MolcaUiTokenCatalog"/> by asset name, or the only/first one in the project
        /// when no name is given. Returns null with an explanatory error when none exists.
        /// </summary>
        private static MolcaUiTokenRegistry ResolveUiTokenCatalog(string nameOrNull, out string error)
        {
            error = null;
            var guids = AssetDatabase.FindAssets("t:MolcaUiTokenCatalog");
            if (guids == null || guids.Length == 0)
            {
                error = "No UI Token Catalog found. Create one (Create > Molca > UI > UI Token Catalog) "
                      + "or mine one from your UI prefabs (Sprint 57).";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(nameOrNull))
            {
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var catalog = AssetDatabase.LoadAssetAtPath<MolcaUiTokenCatalog>(path);
                    if (catalog != null && catalog.name == nameOrNull) return catalog;
                }
                error = $"No UI Token Catalog asset named '{nameOrNull}'.";
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<MolcaUiTokenCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
    }
}
