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
            description: "Sets (or adds) an inline translation on a LocalizedValue field for a given language "
                       + "code. Resolve the owning GameObject by hierarchy path or instance id; 'field' is "
                       + "the property path reported by molca_localization_coverage (optional when the "
                       + "GameObject has exactly one DynamicLocalization). One Unity Undo group.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"target\":{\"type\":\"string\",\"description\":\"GameObject hierarchy path or instance id owning the LocalizedValue.\"}," +
                "\"field\":{\"type\":\"string\",\"description\":\"Serialized property path of the LocalizedValue (from molca_localization_coverage). Optional when only one exists on the target.\"}," +
                "\"languageCode\":{\"type\":\"string\",\"description\":\"BCP-47 code, e.g. \\\"en\\\".\"}," +
                "\"text\":{\"type\":\"string\"}}," +
                "\"required\":[\"target\",\"languageCode\",\"text\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationSetText,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateLocalizationAddLanguageTool() => new McpToolDefinition(
            name: "molca_localization_add_language",
            description: "Executes a previously previewed add-or-repair locale transaction. The plan "
                       + "atomically updates the Molca module, Unity Locale registry, Addressables, and "
                       + "every String/Asset Table collection. Refuses stale catalog fingerprints and "
                       + "rolls back partial changes on failure. Use molca_localization_plan_add_language first.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"planId\":{\"type\":\"string\",\"description\":\"Opaque plan id returned by molca_localization_plan_add_language.\"}}," +
                "\"required\":[\"planId\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationAddLanguage,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static McpToolDefinition CreateLocalizationPlanAddLanguageTool() => new McpToolDefinition(
            name: "molca_localization_plan_add_language",
            description: "Previews an add-or-repair locale transaction without changing the project. "
                       + "Returns explicit mutations, warnings, errors, and a catalog-bound plan id. "
                       + "'modulePath' is optional only when the project has exactly one module.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"code\":{\"type\":\"string\",\"description\":\"BCP-47 code to add or repair, e.g. \\\"id\\\".\"}," +
                "\"name\":{\"type\":\"string\",\"description\":\"Optional Molca display name; defaults to the canonical code.\"}," +
                "\"modulePath\":{\"type\":\"string\",\"description\":\"Asset path of the target LocalizationModule. Optional when only one exists.\"}}," +
                "\"required\":[\"code\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationPlanAddLanguage);

        private static McpToolDefinition CreateLocalizationPlanArchiveLanguageTool() => new McpToolDefinition(
            name: "molca_localization_plan_archive_language",
            description: "Previews non-destructive locale removal without changing the project. The plan "
                       + "disables the Molca row, Unity registration, tables, and Addressables while "
                       + "preserving Locale/table assets and inline rows for restore or explicit deletion.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"code\":{\"type\":\"string\",\"description\":\"Configured BCP-47 code to archive.\"}," +
                "\"modulePath\":{\"type\":\"string\",\"description\":\"Asset path of the target LocalizationModule. Optional when only one exists.\"}}," +
                "\"required\":[\"code\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationPlanArchiveLanguage);

        private static McpToolDefinition CreateLocalizationArchiveLanguageTool() => new McpToolDefinition(
            name: "molca_localization_archive_language",
            description: "Executes a fresh non-destructive locale archive plan. Preserves authored assets "
                       + "and inline values, verifies postconditions, and applies as one Unity Undo group. "
                       + "Use molca_localization_plan_archive_language first.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"planId\":{\"type\":\"string\",\"description\":\"Opaque plan id returned by molca_localization_plan_archive_language.\"}}," +
                "\"required\":[\"planId\"],\"additionalProperties\":false}",
            execute: ExecuteLocalizationArchiveLanguage,
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
                    if (!LocalizedValueSerializedUtility.TryDescribe(it, out _)) continue;
                    enter = false;
                    candidates.Add((so, it.Copy(), mb.GetType().Name, it.propertyPath));
                }
            }

            if (candidates.Count == 0)
                return Error($"GameObject '{GameObjectEditingService.GetHierarchyPath(go)}' has no LocalizedValue field.");

            var fieldPath = args.Value<string>("field");
            (SerializedObject so, SerializedProperty prop, string component, string path) chosen;
            if (!string.IsNullOrEmpty(fieldPath))
            {
                var matches = candidates.Where(c => c.path == fieldPath).ToList();
                if (matches.Count == 0)
                    return Error($"No LocalizedValue with field path '{fieldPath}' on the target. "
                               + $"Available: {string.Join(", ", candidates.Select(c => $"{c.component}.{c.path}"))}.");
                chosen = matches[0];
            }
            else if (candidates.Count == 1)
            {
                chosen = candidates[0];
            }
            else
            {
                return Error("Target has multiple LocalizedValue fields; pass 'field'. "
                           + $"Candidates: {string.Join(", ", candidates.Select(c => $"{c.component}.{c.path}"))}.");
            }

            if (!LocalizedValueSerializedUtility.TryDescribe(
                    chosen.prop,
                    out var descriptor))
                return Error("The selected LocalizedValue changed shape; rescan and retry.");
            if (descriptor.SourceKind == LocalizedValueSourceKind.Catalog)
                return Error("This LocalizedValue uses a catalog source. Edit the StringTable entry instead.");
            if (descriptor.SourceKind == LocalizedValueSourceKind.None || descriptor.Rows == null)
                return Error("This LocalizedValue has no inline source. Choose Inline in the Inspector first.");

            var translationsProp = descriptor.Rows;
            SerializedProperty entry = null;
            for (int i = 0; i < translationsProp.arraySize; i++)
            {
                var el = translationsProp.GetArrayElementAtIndex(i);
                if (string.Equals(
                        el.FindPropertyRelative(descriptor.CodeField)?.stringValue,
                        languageCode,
                        System.StringComparison.OrdinalIgnoreCase))
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
                entry.FindPropertyRelative(descriptor.CodeField).stringValue = languageCode;
            }
            entry.FindPropertyRelative(descriptor.ValueField).stringValue = text;

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
                ["schemaVersion"] = descriptor.SchemaVersion.intValue,
                ["legacy"] = descriptor.IsLegacy,
                ["languageCode"] = languageCode,
                ["added"] = added
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationAddLanguage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var planId = args.Value<string>("planId");
            if (!LocalizationAuthoringService.TryGetPlan(planId, out var plan))
                return Error("The locale plan is missing or expired. Run molca_localization_plan_add_language again.");
            var result = LocalizationAuthoringService.ExecuteAddLocale(plan);
            if (!result.Succeeded)
                return Error(result.Error);

            return new JObject
            {
                ["planId"] = plan.PlanId,
                ["assetPath"] = plan.ModulePath,
                ["code"] = plan.Code,
                ["name"] = plan.DisplayName,
                ["languageCount"] = plan.Module.Languages.Length,
                ["createdAssetPaths"] = new JArray(result.CreatedAssetPaths),
                ["postAuditSnapshotId"] = result.PostAudit?.SnapshotId,
                ["postAuditStatus"] = result.PostAudit?.Status.ToString(),
                ["postAuditFingerprint"] = result.PostAudit?.CatalogFingerprint
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationPlanAddLanguage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var plan = LocalizationAuthoringService.PreviewAddLocale(
                args.Value<string>("code"),
                args.Value<string>("name"),
                args.Value<string>("modulePath"));
            return SerializeLocalizationPlan(plan).ToString(Formatting.None);
        }

        private static string ExecuteLocalizationPlanArchiveLanguage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var plan = LocalizationAuthoringService.PreviewArchiveLocale(
                args.Value<string>("code"),
                args.Value<string>("modulePath"));
            return SerializeLocalizationArchivePlan(plan).ToString(Formatting.None);
        }

        private static string ExecuteLocalizationArchiveLanguage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            if (!LocalizationAuthoringService.TryGetArchivePlan(
                    args.Value<string>("planId"),
                    out var plan))
                return Error(
                    "The locale archive plan is missing or expired. " +
                    "Run molca_localization_plan_archive_language again.");
            var result = LocalizationAuthoringService.ExecuteArchiveLocale(plan);
            if (!result.Succeeded)
                return Error(result.Error);

            return new JObject
            {
                ["planId"] = plan.PlanId,
                ["assetPath"] = plan.ModulePath,
                ["code"] = plan.Code,
                ["preservedLocaleAsset"] = AssetDatabase.GetAssetPath(plan.LocaleAsset),
                ["preservedTableAssets"] = new JArray(
                    plan.Tables.Select(item => AssetDatabase.GetAssetPath(item.table))),
                ["postAuditSnapshotId"] = result.PostAudit?.SnapshotId,
                ["postAuditStatus"] = result.PostAudit?.Status.ToString(),
                ["postAuditFingerprint"] = result.PostAudit?.CatalogFingerprint,
            }.ToString(Formatting.None);
        }

        private static JObject SerializeLocalizationPlan(LocalizationLocaleAuthoringPlan plan) =>
            new()
            {
                ["planId"] = plan.PlanId,
                ["createdAtUtc"] = plan.CreatedAtUtc,
                ["code"] = plan.Code,
                ["name"] = plan.DisplayName,
                ["modulePath"] = plan.ModulePath,
                ["sourceFingerprint"] = plan.SourceFingerprint,
                ["executable"] = plan.IsExecutable,
                ["changes"] = new JArray(plan.Changes),
                ["warnings"] = new JArray(plan.Warnings),
                ["errors"] = new JArray(plan.Errors),
            };

        private static JObject SerializeLocalizationArchivePlan(LocalizationLocaleArchivePlan plan) =>
            new()
            {
                ["planId"] = plan.PlanId,
                ["createdAtUtc"] = plan.CreatedAtUtc,
                ["code"] = plan.Code,
                ["modulePath"] = plan.ModulePath,
                ["sourceFingerprint"] = plan.SourceFingerprint,
                ["executable"] = plan.IsExecutable,
                ["changes"] = new JArray(plan.Changes),
                ["warnings"] = new JArray(plan.Warnings),
                ["errors"] = new JArray(plan.Errors),
            };

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
