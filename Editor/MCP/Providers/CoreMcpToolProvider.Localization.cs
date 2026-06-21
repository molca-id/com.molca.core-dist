using System.Collections.Generic;
using System.Linq;
using Molca.Editor;
using Molca.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Read-only localization introspection tools (Sprint 41). Surfaces the project's
    /// <see cref="LocalizationModule"/> configuration, the <see cref="LocalizedText"/> components in the
    /// loaded scene(s), and a translation-coverage report over every <see cref="DynamicLocalization"/>
    /// found in those scenes — so the assistant can answer "what languages are configured", "which texts
    /// are localized", and "what still needs translating" before any authoring action.
    /// </summary>
    /// <remarks>
    /// Read-only; main thread only (driven by the MCP bridge). The coverage scan is limited to
    /// <em>loaded</em> scenes so the synchronous tool stays fast; full prefab/ScriptableObject auditing is
    /// the Doctor's job (see <c>DynamicLocalizationLocaleValidityCheck</c>). Edit/play-mode authoring tools
    /// (set-text, add-language, set-language) are intentionally deferred to a follow-up sprint.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateLocalizationStatusTool() => new McpToolDefinition(
            name: "molca_localization_status",
            description: "Reports localization configuration: every LocalizationModule asset with its "
                       + "supported languages (code, name, whether a flag sprite is assigned), and — in "
                       + "Play mode — the active, current, and default language reported by "
                       + "LocalizationManager. The orientation tool to call before other localization tools.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteLocalizationStatus,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateLocalizationListTextsTool() => new McpToolDefinition(
            name: "molca_localization_list_texts",
            description: "Lists LocalizedText components in the loaded scene(s) with hierarchy path, instance "
                       + "id, whether a style is assigned, and the assigned LocalizedString's table / entry "
                       + "reference (flagging empty ones). Optional 'pathFilter' substring narrows results.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"pathFilter\":{\"type\":\"string\",\"description\":\"Case-insensitive substring; only components whose hierarchy path contains it are returned.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteLocalizationListTexts,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateLocalizationCoverageTool() => new McpToolDefinition(
            name: "molca_localization_coverage",
            description: "Translation-gap report over every DynamicLocalization in the loaded scene(s): for "
                       + "each, which LocalizationModule-defined languages have no (or blank) text, plus a "
                       + "count of rows with blank/unknown language codes. Scans loaded scenes only; use the "
                       + "Doctor for full prefab/ScriptableObject auditing.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteLocalizationCoverage,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        /// <summary>Loads every <see cref="LocalizationModule"/> asset in the project.</summary>
        private static List<LocalizationModule> FindLocalizationModules() =>
            AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationModule>)
                .Where(m => m != null)
                .ToList();

        private static string ExecuteLocalizationStatus(string argumentsJson)
        {
            var modulesArr = new JArray();
            var allCodes = new HashSet<string>();

            foreach (var module in FindLocalizationModules())
            {
                var languages = new JArray();
                if (module.Languages != null)
                {
                    foreach (var lang in module.Languages)
                    {
                        if (!string.IsNullOrEmpty(lang.Code))
                            allCodes.Add(lang.Code);
                        languages.Add(new JObject
                        {
                            ["code"] = lang.Code,
                            ["name"] = lang.Name,
                            ["flagAssigned"] = lang.Flag != null
                        });
                    }
                }

                modulesArr.Add(new JObject
                {
                    ["assetPath"] = AssetDatabase.GetAssetPath(module),
                    ["languageCount"] = languages.Count,
                    ["languages"] = languages
                });
            }

            var result = new JObject
            {
                ["moduleCount"] = modulesArr.Count,
                ["modules"] = modulesArr,
                ["definedLanguageCodes"] = new JArray(allCodes.OrderBy(c => c).Cast<object>().ToArray()),
                ["isPlaying"] = Application.isPlaying
            };

            // LocalizationManager statics only carry meaningful runtime state in Play mode.
            if (Application.isPlaying)
            {
                result["currentLanguage"] = LocalizationManager.CurrentLanguage;
                result["defaultLanguage"] = LocalizationManager.DefaultLanguageCode;
            }

            return result.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationListTexts(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var pathFilter = args.Value<string>("pathFilter");

            var texts = new JArray();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var lt in root.GetComponentsInChildren<LocalizedText>(true))
                {
                    var path = GameObjectEditingService.GetHierarchyPath(lt.gameObject);
                    if (!string.IsNullOrEmpty(pathFilter) &&
                        path.IndexOf(pathFilter, System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var localized = lt.GetLocalizedString();
                    var isEmpty = localized == null || localized.IsEmpty;

                    var entry = new JObject
                    {
                        ["path"] = path,
                        ["instanceId"] = lt.GetInstanceID(),
                        ["styleAssigned"] = HasStyleAssigned(lt),
                        ["localizedStringEmpty"] = isEmpty
                    };

                    // TableReference / TableEntryReference ToString gives the human-readable
                    // collection name and key; guard in case an unassigned reference throws.
                    if (!isEmpty)
                    {
                        try { entry["table"] = localized.TableReference.ToString(); } catch { /* unresolved */ }
                        try { entry["entry"] = localized.TableEntryReference.ToString(); } catch { /* unresolved */ }
                    }

                    texts.Add(entry);
                }
            }

            return new JObject
            {
                ["count"] = texts.Count,
                ["texts"] = texts
            }.ToString(Formatting.None);
        }

        /// <summary>
        /// Reads the protected serialized <c>styleInfo</c> field of a <see cref="LocalizedText"/> without
        /// exposing it — purely for reporting whether a style asset is wired up.
        /// </summary>
        private static bool HasStyleAssigned(LocalizedText text)
        {
            var serialized = new SerializedObject(text);
            var prop = serialized.FindProperty("styleInfo");
            return prop != null && prop.objectReferenceValue != null;
        }

        private static string ExecuteLocalizationCoverage(string argumentsJson)
        {
            // Union of every module's defined codes: a project may ship more than one module, so a code
            // counts as defined when any module declares it (mirrors the Doctor's validity check).
            var definedCodes = new HashSet<string>();
            foreach (var module in FindLocalizationModules())
                foreach (var code in module.LanguageCode)
                    if (!string.IsNullOrEmpty(code))
                        definedCodes.Add(code);

            var entries = new JArray();
            int blankCodeRows = 0;
            int fullyCovered = 0;

            foreach (var root in GameObjectEditingService.EnumerateRoots())
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;

                    var serialized = new SerializedObject(mb);
                    var property = serialized.GetIterator();
                    bool enterChildren = true;
                    while (property.Next(enterChildren))
                    {
                        enterChildren = true;
                        if (property.propertyType != SerializedPropertyType.Generic)
                            continue;

                        // DynamicLocalization serializes as a generic struct with a `translations`
                        // array plus the `useLocalizedString` flag (same shape the Doctor keys on).
                        var translations = property.FindPropertyRelative("translations");
                        var useLocalized = property.FindPropertyRelative("useLocalizedString");
                        if (translations == null || !translations.isArray || useLocalized == null)
                            continue;

                        enterChildren = false; // it's a DynamicLocalization; don't descend into it
                        if (useLocalized.boolValue)
                            continue; // LocalizedString mode does not use the translations list

                        var present = new HashSet<string>();
                        for (int i = 0; i < translations.arraySize; i++)
                        {
                            var row = translations.GetArrayElementAtIndex(i);
                            var code = row.FindPropertyRelative("languageCode")?.stringValue;
                            var text = row.FindPropertyRelative("text")?.stringValue;
                            if (string.IsNullOrEmpty(code))
                            {
                                blankCodeRows++;
                                continue;
                            }
                            if (!string.IsNullOrEmpty(text))
                                present.Add(code);
                        }

                        var missing = definedCodes.Where(c => !present.Contains(c))
                                                  .OrderBy(c => c)
                                                  .Cast<object>()
                                                  .ToArray();
                        if (missing.Length == 0)
                            fullyCovered++;

                        entries.Add(new JObject
                        {
                            ["path"] = GameObjectEditingService.GetHierarchyPath(mb.gameObject),
                            ["component"] = mb.GetType().Name,
                            ["field"] = property.propertyPath,
                            ["missingLanguages"] = new JArray(missing)
                        });
                    }
                }
            }

            return new JObject
            {
                ["definedLanguageCodes"] = new JArray(definedCodes.OrderBy(c => c).Cast<object>().ToArray()),
                ["dynamicLocalizationCount"] = entries.Count,
                ["fullyCovered"] = fullyCovered,
                ["blankCodeRows"] = blankCodeRows,
                ["entries"] = entries
            }.ToString(Formatting.None);
        }
    }
}
