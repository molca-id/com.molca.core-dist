using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Molca.Editor.UI.Figma;
using Molca.Editor.UI.Build;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        // ── molca_build_ugui ─────────────────────────────────────────────────────────────────
        //
        // Sprint 59: materialize a UI Intent Spec (Sprint 58) into a VR-ready uGUI prefab — controls are
        // the catalog's real prefabs, all appearance comes from the Sprint-57 resolver, layout/VR rules are
        // applied mechanically. Deterministic: no model judgement runs here.

        /// <summary>
        /// The <c>molca_build_ugui</c> tool: validated <see cref="UiIntentSpec"/> → uGUI prefab. Runs the
        /// materializer + layout + VR passes and writes the prefab. Action tool; the write is snapshotted
        /// for revert when it overwrites an existing prefab.
        /// </summary>
        /// <remarks>
        /// Classified <see cref="McpToolReversibility.FileSnapshot"/> (a prefab asset write is not on
        /// Unity's Undo stack — the spec's "UnityUndo" label doesn't apply to asset creation; this mirrors
        /// the codegen/edit tools). A brand-new prefab has no backup — revert by deleting it.
        /// </remarks>
        private static McpToolDefinition CreateBuildUguiTool() => new McpToolDefinition(
            name: "molca_build_ugui",
            description: "Materializes a Molca UI Intent Spec (from molca_figma_to_ui_spec) into a VR-ready "
                       + "uGUI prefab: control nodes instantiate the catalog's real prefabs, all colors/text/"
                       + "backgrounds are written by the token resolver, layout groups + world-space canvas + "
                       + "raycaster + minimum hit targets are applied mechanically, and any '_unmapped' token "
                       + "becomes a visible magenta TODO_ placeholder. Pass 'spec' (the spec JSON object), "
                       + "'outputPath' (an Assets/… .prefab path), optional 'overwrite' (default false), "
                       + "'catalog' (UI Token Catalog name), and 'canvasMode' ('overlay'/'camera' for a "
                       + "normal flat screen UI, or 'world' for VR/diegetic — default 'world'). Deterministic "
                       + "— no model runs here. Returns the "
                       + "prefab path + a build report. It produces a strong first draft for a developer to "
                       + "polish, not a finished screen.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"spec\":{\"type\":\"object\",\"description\":\"The UI Intent Spec JSON (the 'spec' field returned by molca_figma_to_ui_spec).\"}," +
                "\"outputPath\":{\"type\":\"string\",\"description\":\"Project-relative Assets/ path for the prefab ('.prefab' appended if missing).\"}," +
                "\"overwrite\":{\"type\":\"boolean\",\"description\":\"Replace an existing prefab at the path (default false).\"}," +
                "\"catalog\":{\"type\":\"string\",\"description\":\"UI Token Catalog asset name; omit to use the only/first one.\"}," +
                "\"canvasMode\":{\"type\":\"string\",\"enum\":[\"overlay\",\"camera\",\"world\"],\"description\":\"Root canvas mode: 'overlay'/'camera' = flat screen UI; 'world' = VR/diegetic (default).\"}}," +
                "\"required\":[\"spec\",\"outputPath\"],\"additionalProperties\":false}",
            execute: ExecuteBuildUgui,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        private static string ExecuteBuildUgui(string argumentsJson)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Error("Refusing to write a prefab during Play mode; exit Play mode and retry.");

            var args = ParseArgs(argumentsJson);

            var specToken = args["spec"];
            if (specToken == null) return Error("Provide 'spec' (the UI Intent Spec JSON from molca_figma_to_ui_spec).");
            UiIntentSpec spec;
            try { spec = JsonConvert.DeserializeObject<UiIntentSpec>(specToken.ToString()); }
            catch (Exception ex) { return Error($"Invalid spec JSON: {ex.Message}"); }
            if (spec?.root == null) return Error("Spec has no root node.");

            var catalog = ResolveUiTokenCatalog(args.Value<string>("catalog"), out string catalogError);
            if (catalog == null) return Error(catalogError);

            if (!UiIntentSpecValidator.Validate(spec, catalog, out var errors))
                return Error("Spec failed catalog validation: " + string.Join("; ", errors));

            string outputPath = args.Value<string>("outputPath");
            if (string.IsNullOrWhiteSpace(outputPath)) return Error("Provide 'outputPath' (an Assets/… .prefab path).");
            bool overwrite = args.Value<bool?>("overwrite") ?? false;
            var canvasMode = ParseCanvasMode(args.Value<string>("canvasMode"));

            var materializer = new UguiMaterializer();
            var root = materializer.Build(spec, catalog, out var report);
            if (root == null) return Error("Materialization produced no root GameObject.");

            try
            {
                UguiLayoutPass.Apply(materializer.Bindings);
                UguiVrPass.Apply(root, spec, catalog, materializer.Bindings, report.Notes, canvasMode);

                string normalized = outputPath.Replace('\\', '/');
                if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) normalized += ".prefab";
                string undoId = System.IO.File.Exists(normalized)
                    ? McpUndoStack.Snapshot(normalized, "molca_build_ugui", $"build {normalized}")
                    : null;

                string written = UguiPrefabWriter.Write(root, outputPath, overwrite, out string writeError);
                if (written == null)
                {
                    if (undoId != null) McpUndoStack.Discard(undoId);
                    return Error(writeError);
                }
                AssetDatabase.ImportAsset(written, ImportAssetOptions.ForceSynchronousImport);

                var notes = new JArray();
                foreach (var note in report.Notes) notes.Add(note);

                return new JObject
                {
                    ["prefab"] = written,
                    ["undoId"] = undoId,
                    ["nodesBuilt"] = report.NodesBuilt,
                    ["prefabsInstantiated"] = report.PrefabsInstantiated,
                    ["primitivesBuilt"] = report.PrimitivesBuilt,
                    ["unmappedPlaceholders"] = report.UnmappedPlaceholders,
                    ["notes"] = notes,
                    ["note"] = report.UnmappedPlaceholders > 0
                        ? "Built with TODO_ placeholders for unmapped tokens — review each before use."
                        : "Built. A first-draft uGUI prefab — review layout/VR sizing before shipping."
                }.ToString(Formatting.None);
            }
            finally
            {
                // The prefab asset is saved; drop the transient scene instance.
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static UguiCanvasMode ParseCanvasMode(string value) => value?.ToLowerInvariant() switch
        {
            "overlay" => UguiCanvasMode.Overlay,
            "camera" => UguiCanvasMode.Camera,
            _ => UguiCanvasMode.World, // default preserves VR behavior
        };
    }
}
