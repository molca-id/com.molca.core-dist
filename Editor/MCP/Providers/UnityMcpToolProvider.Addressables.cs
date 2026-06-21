using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Addressables discovery and authoring tools (groups, entries, labels, profiles).
    /// </summary>
    /// <remarks>
    /// All tools require Addressables to be initialized in the project
    /// (<see cref="AddressableAssetSettingsDefaultObject.Settings"/> non-null); each returns a clear
    /// error otherwise. Action tools are marked <see cref="McpToolReversibility.Irreversible"/>: an
    /// Addressables edit touches the settings asset and one or more per-group assets at once, which the
    /// single-file <c>McpUndoStack</c> snapshot cannot capture, so no automatic revert is offered.
    /// </remarks>
    public sealed partial class UnityMcpToolProvider
    {
        // ---- Read-only tools -------------------------------------------------------------------

        private static McpToolDefinition CreateAddressableSettingsTool() => new McpToolDefinition(
            name: "molca_unity_addressable_settings",
            description: "Reports the Addressables configuration: settings asset path, active profile and all "
                       + "profiles, label list, and a per-group summary (name, GUID, entry count, schema types, "
                       + "and bundled build/load path variables). Read-only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteAddressableSettings,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateAddressableEntriesTool() => new McpToolDefinition(
            name: "molca_unity_addressable_entries",
            description: "Lists Addressables entries with address, asset path, GUID, owning group, labels, and "
                       + "asset type. Optional 'group' (exact group name), 'addressContains' (case-insensitive "
                       + "substring), and 'label' filters; 'limit' caps results (default 200). Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"group\":{\"type\":\"string\",\"description\":\"Only entries in this group (exact name).\"}," +
                "\"addressContains\":{\"type\":\"string\",\"description\":\"Case-insensitive address substring filter.\"}," +
                "\"label\":{\"type\":\"string\",\"description\":\"Only entries carrying this label.\"}," +
                "\"limit\":{\"type\":\"integer\",\"description\":\"Max entries to return (default 200).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteAddressableEntries,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static McpToolDefinition CreateAddressableResolveTool() => new McpToolDefinition(
            name: "molca_unity_addressable_resolve",
            description: "Resolves an Addressables 'address' (exact) or 'label' to the matching entries, "
                       + "reporting each asset path, group, and labels. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"address\":{\"type\":\"string\",\"description\":\"Exact address to resolve.\"}," +
                "\"label\":{\"type\":\"string\",\"description\":\"Label to resolve.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteAddressableResolve,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        // ---- Action tools ----------------------------------------------------------------------

        private static McpToolDefinition CreateAddressableMarkTool() => new McpToolDefinition(
            name: "molca_unity_addressable_mark",
            description: "Makes a project asset addressable. 'path' (asset path) is required. Optional 'group' "
                       + "(target group name; the default group if omitted), 'address' (defaults to the asset "
                       + "path), 'labels' (array to apply), and 'createGroup' (create the named group if absent). "
                       + "Irreversible (no automatic revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Project asset path to make addressable.\"}," +
                "\"group\":{\"type\":\"string\",\"description\":\"Target group name (default group if omitted).\"}," +
                "\"address\":{\"type\":\"string\",\"description\":\"Address to assign (defaults to the asset path).\"}," +
                "\"labels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Labels to apply.\"}," +
                "\"createGroup\":{\"type\":\"boolean\",\"description\":\"Create 'group' if it does not exist.\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteAddressableMark,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateAddressableUnmarkTool() => new McpToolDefinition(
            name: "molca_unity_addressable_unmark",
            description: "Removes an asset from Addressables, identified by 'path' (asset path) or 'address'. "
                       + "Irreversible (no automatic revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path of the entry to remove.\"}," +
                "\"address\":{\"type\":\"string\",\"description\":\"Address of the entry to remove.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteAddressableUnmark,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateAddressableSetAddressTool() => new McpToolDefinition(
            name: "molca_unity_addressable_set_address",
            description: "Renames an entry's address. Identify the entry by 'path' (asset path) or current "
                       + "'address'; 'newAddress' is required. Irreversible (no automatic revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path of the entry.\"}," +
                "\"address\":{\"type\":\"string\",\"description\":\"Current address of the entry.\"}," +
                "\"newAddress\":{\"type\":\"string\",\"description\":\"New address to assign.\"}}," +
                "\"required\":[\"newAddress\"],\"additionalProperties\":false}",
            execute: ExecuteAddressableSetAddress,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateAddressableSetLabelsTool() => new McpToolDefinition(
            name: "molca_unity_addressable_set_labels",
            description: "Edits an entry's labels. Identify the entry by 'path' or 'address'. Use 'set' (array) to "
                       + "replace all labels, or 'add'/'remove' (arrays) for incremental changes. Missing labels "
                       + "are created in the settings. Irreversible (no automatic revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path of the entry.\"}," +
                "\"address\":{\"type\":\"string\",\"description\":\"Address of the entry.\"}," +
                "\"set\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Replace all labels with these.\"}," +
                "\"add\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Labels to add.\"}," +
                "\"remove\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Labels to remove.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteAddressableSetLabels,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateAddressableMoveTool() => new McpToolDefinition(
            name: "molca_unity_addressable_move",
            description: "Moves an existing entry to another group. Identify the entry by 'path' or 'address'; "
                       + "'group' (target group name) is required. Optional 'createGroup' creates the target group "
                       + "if absent. Irreversible (no automatic revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path of the entry.\"}," +
                "\"address\":{\"type\":\"string\",\"description\":\"Address of the entry.\"}," +
                "\"group\":{\"type\":\"string\",\"description\":\"Target group name.\"}," +
                "\"createGroup\":{\"type\":\"boolean\",\"description\":\"Create 'group' if it does not exist.\"}}," +
                "\"required\":[\"group\"],\"additionalProperties\":false}",
            execute: ExecuteAddressableMove,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateAddressableCreateGroupTool() => new McpToolDefinition(
            name: "molca_unity_addressable_create_group",
            description: "Creates an Addressables group with the standard bundled + content-update schemas. "
                       + "'name' is required; optional 'setAsDefault' makes it the default group. Irreversible.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"New group name.\"}," +
                "\"setAsDefault\":{\"type\":\"boolean\",\"description\":\"Make this the default group.\"}}," +
                "\"required\":[\"name\"],\"additionalProperties\":false}",
            execute: ExecuteAddressableCreateGroup,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static McpToolDefinition CreateAddressableRemoveGroupTool() => new McpToolDefinition(
            name: "molca_unity_addressable_remove_group",
            description: "Removes an Addressables group by 'name', along with its entries. The default group and "
                       + "read-only/built-in groups cannot be removed. Irreversible (no automatic revert).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"name\":{\"type\":\"string\",\"description\":\"Group name to remove.\"}}," +
                "\"required\":[\"name\"],\"additionalProperties\":false}",
            execute: ExecuteAddressableRemoveGroup,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        // ---- Read-only execution ---------------------------------------------------------------

        private static string ExecuteAddressableSettings(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var profiles = new JArray();
            foreach (var name in settings.profileSettings.GetAllProfileNames())
                profiles.Add(name);

            var groups = new JArray();
            foreach (var group in settings.groups)
            {
                if (group == null) continue;
                var bundled = group.GetSchema<BundledAssetGroupSchema>();
                groups.Add(new JObject
                {
                    ["name"] = group.Name,
                    ["guid"] = group.Guid,
                    ["entryCount"] = group.entries?.Count ?? 0,
                    ["isDefault"] = settings.DefaultGroup == group,
                    ["readOnly"] = group.ReadOnly,
                    ["schemas"] = new JArray(group.Schemas.Where(s => s != null).Select(s => s.GetType().Name)),
                    ["buildPath"] = bundled != null ? bundled.BuildPath.GetName(settings) : null,
                    ["loadPath"] = bundled != null ? bundled.LoadPath.GetName(settings) : null
                });
            }

            return new JObject
            {
                ["settingsPath"] = AssetDatabase.GetAssetPath(settings),
                ["activeProfile"] = settings.profileSettings.GetProfileName(settings.activeProfileId),
                ["profiles"] = profiles,
                ["labels"] = new JArray(settings.GetLabels()),
                ["defaultGroup"] = settings.DefaultGroup != null ? settings.DefaultGroup.Name : null,
                ["groupCount"] = groups.Count,
                ["groups"] = groups
            }.ToString(Formatting.None);
        }

        private static string ExecuteAddressableEntries(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var groupFilter = args.Value<string>("group");
            var addressContains = args.Value<string>("addressContains");
            var label = args.Value<string>("label");
            var limit = args["limit"] != null ? Math.Max(1, args.Value<int>("limit")) : 200;

            var entries = new JArray();
            var truncated = false;
            foreach (var group in settings.groups)
            {
                if (truncated) break;
                if (group == null) continue;
                if (!string.IsNullOrEmpty(groupFilter) && !string.Equals(group.Name, groupFilter, StringComparison.Ordinal))
                    continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null) continue;
                    if (!string.IsNullOrEmpty(addressContains) &&
                        (entry.address == null || entry.address.IndexOf(addressContains, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    if (!string.IsNullOrEmpty(label) && !entry.labels.Contains(label))
                        continue;

                    if (entries.Count >= limit) { truncated = true; break; }
                    entries.Add(EntryToJson(entry, group));
                }
            }

            return new JObject
            {
                ["count"] = entries.Count,
                ["truncated"] = truncated,
                ["entries"] = entries
            }.ToString(Formatting.None);
        }

        private static string ExecuteAddressableResolve(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var address = args.Value<string>("address");
            var label = args.Value<string>("label");
            if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(label))
                return Error("pass 'address' or 'label'.");

            var matches = new JArray();
            foreach (var group in settings.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries)
                {
                    if (entry == null) continue;
                    var addressHit = !string.IsNullOrEmpty(address) &&
                                     string.Equals(entry.address, address, StringComparison.Ordinal);
                    var labelHit = !string.IsNullOrEmpty(label) && entry.labels.Contains(label);
                    if (addressHit || labelHit)
                        matches.Add(EntryToJson(entry, group));
                }
            }

            return new JObject { ["count"] = matches.Count, ["entries"] = matches }.ToString(Formatting.None);
        }

        // ---- Action execution ------------------------------------------------------------------

        private static string ExecuteAddressableMark(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return Error($"no asset at '{path}'.");

            var group = ResolveTargetGroup(settings, args.Value<string>("group"),
                args.Value<bool?>("createGroup") ?? false, out var groupError);
            if (group == null) return Error(groupError);

            var entry = settings.CreateOrMoveEntry(guid, group, readOnly: false, postEvent: true);
            if (entry == null) return Error($"failed to create an Addressables entry for '{path}'.");

            var address = args.Value<string>("address");
            if (!string.IsNullOrWhiteSpace(address)) entry.address = address;

            ApplyLabelArray(settings, entry, args["labels"] as JArray, enable: true);

            AssetDatabase.SaveAssets();
            return EntryToJson(entry, group).ToString(Formatting.None);
        }

        private static string ExecuteAddressableUnmark(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var entry = ResolveEntry(settings, args.Value<string>("path"), args.Value<string>("address"), out var error);
            if (entry == null) return Error(error);

            var removedAddress = entry.address;
            settings.RemoveAssetEntry(entry.guid);
            AssetDatabase.SaveAssets();

            return new JObject { ["removed"] = removedAddress, ["guid"] = entry.guid }.ToString(Formatting.None);
        }

        private static string ExecuteAddressableSetAddress(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var newAddress = args.Value<string>("newAddress");
            if (string.IsNullOrWhiteSpace(newAddress)) return Error("'newAddress' is required.");

            var entry = ResolveEntry(settings, args.Value<string>("path"), args.Value<string>("address"), out var error);
            if (entry == null) return Error(error);

            var oldAddress = entry.address;
            entry.address = newAddress;
            AssetDatabase.SaveAssets();

            return new JObject { ["oldAddress"] = oldAddress, ["address"] = entry.address, ["guid"] = entry.guid }
                .ToString(Formatting.None);
        }

        private static string ExecuteAddressableSetLabels(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var entry = ResolveEntry(settings, args.Value<string>("path"), args.Value<string>("address"), out var error);
            if (entry == null) return Error(error);

            if (args["set"] is JArray set)
            {
                // Replace: drop every current label, then apply the requested set.
                foreach (var existing in entry.labels.ToArray())
                    entry.SetLabel(existing, false, force: false, postEvent: true);
                ApplyLabelArray(settings, entry, set, enable: true);
            }
            else
            {
                ApplyLabelArray(settings, entry, args["remove"] as JArray, enable: false);
                ApplyLabelArray(settings, entry, args["add"] as JArray, enable: true);
            }

            AssetDatabase.SaveAssets();
            return EntryToJson(entry, entry.parentGroup).ToString(Formatting.None);
        }

        private static string ExecuteAddressableMove(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var entry = ResolveEntry(settings, args.Value<string>("path"), args.Value<string>("address"), out var error);
            if (entry == null) return Error(error);

            var group = ResolveTargetGroup(settings, args.Value<string>("group"),
                args.Value<bool?>("createGroup") ?? false, out var groupError);
            if (group == null) return Error(groupError);

            var moved = settings.CreateOrMoveEntry(entry.guid, group, readOnly: false, postEvent: true);
            AssetDatabase.SaveAssets();
            return EntryToJson(moved, group).ToString(Formatting.None);
        }

        private static string ExecuteAddressableCreateGroup(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var name = args.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required.");
            if (settings.FindGroup(name) != null) return Error($"a group named '{name}' already exists.");

            var setAsDefault = args.Value<bool?>("setAsDefault") ?? false;
            var group = settings.CreateGroup(name, setAsDefault, readOnly: false, postEvent: true,
                schemasToCopy: null,
                types: new[] { typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema) });
            if (group == null) return Error($"failed to create group '{name}'.");

            AssetDatabase.SaveAssets();
            return new JObject
            {
                ["created"] = group.Name,
                ["guid"] = group.Guid,
                ["isDefault"] = settings.DefaultGroup == group
            }.ToString(Formatting.None);
        }

        private static string ExecuteAddressableRemoveGroup(string argumentsJson)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return AddressablesNotConfigured();

            var args = ParseArgs(argumentsJson);
            var name = args.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required.");

            var group = settings.FindGroup(name);
            if (group == null) return Error($"no group named '{name}'.");
            if (group.ReadOnly) return Error($"'{name}' is a read-only/built-in group and cannot be removed.");
            if (settings.DefaultGroup == group) return Error($"'{name}' is the default group; set another group as default first.");

            var removedEntries = group.entries?.Count ?? 0;
            settings.RemoveGroup(group);
            AssetDatabase.SaveAssets();

            return new JObject { ["removed"] = name, ["removedEntries"] = removedEntries }.ToString(Formatting.None);
        }

        // ---- Helpers ---------------------------------------------------------------------------

        private static string AddressablesNotConfigured() => Error(
            "Addressables is not initialized in this project. Open Window > Asset Management > Addressables > "
            + "Groups and create the settings first.");

        /// <summary>Serializes one entry plus its owning group to the standard JSON shape.</summary>
        private static JObject EntryToJson(AddressableAssetEntry entry, AddressableAssetGroup group) => new JObject
        {
            ["address"] = entry.address,
            ["assetPath"] = entry.AssetPath,
            ["guid"] = entry.guid,
            ["group"] = group != null ? group.Name : entry.parentGroup != null ? entry.parentGroup.Name : null,
            ["labels"] = new JArray(entry.labels),
            ["type"] = entry.MainAssetType != null ? entry.MainAssetType.Name : null
        };

        /// <summary>
        /// Finds an entry by asset <paramref name="path"/> (preferred) or exact <paramref name="address"/>.
        /// Returns null and sets <paramref name="error"/> when neither is supplied or no entry matches.
        /// </summary>
        private static AddressableAssetEntry ResolveEntry(AddressableAssetSettings settings, string path, string address, out string error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) { error = $"no asset at '{path}'."; return null; }
                var entry = settings.FindAssetEntry(guid);
                if (entry == null) { error = $"'{path}' is not addressable."; return null; }
                return entry;
            }

            if (!string.IsNullOrWhiteSpace(address))
            {
                foreach (var group in settings.groups)
                {
                    if (group == null) continue;
                    foreach (var entry in group.entries)
                        if (entry != null && string.Equals(entry.address, address, StringComparison.Ordinal))
                            return entry;
                }
                error = $"no entry with address '{address}'.";
                return null;
            }

            error = "pass 'path' or 'address' to identify the entry.";
            return null;
        }

        /// <summary>
        /// Resolves the target group by <paramref name="name"/>, creating it (with standard schemas) when
        /// <paramref name="createGroup"/> is set, or falling back to the default group when no name is given.
        /// </summary>
        private static AddressableAssetGroup ResolveTargetGroup(AddressableAssetSettings settings, string name, bool createGroup, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (settings.DefaultGroup == null) { error = "no default Addressables group is set."; return null; }
                return settings.DefaultGroup;
            }

            var group = settings.FindGroup(name);
            if (group != null) return group;

            if (!createGroup) { error = $"no group named '{name}' (pass createGroup=true to create it)."; return null; }

            group = settings.CreateGroup(name, setAsDefaultGroup: false, readOnly: false, postEvent: true,
                schemasToCopy: null,
                types: new[] { typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema) });
            if (group == null) error = $"failed to create group '{name}'.";
            return group;
        }

        /// <summary>Applies (or clears) each label in <paramref name="labels"/> on the entry, creating missing labels.</summary>
        private static void ApplyLabelArray(AddressableAssetSettings settings, AddressableAssetEntry entry, JArray labels, bool enable)
        {
            if (labels == null) return;
            foreach (var token in labels)
            {
                var label = token?.Value<string>();
                if (string.IsNullOrWhiteSpace(label)) continue;
                // force:true registers the label in the settings if it is new, so SetLabel never no-ops.
                entry.SetLabel(label, enable, force: enable, postEvent: true);
            }
        }
    }
}
