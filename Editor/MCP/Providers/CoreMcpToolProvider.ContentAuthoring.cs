using System;
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
// Aliased: this file's namespace is Molca.Editor.Mcp.Providers, so the unqualified names would not
// resolve against Molca.ContentPackage.Editor.
using ContentEditResult = Molca.ContentPackage.Editor.ContentEditResult;
using ContentPackageEditingService = Molca.ContentPackage.Editor.ContentPackageEditingService;

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
    /// <b>Every config edit goes through <see cref="ContentPackageEditingService"/></b>, the one write
    /// path the Hub workspace and the remediation fixes also use. These tools used to mutate
    /// <see cref="ContentPackageSettings.PackageConfig"/> fields directly, which meant they were the only
    /// surface that could write a settings asset the service refuses — one inside a package or the
    /// read-only SDK layer, where an upgrade discards the write. They also could not report a refusal,
    /// because nothing was refusing.
    /// <para>
    /// The service records one Undo entry per operation; each tool collapses its batch into a single
    /// named group, so a multi-field call is still one Ctrl+Z
    /// (<see cref="McpToolReversibility.UnityUndo"/>). The Addressables group binding stamps labels onto
    /// group entries — those entry mutations are not cleanly undoable, so that tool is marked
    /// <see cref="McpToolReversibility.Irreversible"/>. Every <see cref="McpToolKind.Action"/> tool here
    /// is allowlist + confirmation gated (Sprint 17). No new runtime behaviour.
    /// </para>
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
            var editing = ResolveContentEditing(out var settings, out var error);
            if (editing == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            int group = Undo.GetCurrentGroup();
            var notes = new List<string>();

            bool created = settings.GetPackageConfig(packageId) == null;
            if (created)
            {
                var add = editing.AddPackage(packageId);
                if (!add.Changed) return Error(add.Message);

                // The service creates a package with an empty display name; this tool has always
                // defaulted it to the id, and a caller relying on that would otherwise get a config
                // carrying a blocking package_display_name_missing finding it did not ask for.
                if (args["displayName"] == null)
                    Note(notes, editing.SetDisplayName(packageId, packageId));
            }

            ApplyPackageFields(editing, packageId, args, notes);

            CollapseUndo(group, "Define Content Package");
            PersistSettings(settings);

            var cfg = settings.GetPackageConfig(packageId);
            return PackageConfigToJson(cfg, extra: new JObject
            {
                ["created"] = created,
                ["notes"] = new JArray(notes),
            });
        }

        /// <summary>
        /// Applies whichever package fields the arguments carry, through the editing service.
        /// </summary>
        /// <param name="editing">The write path.</param>
        /// <param name="packageId">The package to patch.</param>
        /// <param name="args">The parsed tool arguments.</param>
        /// <param name="notes">Collects what each setter reported, including refusals.</param>
        /// <remarks>
        /// Shared by define and update so the two cannot drift: they took the same field set and wrote it
        /// two ways, and only one of them was ever read.
        /// </remarks>
        private static void ApplyPackageFields(
            ContentPackageEditingService editing, string packageId, JObject args, List<string> notes)
        {
            if (args["displayName"] != null)
                Note(notes, editing.SetDisplayName(packageId, args.Value<string>("displayName")));
            if (args["version"] != null)
                Note(notes, editing.SetVersion(packageId, args.Value<string>("version")));
            if (args["description"] != null)
                Note(notes, editing.SetDescription(packageId, args.Value<string>("description")));
            if (args["author"] != null)
                Note(notes, editing.SetAuthor(packageId, args.Value<string>("author")));
            if (args["tags"] is JArray tags)
                Note(notes, editing.SetTags(packageId, ToStringArray(tags)));
            if (args["isVisible"] != null)
                Note(notes, editing.SetVisible(packageId, args.Value<bool>("isVisible")));
            if (args["isRequired"] != null)
                Note(notes, editing.SetRequired(packageId, args.Value<bool>("isRequired")));
            if (args["dependencies"] is JArray dependencies)
                Note(notes, editing.SetDependencies(packageId, ToStringArray(dependencies)));
            if (args["addressableLabels"] is JArray labels)
                Note(notes, editing.SetLabels(packageId, ToStringArray(labels)));
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
            var editing = ResolveContentEditing(out var settings, out var error);
            if (editing == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");
            if (settings.GetPackageConfig(packageId) == null)
                return Error($"No package config with id '{packageId}'.");

            int group = Undo.GetCurrentGroup();
            var notes = new List<string>();

            ApplyPackageFields(editing, packageId, args, notes);

            CollapseUndo(group, "Update Content Package");
            PersistSettings(settings);

            return PackageConfigToJson(
                settings.GetPackageConfig(packageId),
                extra: new JObject { ["notes"] = new JArray(notes) });
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
            var editing = ResolveContentEditing(out var settings, out var error);
            if (editing == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            var result = editing.RemovePackage(packageId);
            if (!result.Changed) return Error(result.Message);

            PersistSettings(settings);

            // The message names any package left depending on this one. Those are now
            // dependency_missing errors, and a caller that removed a package in a batch has no other
            // way to find out before its next validate.
            return new JObject
            {
                ["packageId"] = packageId,
                ["removed"] = true,
                ["notes"] = new JArray(result.Message),
            }.ToString(Formatting.None);
        }

        // ── molca_content_validate_config (read-only) ────────────────────────────────────────

        private static McpToolDefinition CreateContentValidateConfigTool() => new McpToolDefinition(
            name: "molca_content_validate_config",
            description: "Validates the authored content package configs (ids, display names, versions, "
                       + "Addressables labels, dependency edges and cycles) and returns the findings. "
                       + "Settings-level only — findings that need a build graph come from the Hub's "
                       + "Verify page. Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteContentValidateConfig,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        /// <remarks>
        /// Reports what <see cref="Molca.ContentPackage.Editor.ContentValidation"/> reports, not
        /// <c>ContentPackageSettings.ValidateConfigurations</c>. The legacy method knows about three of
        /// the checks and none of the dependency ones, so an agent could be told a definition set was
        /// valid while the Hub, the Doctor check and publishing all refused it.
        /// </remarks>
        private static string ExecuteContentValidateConfig(string argumentsJson)
        {
            var settings = ResolveContentSettings(out var error);
            if (settings == null) return Error(error);

            var report = Molca.ContentPackage.Editor.ContentValidation.ValidateSettings(settings.packageConfigs);

            return new JObject
            {
                ["packageCount"] = settings.packageConfigs.Count,
                ["valid"] = report.ErrorCount == 0,
                ["errorCount"] = report.ErrorCount,
                ["warningCount"] = report.WarningCount,
                ["errors"] = new JArray(report.Issues
                    .Where(issue => issue.Severity == Molca.ContentPackage.Editor.ContentIssueSeverity.Error)
                    .Select(issue => issue.ToString())),
                ["warnings"] = new JArray(report.Issues
                    .Where(issue => issue.Severity == Molca.ContentPackage.Editor.ContentIssueSeverity.Warning)
                    .Select(issue => issue.ToString())),
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
            var editing = ResolveContentEditing(out var settings, out var error);
            if (editing == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            var cfg = settings.GetPackageConfig(packageId);
            if (cfg == null) return Error($"No package config with id '{packageId}'.");

            int group = Undo.GetCurrentGroup();
            var notes = new List<string>();

            // Removals first, so add+remove of the same label in one call leaves it present — the order
            // this tool has always applied them in.
            if (args["remove"] is JArray removals)
            {
                foreach (var label in ToStringArray(removals))
                    Note(notes, editing.RemoveLabel(packageId, label));
            }

            if (args["add"] is JArray additions)
            {
                foreach (var label in ToStringArray(additions))
                    Note(notes, editing.AddLabel(packageId, label));
            }

            CollapseUndo(group, "Assign Content Labels");
            PersistSettings(settings);

            return new JObject
            {
                ["packageId"] = packageId,
                ["addressableLabels"] = new JArray(cfg.addressableLabels ?? new string[0]),
                ["notes"] = new JArray(notes),
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
            var editing = ResolveContentEditing(out var settings, out var error);
            if (editing == null) return Error(error);

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

            var bind = editing.AddLabel(packageId, labelName);
            PersistSettings(settings);

            return new JObject
            {
                ["packageId"] = packageId,
                ["group"] = groupName,
                ["label"] = labelName,
                ["entriesStamped"] = stamped,
                ["addressableLabels"] = new JArray(cfg.addressableLabels ?? new string[0]),
                ["notes"] = new JArray(bind.Message),
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

        /// <summary>
        /// Resolves the project's settings asset and the one service permitted to write it.
        /// </summary>
        /// <param name="settings">The resolved asset, or null.</param>
        /// <param name="error">Why no service could be built, or null.</param>
        /// <returns>The editing service, or null.</returns>
        /// <remarks>
        /// The read-only check is the reason this exists. These tools used to write
        /// <see cref="ContentPackageSettings.PackageConfig"/> fields directly, so an agent could author a
        /// settings asset living inside <c>Packages/</c> or the read-only SDK layer — writes an upgrade
        /// discards without anything saying so. The service refuses those assets; asking it up front
        /// turns that refusal into one clear tool error instead of a per-field one.
        /// </remarks>
        private static ContentPackageEditingService ResolveContentEditing(
            out ContentPackageSettings settings, out string error)
        {
            settings = ResolveContentSettings(out error);
            if (settings == null) return null;

            var editing = new ContentPackageEditingService(settings);
            string readOnly = editing.ReadOnlyReason();
            if (readOnly != null)
            {
                error = readOnly;
                return null;
            }

            return editing;
        }

        /// <summary>Records what a setter reported, so a refused field is visible in the tool result.</summary>
        /// <param name="notes">The collected messages.</param>
        /// <param name="result">The setter's result.</param>
        /// <remarks>
        /// Only changes and refusals are recorded. "Already that" is the common case when a caller
        /// re-sends a full field set, and reporting it would bury the one line that matters.
        /// </remarks>
        private static void Note(List<string> notes, ContentEditResult result)
        {
            if (result == null) return;
            if (result.Changed || !result.Message.EndsWith("is already that.", StringComparison.Ordinal))
                notes.Add(result.Message);
        }

        /// <summary>
        /// Folds every Undo entry since <paramref name="group"/> into one named step.
        /// </summary>
        /// <param name="group">The undo group captured before the first edit.</param>
        /// <param name="name">What the collapsed step is called.</param>
        /// <remarks>
        /// The editing service records one Undo entry per operation, which is what lets a Hub form undo a
        /// single field. A tool that applies eight fields is one action to its caller, though, and these
        /// tools promise <c>UnityUndo</c> reversibility — without the collapse, Ctrl+Z would walk back
        /// through them one at a time.
        /// </remarks>
        private static void CollapseUndo(int group, string name)
        {
            Undo.SetCurrentGroupName(name);
            Undo.CollapseUndoOperations(group);
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
