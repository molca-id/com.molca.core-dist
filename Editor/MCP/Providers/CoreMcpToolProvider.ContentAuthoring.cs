using System.Collections.Generic;
using System.IO;
using System.Linq;
using Molca.ContentPackage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Content Package <em>authoring</em> MCP tool family — the edit-time complement to the runtime
    /// operation family in <see cref="CoreMcpToolProvider"/>'s <c>ContentPackages</c> partial. Where the
    /// operation tools drive the live <c>PackageService</c> in Play mode, these mutate the project's
    /// authored <see cref="ContentPackageSettings"/> asset and its Addressables wiring in Edit mode:
    /// defining packages, patching their metadata, and binding Addressables labels/groups.
    /// </summary>
    /// <remarks>
    /// All config edits route through <see cref="Undo.RecordObject"/> on the
    /// <see cref="ContentPackageSettings"/> asset, so they collapse to a single Unity Undo group and are
    /// revertible with Ctrl+Z (<see cref="McpToolReversibility.UnityUndo"/>). The Addressables group
    /// binding stamps labels onto group entries — those entry mutations are not cleanly undoable, so that
    /// tool is marked <see cref="McpToolReversibility.Irreversible"/>. Every <see cref="McpToolKind.Action"/>
    /// tool here is allowlist + confirmation gated (Sprint 17). Mirrors the inspector authoring paths in
    /// <c>ContentPackageSettingsEditor</c> — no new runtime behaviour.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── Tier 1: package config authoring ─────────────────────────────────────────────────

        // ── molca_content_define_package ─────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentDefinePackageTool() => new McpToolDefinition(
            name: "molca_content_define_package",
            description: "Creates or fully replaces an authored content package config on the project's "
                       + "ContentPackageSettings asset. Sets id, display name, metadata (version/description/"
                       + "author/tags), visibility, required flag, dependencies, and Addressables labels. "
                       + "Edit mode only; one undo group (Ctrl+Z to revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"packageId\":{\"type\":\"string\",\"description\":\"Unique package id.\"}," +
                "\"displayName\":{\"type\":\"string\",\"description\":\"Human-facing name.\"}," +
                "\"version\":{\"type\":\"string\",\"description\":\"Authoring default version (e.g. '1.0.0').\"}," +
                "\"description\":{\"type\":\"string\"}," +
                "\"author\":{\"type\":\"string\"}," +
                "\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
                "\"isVisible\":{\"type\":\"boolean\",\"description\":\"Shown in the manager UI (default true).\"}," +
                "\"isRequired\":{\"type\":\"boolean\",\"description\":\"Auto-installed and non-uninstallable (default false).\"}," +
                "\"dependencies\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Package ids this package depends on.\"}," +
                "\"addressableLabels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
                "\"required\":[\"packageId\"],\"additionalProperties\":false}",
            execute: ExecuteContentDefinePackage,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteContentDefinePackage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            Undo.RecordObject(settings, "Define Content Package");

            var cfg = settings.GetPackageConfig(packageId);
            bool created = cfg == null;
            if (created)
            {
                cfg = new ContentPackageSettings.PackageConfig { packageId = packageId };
                settings.packageConfigs.Add(cfg);
            }

            cfg.displayName       = args.Value<string>("displayName") ?? cfg.displayName ?? packageId;
            cfg.metadata        ??= new ContentPackageSettings.PackageMetadata();
            cfg.metadata.version     = args.Value<string>("version") ?? cfg.metadata.version;
            cfg.metadata.description = args.Value<string>("description") ?? cfg.metadata.description ?? "";
            cfg.metadata.author      = args.Value<string>("author") ?? cfg.metadata.author ?? "";
            if (args["tags"] is JArray tags) cfg.metadata.tags = ToStringArray(tags);
            if (args["isVisible"] != null)  cfg.isVisible  = args.Value<bool>("isVisible");
            if (args["isRequired"] != null) cfg.isRequired = args.Value<bool>("isRequired");
            if (args["dependencies"] is JArray deps)
                cfg.dependencies = ToStringArray(deps)
                    .Select(id => new ContentPackageSettings.PackageDependency { packageId = id })
                    .ToArray();
            if (args["addressableLabels"] is JArray labels)
                cfg.addressableLabels = ToStringArray(labels);

            PersistSettings(settings);
            return PackageConfigToJson(cfg, extra: new JObject { ["created"] = created });
        }

        // ── molca_content_update_package ─────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentUpdatePackageTool() => new McpToolDefinition(
            name: "molca_content_update_package",
            description: "Patches an existing content package config: only the fields you provide are "
                       + "changed (others are left untouched). Locate by 'packageId'. Edit mode only; "
                       + "revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"packageId\":{\"type\":\"string\",\"description\":\"Id of the package to patch.\"}," +
                "\"displayName\":{\"type\":\"string\"}," +
                "\"version\":{\"type\":\"string\"}," +
                "\"description\":{\"type\":\"string\"}," +
                "\"author\":{\"type\":\"string\"}," +
                "\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
                "\"isVisible\":{\"type\":\"boolean\"}," +
                "\"isRequired\":{\"type\":\"boolean\"}," +
                "\"dependencies\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
                "\"addressableLabels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
                "\"required\":[\"packageId\"],\"additionalProperties\":false}",
            execute: ExecuteContentUpdatePackage,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteContentUpdatePackage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            var cfg = settings.GetPackageConfig(packageId);
            if (cfg == null) return Error($"No package config with id '{packageId}'.");

            Undo.RecordObject(settings, "Update Content Package");

            if (args["displayName"] != null) cfg.displayName = args.Value<string>("displayName");
            if (args["version"] != null || args["description"] != null || args["author"] != null || args["tags"] is JArray)
                cfg.metadata ??= new ContentPackageSettings.PackageMetadata();
            if (args["version"] != null)     cfg.metadata.version     = args.Value<string>("version");
            if (args["description"] != null) cfg.metadata.description = args.Value<string>("description");
            if (args["author"] != null)      cfg.metadata.author      = args.Value<string>("author");
            if (args["tags"] is JArray tags) cfg.metadata.tags        = ToStringArray(tags);
            if (args["isVisible"] != null)   cfg.isVisible  = args.Value<bool>("isVisible");
            if (args["isRequired"] != null)  cfg.isRequired = args.Value<bool>("isRequired");
            if (args["dependencies"] is JArray deps)
                cfg.dependencies = ToStringArray(deps)
                    .Select(id => new ContentPackageSettings.PackageDependency { packageId = id })
                    .ToArray();
            if (args["addressableLabels"] is JArray labels)
                cfg.addressableLabels = ToStringArray(labels);

            PersistSettings(settings);
            return PackageConfigToJson(cfg);
        }

        // ── molca_content_remove_package ─────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentRemovePackageTool() => new McpToolDefinition(
            name: "molca_content_remove_package",
            description: "Removes an authored content package config (by id) from ContentPackageSettings. "
                       + "Does not touch built bundles or Addressables labels. Edit mode only; revert with Ctrl+Z.",
            inputSchemaJson: SinglePackageSchema,
            execute: ExecuteContentRemovePackage,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteContentRemovePackage(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            int idx = settings.packageConfigs.FindIndex(c => c.packageId == packageId);
            if (idx < 0) return Error($"No package config with id '{packageId}'.");

            Undo.RecordObject(settings, "Remove Content Package");
            settings.packageConfigs.RemoveAt(idx);
            PersistSettings(settings);

            return new JObject { ["packageId"] = packageId, ["removed"] = true }.ToString(Formatting.None);
        }

        // ── molca_content_validate_config (read-only) ────────────────────────────────────────

        private static McpToolDefinition CreateContentValidateConfigTool() => new McpToolDefinition(
            name: "molca_content_validate_config",
            description: "Validates the authored content package configs (missing ids, display names, or "
                       + "Addressables labels) and returns the list of human-readable errors. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteContentValidateConfig,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteContentValidateConfig(string argumentsJson)
        {
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var errors = settings.ValidateConfigurations();
            return new JObject
            {
                ["packageCount"] = settings.packageConfigs.Count,
                ["valid"] = errors.Count == 0,
                ["errors"] = new JArray(errors)
            }.ToString(Formatting.None);
        }

        // ── Tier 2: Addressables wiring ──────────────────────────────────────────────────────

        // ── molca_content_assign_labels ──────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentAssignLabelsTool() => new McpToolDefinition(
            name: "molca_content_assign_labels",
            description: "Adds and/or removes Addressables labels on a package config's download set. "
                       + "Operates only on the config (does not stamp labels onto Addressables entries — "
                       + "use molca_content_bind_group for that). Edit mode only; revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"packageId\":{\"type\":\"string\",\"description\":\"Package config to modify.\"}," +
                "\"add\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Labels to add.\"}," +
                "\"remove\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Labels to remove.\"}}," +
                "\"required\":[\"packageId\"],\"additionalProperties\":false}",
            execute: ExecuteContentAssignLabels,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteContentAssignLabels(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            var cfg = settings.GetPackageConfig(packageId);
            if (cfg == null) return Error($"No package config with id '{packageId}'.");

            Undo.RecordObject(settings, "Assign Content Labels");

            var current = new List<string>(cfg.addressableLabels ?? new string[0]);
            if (args["remove"] is JArray rem)
                foreach (var label in ToStringArray(rem)) current.RemoveAll(l => l == label);
            if (args["add"] is JArray add)
                foreach (var label in ToStringArray(add))
                    if (!current.Contains(label)) current.Add(label);

            cfg.addressableLabels = current.ToArray();
            PersistSettings(settings);

            return new JObject
            {
                ["packageId"] = packageId,
                ["addressableLabels"] = new JArray(current)
            }.ToString(Formatting.None);
        }

        // ── molca_content_bind_group ─────────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentBindGroupTool() => new McpToolDefinition(
            name: "molca_content_bind_group",
            description: "Binds an Addressables group to a package: ensures a label named after the group "
                       + "exists, stamps it onto every entry in that group, then adds it to the package's "
                       + "labels. Mirrors the inspector's 'Pick Groups…' action. Edit mode only. Note: "
                       + "stamping labels onto Addressables entries is not undoable.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"packageId\":{\"type\":\"string\",\"description\":\"Package config to bind to.\"}," +
                "\"group\":{\"type\":\"string\",\"description\":\"Addressables group name to bind (used as the label).\"}}," +
                "\"required\":[\"packageId\",\"group\"],\"additionalProperties\":false}",
            execute: ExecuteContentBindGroup,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteContentBindGroup(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            var groupName = args.Value<string>("group");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");
            if (string.IsNullOrWhiteSpace(groupName)) return Error("'group' is required.");

            var cfg = settings.GetPackageConfig(packageId);
            if (cfg == null) return Error($"No package config with id '{packageId}'.");

            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings == null) return Error("Addressables is not configured in this project.");

            var group = addrSettings.groups.FirstOrDefault(g => g != null && g.Name == groupName);
            if (group == null) return Error($"No Addressables group named '{groupName}'.");

            // Use the group name as the label (the convention the inspector follows).
            var labelName = groupName;
            if (!addrSettings.GetLabels().Contains(labelName))
                addrSettings.AddLabel(labelName);

            int stamped = 0;
            foreach (var entry in group.entries)
            {
                if (entry == null || entry.labels.Contains(labelName)) continue;
                entry.SetLabel(labelName, true, postEvent: false);
                stamped++;
            }
            EditorUtility.SetDirty(addrSettings);

            Undo.RecordObject(settings, "Bind Content Group");
            var labels = new List<string>(cfg.addressableLabels ?? new string[0]);
            if (!labels.Contains(labelName)) labels.Add(labelName);
            cfg.addressableLabels = labels.ToArray();
            PersistSettings(settings);

            return new JObject
            {
                ["packageId"] = packageId,
                ["group"] = groupName,
                ["label"] = labelName,
                ["entriesStamped"] = stamped,
                ["addressableLabels"] = new JArray(labels)
            }.ToString(Formatting.None);
        }

        // ── molca_content_scan (read-only) ───────────────────────────────────────────────────

        private static McpToolDefinition CreateContentScanTool() => new McpToolDefinition(
            name: "molca_content_scan",
            description: "Scans the Addressables entries matching a package's labels and reports asset count "
                       + "and approximate source size (the real bundle size is written at build time). "
                       + "Read-only.",
            inputSchemaJson: SinglePackageSchema,
            execute: ExecuteContentScan,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteContentScan(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            var cfg = settings.GetPackageConfig(packageId);
            if (cfg == null) return Error($"No package config with id '{packageId}'.");

            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings == null) return Error("Addressables is not configured in this project.");

            var labels = new HashSet<string>(cfg.addressableLabels ?? new string[0]);
            if (labels.Count == 0)
                return new JObject { ["packageId"] = packageId, ["labelCount"] = 0, ["assetCount"] = 0, ["sourceBytes"] = 0 }
                    .ToString(Formatting.None);

            var (count, size) = ScanLabelAssets(labels, addrSettings);
            return new JObject
            {
                ["packageId"] = packageId,
                ["labelCount"] = labels.Count,
                ["assetCount"] = count,
                ["sourceBytes"] = size
            }.ToString(Formatting.None);
        }

        // ── Shared plumbing ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the single authored <see cref="ContentPackageSettings"/> asset in the project,
        /// or sets <paramref name="error"/> if none (or the load) fails.
        /// </summary>
        private static ContentPackageSettings ResolveContentSettings(out string error)
        {
            error = null;
            var guids = AssetDatabase.FindAssets("t:ContentPackageSettings");
            if (guids.Length == 0)
            {
                error = "No ContentPackageSettings asset found. Create one via "
                      + "Assets > Create > Molca > Settings > Content Package Settings.";
                return null;
            }

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            var settings = AssetDatabase.LoadAssetAtPath<ContentPackageSettings>(path);
            if (settings == null)
            {
                error = $"Failed to load ContentPackageSettings at '{path}'.";
                return null;
            }
            return settings;
        }

        /// <summary>Marks the settings asset dirty and writes it to disk after an authored edit.</summary>
        private static void PersistSettings(ContentPackageSettings settings)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        /// <summary>Serializes a <see cref="ContentPackageSettings.PackageConfig"/> to a flat JSON object.</summary>
        private static string PackageConfigToJson(ContentPackageSettings.PackageConfig cfg, JObject extra = null)
        {
            var obj = new JObject
            {
                ["packageId"] = cfg.packageId,
                ["displayName"] = cfg.displayName,
                ["version"] = cfg.metadata?.version,
                ["description"] = cfg.metadata?.description,
                ["author"] = cfg.metadata?.author,
                ["tags"] = new JArray(cfg.metadata?.tags ?? new string[0]),
                ["isVisible"] = cfg.isVisible,
                ["isRequired"] = cfg.isRequired,
                ["dependencies"] = new JArray((cfg.dependencies ?? new ContentPackageSettings.PackageDependency[0])
                    .Where(d => d != null).Select(d => d.packageId)),
                ["addressableLabels"] = new JArray(cfg.addressableLabels ?? new string[0])
            };
            if (extra != null)
                foreach (var prop in extra.Properties())
                    obj[prop.Name] = prop.Value;
            return obj.ToString(Formatting.None);
        }

        private static string[] ToStringArray(JArray array)
            => array.Select(t => t.Value<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        /// <summary>
        /// Counts assets (and approximate source bytes, including dependencies) of every Addressables
        /// entry whose labels overlap <paramref name="labels"/>. Ports the inspector's scan logic.
        /// </summary>
        private static (int count, long size) ScanLabelAssets(HashSet<string> labels, AddressableAssetSettings addrSettings)
        {
            int count = 0;
            long size = 0;
            var counted = new HashSet<string>();

            foreach (var group in addrSettings.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries)
                {
                    if (entry == null || !entry.labels.Overlaps(labels)) continue;
                    AccumulateEntry(entry.AssetPath, counted, ref count, ref size);
                }
            }
            return (count, size);
        }

        private static void AccumulateEntry(string assetPath, HashSet<string> counted, ref int count, ref long size)
        {
            if (string.IsNullOrEmpty(assetPath) || !counted.Add(assetPath)) return;

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                foreach (var guid in AssetDatabase.FindAssets("", new[] { assetPath }))
                {
                    var child = AssetDatabase.GUIDToAssetPath(guid);
                    if (!AssetDatabase.IsValidFolder(child))
                        AccumulateEntry(child, counted, ref count, ref size);
                }
                return;
            }

            var fi = new FileInfo(assetPath);
            if (!fi.Exists) return;
            size += fi.Length;
            count++;

            foreach (var dep in AssetDatabase.GetDependencies(assetPath, recursive: true))
            {
                if (dep == assetPath || AssetDatabase.IsValidFolder(dep) || !counted.Add(dep)) continue;
                var depFi = new FileInfo(dep);
                if (depFi.Exists) size += depFi.Length;
            }
        }
    }
}
