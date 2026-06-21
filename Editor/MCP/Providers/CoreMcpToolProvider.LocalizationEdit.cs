using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using Molca.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Localization authoring actions (Sprint 41 v2): write a <see cref="DynamicLocalization"/>
    /// translation, add a language to a <see cref="LocalizationModule"/>, and switch the active locale
    /// at runtime. Complements the read-only <c>molca_localization_*</c> introspection family.
    /// </summary>
    /// <remarks>
    /// Edit-mode tools mutate through Unity's Undo stack (plain Ctrl+Z reverts) and dirty the owning
    /// scene/asset; the runtime locale switch is Play-mode only and not undoable. Discovered by
    /// convention via the <c>Create*Tool</c> factories.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateLocalizationSetTextTool() => new McpToolDefinition(
            name: "molca_localization_set_text",
            description: "Sets (or adds) a translation on a DynamicLocalization field for a given language "
                       + "code. Resolve the owning GameObject by hierarchy path or instance id; 'field' is "
                       + "the property path reported by molca_localization_coverage (optional when the "
                       + "GameObject has exactly one DynamicLocalization). One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id owning the DynamicLocalization.\"}," +
                "\"field\":{\"type\":\"string\",\"description\":\"Serialized property path of the DynamicLocalization (from molca_localization_coverage). Optional when only one exists on the target.\"}," +
                "\"languageCode\":{\"type\":\"string\",\"description\":\"BCP-47 code, e.g. \\\"en\\\".\"}," +
                "\"text\":{\"type\":\"string\"}}," +
                "\"required\":[\"target\",\"languageCode\",\"text\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationSetText,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateLocalizationAddLanguageTool() => new McpToolDefinition(
            name: "molca_localization_add_language",
            description: "Adds a language entry (code + optional display name) to a LocalizationModule asset. "
                       + "'modulePath' is optional when the project has exactly one module. Rejects a code "
                       + "the module already defines. One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"code\":{\"type\":\"string\",\"description\":\"BCP-47 code to add, e.g. \\\"id\\\".\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Optional display name; defaults to the code.\"}," +
                "\"modulePath\":{\"type\":\"string\",\"description\":\"Asset path of the target LocalizationModule. Optional when only one exists.\"}}," +
                "\"required\":[\"code\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationAddLanguage,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateLocalizationSetLanguageTool() => new McpToolDefinition(
            name: "molca_localization_set_language",
            description: "Switches the active runtime locale via LocalizationManager (Play mode only). "
                       + "Validates the code against the registered locales. Not undoable.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"code\":{\"type\":\"string\",\"description\":\"BCP-47 code of the locale to activate.\"}}," +
                "\"required\":[\"code\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationSetLanguage,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteLocalizationSetText(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var languageCode = args.Value<string>("languageCode");
            if (string.IsNullOrEmpty(languageCode))
                return Error("'languageCode' is required and must not be blank (blank codes are unmatchable at runtime).");
            var text = args.Value<string>("text") ?? string.Empty;

            var go = GameObjectEditingService.Resolve(args.Value<string>("target"), out var error);
            if (go == null) return Error(error);

            // Collect every DynamicLocalization across the GameObject's components. Each candidate keeps
            // its own SerializedObject so ApplyModifiedProperties (and the Undo it records) targets the
            // right component.
            var candidates = new List<(SerializedObject so, SerializedProperty prop, string component, string path)>();
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var so = new SerializedObject(mb);
                var it = so.GetIterator();
                bool enter = true;
                while (it.Next(enter))
                {
                    enter = true;
                    if (it.propertyType != SerializedPropertyType.Generic) continue;
                    var translations = it.FindPropertyRelative("translations");
                    var useLocalized = it.FindPropertyRelative("useLocalizedString");
                    if (translations == null || !translations.isArray || useLocalized == null) continue;
                    enter = false; // it's a DynamicLocalization; don't descend
                    candidates.Add((so, it.Copy(), mb.GetType().Name, it.propertyPath));
                }
            }

            if (candidates.Count == 0)
                return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no DynamicLocalization field.");

            var fieldPath = args.Value<string>("field");
            (SerializedObject so, SerializedProperty prop, string component, string path) chosen;
            if (!string.IsNullOrEmpty(fieldPath))
            {
                var matches = candidates.Where(c => c.path == fieldPath).ToList();
                if (matches.Count == 0)
                    return Error($"No DynamicLocalization with field path '{fieldPath}' on the target. "
                               + $"Available: {string.Join(", ", candidates.Select(c => $"{c.component}.{c.path}"))}.");
                chosen = matches[0];
            }
            else if (candidates.Count == 1)
            {
                chosen = candidates[0];
            }
            else
            {
                return Error("Target has multiple DynamicLocalization fields; pass 'field'. "
                           + $"Candidates: {string.Join(", ", candidates.Select(c => $"{c.component}.{c.path}"))}.");
            }

            if (chosen.prop.FindPropertyRelative("useLocalizedString").boolValue)
                return Error("This DynamicLocalization uses a LocalizedString (useLocalizedString=true); "
                           + "the translations list is not used. Edit the LocalizedString asset instead.");

            var translationsProp = chosen.prop.FindPropertyRelative("translations");
            SerializedProperty entry = null;
            for (int i = 0; i < translationsProp.arraySize; i++)
            {
                var el = translationsProp.GetArrayElementAtIndex(i);
                if (el.FindPropertyRelative("languageCode")?.stringValue == languageCode)
                {
                    entry = el;
                    break;
                }
            }

            bool added = entry == null;
            if (added)
            {
                int idx = translationsProp.arraySize;
                translationsProp.InsertArrayElementAtIndex(idx);
                entry = translationsProp.GetArrayElementAtIndex(idx);
                entry.FindPropertyRelative("languageCode").stringValue = languageCode;
            }
            entry.FindPropertyRelative("text").stringValue = text;

            // ApplyModifiedProperties records the change on the Unity Undo stack and dirties the object;
            // mark the scene dirty too so the edit is persisted on save.
            chosen.so.ApplyModifiedProperties();
            if (!go.scene.IsValid() || !go.scene.isLoaded)
                EditorUtility.SetDirty(go);
            else
                EditorSceneManager.MarkSceneDirty(go.scene);

            return new JObject
            {
                ["path"] = GameObjectEditingService.GetHierarchyPath(go),
                ["component"] = chosen.component,
                ["field"] = chosen.path,
                ["languageCode"] = languageCode,
                ["added"] = added
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationAddLanguage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var code = args.Value<string>("code");
            if (string.IsNullOrEmpty(code))
                return Error("'code' is required and must not be blank.");
            var name = args.Value<string>("name");
            if (string.IsNullOrEmpty(name)) name = code;

            var modules = FindLocalizationModules();
            if (modules.Count == 0)
                return Error("No LocalizationModule asset found in the project.");

            LocalizationModule module;
            var modulePath = args.Value<string>("modulePath");
            if (!string.IsNullOrEmpty(modulePath))
            {
                module = modules.FirstOrDefault(m => AssetDatabase.GetAssetPath(m) == modulePath);
                if (module == null)
                    return Error($"No LocalizationModule at '{modulePath}'. "
                               + $"Available: {string.Join(", ", modules.Select(AssetDatabase.GetAssetPath))}.");
            }
            else if (modules.Count == 1)
            {
                module = modules[0];
            }
            else
            {
                return Error("Multiple LocalizationModule assets exist; pass 'modulePath'. "
                           + $"Available: {string.Join(", ", modules.Select(AssetDatabase.GetAssetPath))}.");
            }

            if (module.Languages != null && module.Languages.Any(l => l.Code == code))
                return Error($"Language code '{code}' is already defined in '{AssetDatabase.GetAssetPath(module)}'.");

            Undo.RecordObject(module, $"MCP Add Language {code}");
            var list = new List<LocalizationModule.LanguageEntry>(module.Languages ?? System.Array.Empty<LocalizationModule.LanguageEntry>())
            {
                new LocalizationModule.LanguageEntry { Code = code, Name = name }
            };
            module.Languages = list.ToArray();
            EditorUtility.SetDirty(module);
            AssetDatabase.SaveAssetIfDirty(module);

            return new JObject
            {
                ["assetPath"] = AssetDatabase.GetAssetPath(module),
                ["code"] = code,
                ["name"] = name,
                ["languageCount"] = module.Languages.Length
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationSetLanguage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var code = args.Value<string>("code");
            if (string.IsNullOrEmpty(code))
                return Error("'code' is required.");

            var manager = RuntimeManager.GetSubsystem<LocalizationManager>();
            if (manager == null)
                return Error("LocalizationManager is not available (is the app running and bootstrapped?).");

            if (!manager.HasLanguage(code))
                return Error($"'{code}' is not a registered locale. Available: "
                           + $"{string.Join(", ", manager.GetAvailableLanguages())}.");

            LocalizationManager.SetLanguage(code);

            return new JObject
            {
                ["currentLanguage"] = LocalizationManager.CurrentLanguage,
                ["available"] = new JArray(manager.GetAvailableLanguages().Cast<object>().ToArray())
            }.ToString(Formatting.None);
        }
    }
}
