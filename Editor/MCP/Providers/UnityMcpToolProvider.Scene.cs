using System;
using System.Linq;
using Molca.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only Unity scene, hierarchy, and editor-selection discovery tools.
    /// </summary>
    public sealed partial class UnityMcpToolProvider
    {
        private static McpToolDefinition CreateSceneObjectsTool() => new McpToolDefinition(
            name: "molca_unity_scene_objects",
            description: "Lists GameObjects in the loaded scene(s) with their hierarchy path, active state, "
                       + "instance id, component type names, and (for Steps) their auxiliary type names. This "
                       + "is the discovery primitive: filter to find the objects you need, then act on the "
                       + "returned path/instanceId with any action tool. Filters (all case-insensitive, "
                       + "combinable): 'nameContains' by name; 'componentType' by attached component type (e.g. "
                       + "'Light', 'Rigidbody', 'MeshRenderer'); 'auxiliaryType' by a Step's serialized "
                       + "auxiliary type (e.g. 'Hint', 'Timer') — auxiliaries are SerializeReference data on a "
                       + "Step, not components, so 'componentType' will not find them. 'limit' caps results "
                       + "(default 200). Prefer a filter over dumping the whole scene.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"nameContains\":{\"type\":\"string\",\"description\":\"Case-insensitive name substring filter.\"}," +
                "\"componentType\":{\"type\":\"string\",\"description\":\"Case-insensitive attached-component type-name substring filter (e.g. 'Light', 'Rigidbody').\"}," +
                "\"auxiliaryType\":{\"type\":\"string\",\"description\":\"Case-insensitive filter matching a Step's serialized auxiliary type name (e.g. 'Hint', 'Timer'). Use this, not componentType, to find Steps by auxiliary.\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max entries to return (default 200).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteSceneObjects,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteSceneObjects(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var filter = args.Value<string>("nameContains");
            var componentFilter = args.Value<string>("componentType");
            var auxiliaryFilter = args.Value<string>("auxiliaryType");
            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 200;

            var entries = new JArray();
            var truncated = false;
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                if (truncated) break;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    if (!string.IsNullOrEmpty(filter) &&
                        go.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var componentNames = go.GetComponents<Component>()
                        .Where(c => c != null).Select(c => c.GetType().Name).ToArray();

                    // Filter by attached component type name (substring, case-insensitive) so the model can
                    // find "the lights" / "objects with a Rigidbody" without dumping the whole scene.
                    if (!string.IsNullOrEmpty(componentFilter) &&
                        !componentNames.Any(n => n.IndexOf(componentFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    // A Step's auxiliaries are SerializeReference data, not components, so they must be read
                    // off the Step directly. Only Steps have them; null entries (broken SerializeReference)
                    // are skipped so a missing script doesn't crash discovery.
                    var step = go.GetComponent<Molca.Sequence.Step>();
                    var auxiliaryNames = step?.Auxiliaries
                        .Where(a => a != null).Select(a => a.GetType().Name).ToArray();

                    if (!string.IsNullOrEmpty(auxiliaryFilter) &&
                        (auxiliaryNames == null ||
                         !auxiliaryNames.Any(n => n.IndexOf(auxiliaryFilter, StringComparison.OrdinalIgnoreCase) >= 0)))
                        continue;

                    if (entries.Count >= limit) { truncated = true; break; }

                    var entry = new JObject
                    {
                        ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                        ["name"] = go.name,
                        ["active"] = go.activeSelf,
                        ["instanceId"] = go.GetInstanceID(),
                        ["components"] = new JArray(componentNames)
                    };
                    // Surface auxiliaries only for Steps that have them, so the model can see what to act on
                    // without a second per-object lookup, while non-Step entries stay unchanged.
                    if (auxiliaryNames != null && auxiliaryNames.Length > 0)
                        entry["auxiliaries"] = new JArray(auxiliaryNames);
                    entries.Add(entry);
                }
            }

            return new JObject
            {
                ["count"] = entries.Count,
                ["truncated"] = truncated,
                ["objects"] = entries
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateSelectionTool() => new McpToolDefinition(
            name: "molca_unity_selection",
            description: "Reports the current Unity editor selection: GameObjects/components with hierarchy "
                       + "paths and assets with AssetDatabase paths.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteSelection,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteSelection(string argumentsJson)
        {
            var entries = new JArray();
            foreach (var obj in Selection.objects)
            {
                if (obj == null) continue;
                var item = new JObject
                {
                    ["name"] = obj.name,
                    ["type"] = obj.GetType().Name,
                    ["instanceId"] = obj.GetInstanceID()
                };

                var path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path))
                {
                    item["assetPath"] = path;
                    item["assetGuid"] = AssetDatabase.AssetPathToGUID(path);
                    item["isMainAsset"] = AssetDatabase.IsMainAsset(obj);
                    item["isSubAsset"] = AssetDatabase.IsSubAsset(obj);
                    item["globalObjectId"] = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
                }
                if (obj is GameObject go) item["hierarchyPath"] = GameObjectEditingService.GetHierarchyPath(go);
                else if (obj is Component c && c.gameObject != null)
                {
                    item["hierarchyPath"] = GameObjectEditingService.GetHierarchyPath(c.gameObject);
                    item["componentType"] = c.GetType().FullName;
                }
                else if (obj is Material material)
                {
                    item["shader"] = material.shader != null ? material.shader.name : null;
                    item["materialHint"] = "Use molca_unity_material with this instanceId, globalObjectId, or name to inspect color properties.";
                }

                entries.Add(item);
            }

            return new JObject
            {
                ["count"] = entries.Count,
                ["activeInstanceId"] = Selection.activeObject != null ? Selection.activeObject.GetInstanceID() : 0,
                ["objects"] = entries
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateScenesTool() => new McpToolDefinition(
            name: "molca_unity_scenes",
            description: "Lists loaded Unity scenes with active/loaded/dirty state, path, build index, and "
                       + "root GameObject count.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteScenes,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteScenes(string argumentsJson)
        {
            var active = SceneManager.GetActiveScene();
            var scenes = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["isLoaded"] = scene.isLoaded,
                    ["isDirty"] = scene.isDirty,
                    ["isActive"] = scene == active,
                    ["buildIndex"] = scene.buildIndex,
                    ["rootCount"] = scene.isLoaded ? scene.rootCount : 0
                });
            }

            return new JObject
            {
                ["count"] = scenes.Count,
                ["activeScene"] = active.name,
                ["scenes"] = scenes
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateBuildScenesTool() => new McpToolDefinition(
            name: "molca_unity_build_scenes",
            description: "Lists scenes configured in Unity EditorBuildSettings with path, enabled state, GUID, "
                       + "and build index.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteBuildScenes,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteBuildScenes(string argumentsJson)
        {
            var scenes = new JArray();
            for (var i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                var scene = EditorBuildSettings.scenes[i];
                scenes.Add(new JObject
                {
                    ["index"] = i,
                    ["path"] = scene.path,
                    ["enabled"] = scene.enabled,
                    ["guid"] = scene.guid.ToString(),
                    ["assetExists"] = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) != null
                });
            }

            return new JObject
            {
                ["count"] = scenes.Count,
                ["scenes"] = scenes
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateBuildScenesSetTool() => new McpToolDefinition(
            name: "molca_unity_build_scenes_set",
            description: "Modifies EditorBuildSettings scenes: 'add' (append a scene path if absent), 'remove' "
                       + "(drop a scene path), or 'setEnabled' (toggle a scene's enabled flag). Pass 'path' and, "
                       + "for setEnabled, 'enabled'. Writes the project build settings (not Unity-Undo reversible; "
                       + "the settings file is snapshotted for revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"action\":{\"type\":\"string\",\"description\":\"add | remove | setEnabled.\"}," +
                "\"path\":{\"type\":\"string\",\"description\":\"Scene asset path.\"}," +
                "\"enabled\":{\"type\":\"boolean\",\"description\":\"Enabled flag for setEnabled.\"}}," +
                "\"required\":[\"action\",\"path\"],\"additionalProperties\":false}",
            execute: ExecuteBuildScenesSet,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.FileSnapshot);

        private static McpToolDefinition CreateBuildTargetSwitchTool() => new McpToolDefinition(
            name: "molca_unity_build_target_switch",
            description: "Switches the active build target. 'target' is a BuildTarget name (e.g. StandaloneWindows64, "
                       + "Android, iOS, WebGL). This is a long, irreversible operation that reimports assets.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"BuildTarget name to switch to.\"}}," +
                "\"required\":[\"target\"],\"additionalProperties\":false}",
            execute: ExecuteBuildTargetSwitch,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteBuildScenesSet(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var action = args.Value<string>("action");
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");

            var undoId = McpUndoStack.Snapshot("ProjectSettings/EditorBuildSettings.asset",
                "molca_unity_build_scenes_set", $"Build scenes {action} ({System.IO.Path.GetFileName(path)})");

            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            var index = scenes.FindIndex(s => string.Equals(s.path, path, StringComparison.OrdinalIgnoreCase));

            switch (action)
            {
                case "add":
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                        return Error($"no scene asset at '{path}'.");
                    if (index < 0) scenes.Add(new EditorBuildSettingsScene(path, true));
                    break;
                case "remove":
                    if (index < 0) return Error($"'{path}' is not in the build scenes.");
                    scenes.RemoveAt(index);
                    break;
                case "setEnabled":
                    if (index < 0) return Error($"'{path}' is not in the build scenes.");
                    if (args["enabled"] == null) return Error("'enabled' is required for setEnabled.");
                    scenes[index] = new EditorBuildSettingsScene(scenes[index].path, args.Value<bool>("enabled"));
                    break;
                default:
                    return Error("'action' must be add, remove, or setEnabled.");
            }

            EditorBuildSettings.scenes = scenes.ToArray();

            return new JObject
            {
                ["action"] = action,
                ["path"] = path,
                ["sceneCount"] = scenes.Count,
                ["revertible"] = undoId != null
            }.ToString(Formatting.None);
        }

        private static string ExecuteBuildTargetSwitch(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var targetArg = args.Value<string>("target");
            if (string.IsNullOrWhiteSpace(targetArg)) return Error("'target' is required.");
            if (!Enum.TryParse<BuildTarget>(targetArg, true, out var target))
                return Error($"'{targetArg}' is not a valid BuildTarget.");

            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (EditorUserBuildSettings.activeBuildTarget == target)
                return new JObject { ["activeBuildTarget"] = target.ToString(), ["changed"] = false }
                    .ToString(Formatting.None);

            var ok = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            return new JObject
            {
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["requestedTarget"] = target.ToString(),
                ["changed"] = ok
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateSceneSetActiveTool() => new McpToolDefinition(
            name: "molca_unity_scene_set_active",
            description: "Sets the active loaded scene by name, path, or loaded-scene index. This changes "
                       + "editor scene focus only; it does not save files.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"scene\":{\"type\":\"string\",\"description\":\"Loaded scene name or path.\"}," +
                "\"index\":{\"type\":\"integer\",\"description\":\"Loaded scene index from molca_unity_scenes.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteSceneSetActive,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteSceneSetActive(string argumentsJson)
        {
            var scene = ResolveLoadedScene(ParseArgs(argumentsJson), out var error);
            if (!scene.IsValid()) return Error(error);
            if (!scene.isLoaded) return Error($"scene '{scene.path}' is not loaded.");

            var active = EditorSceneManager.GetActiveScene();
            if (scene != active && !EditorSceneManager.SetActiveScene(scene))
                return Error($"failed to set active scene '{SceneLabel(scene)}'.");

            return new JObject
            {
                ["activeScene"] = scene.name,
                ["path"] = scene.path,
                ["loadedIndex"] = LoadedSceneIndex(scene)
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateSceneSaveTool() => new McpToolDefinition(
            name: "molca_unity_scene_save",
            description: "Saves a loaded scene by name/path/index, or all loaded scenes with all=true. "
                       + "This writes scene files and is not Unity-Undo reversible.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"scene\":{\"type\":\"string\",\"description\":\"Loaded scene name or path; omit for active scene.\"}," +
                "\"index\":{\"type\":\"integer\",\"description\":\"Loaded scene index from molca_unity_scenes.\"}," +
                "\"all\":{\"type\":\"boolean\",\"description\":\"Save all loaded scenes.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteSceneSave,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteSceneSave(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (args.Value<bool?>("all") == true)
            {
                if (!EditorSceneManager.SaveOpenScenes()) return Error("failed to save one or more loaded scenes.");
                return new JObject { ["saved"] = "all", ["count"] = EditorSceneManager.sceneCount }
                    .ToString(Formatting.None);
            }

            var scene = ResolveLoadedScene(args, out var error, defaultToActive: true);
            if (!scene.IsValid()) return Error(error);
            if (string.IsNullOrEmpty(scene.path))
                return Error("cannot save an untitled scene without a path.");
            if (!EditorSceneManager.SaveScene(scene))
                return Error($"failed to save scene '{SceneLabel(scene)}'.");

            return new JObject
            {
                ["saved"] = scene.name,
                ["path"] = scene.path,
                ["isDirty"] = scene.isDirty
            }.ToString(Formatting.None);
        }

        private static McpToolDefinition CreateSceneOpenTool() => new McpToolDefinition(
            name: "molca_unity_scene_open",
            description: "Opens a scene asset by path in Single or Additive mode. Single mode refuses dirty "
                       + "loaded scenes unless saveDirtyScenes=true.",
            inputSchemaJson:
                "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Scene asset path.\"}," +
                "\"mode\":{\"type\":\"string\",\"description\":\"Single or Additive (default Additive).\"}," +
                "\"saveDirtyScenes\":{\"type\":\"boolean\",\"description\":\"Save dirty loaded scenes before Single open.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteSceneOpen,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteSceneOpen(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                return Error($"scene asset not found at '{path}'.");

            var modeArg = args.Value<string>("mode");
            var mode = OpenSceneMode.Additive;
            if (!string.IsNullOrWhiteSpace(modeArg) && !Enum.TryParse(modeArg, true, out mode))
                return Error("'mode' must be Single or Additive.");

            if (mode == OpenSceneMode.Single && AnyLoadedSceneDirty())
            {
                if (args.Value<bool?>("saveDirtyScenes") != true)
                    return Error("dirty loaded scenes exist; pass saveDirtyScenes=true before opening in Single mode.");
                if (!EditorSceneManager.SaveOpenScenes()) return Error("failed to save dirty loaded scenes.");
            }

            var scene = EditorSceneManager.OpenScene(path, mode);
            return new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["mode"] = mode.ToString(),
                ["isActive"] = scene == EditorSceneManager.GetActiveScene()
            }.ToString(Formatting.None);
        }

        private static Scene ResolveLoadedScene(JObject args, out string error, bool defaultToActive = false)
        {
            error = null;
            if (args["index"] != null)
            {
                var index = args.Value<int>("index");
                if (index < 0 || index >= EditorSceneManager.sceneCount)
                {
                    error = $"index {index} is out of range (0..{EditorSceneManager.sceneCount - 1}).";
                    return default;
                }
                return EditorSceneManager.GetSceneAt(index);
            }

            var sceneArg = args.Value<string>("scene");
            if (string.IsNullOrWhiteSpace(sceneArg))
            {
                if (defaultToActive) return EditorSceneManager.GetActiveScene();
                error = "provide 'scene' or 'index'.";
                return default;
            }

            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (string.Equals(scene.path, sceneArg, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(scene.name, sceneArg, StringComparison.OrdinalIgnoreCase))
                    return scene;
            }

            error = $"no loaded scene named or pathed '{sceneArg}'.";
            return default;
        }

        private static bool AnyLoadedSceneDirty()
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty) return true;
            }
            return false;
        }

        private static int LoadedSceneIndex(Scene target)
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i) == target) return i;
            }
            return -1;
        }

        private static string SceneLabel(Scene scene)
            => string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;
    }
}
