using System;
using System.Linq;
using Molca.ContentPackage;
using Molca.ContentPackage.Editor;
using Molca.ContentPackage.Release;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Authoring tools for <see cref="ContentPackageSettings"/>.
    /// </summary>
    /// <remarks>
    /// The existing <c>molca_content_*</c> tools are all runtime operations — list, install,
    /// uninstall, switch version — so there was no way to author content configuration outside the
    /// inspector. Plan §10.6 requires MCP parity for authoring, and configuration fell through the
    /// same gap the visual surface did.
    ///
    /// Every mutation routes through <see cref="ContentPackageEditingService"/> — the same path the
    /// inspector and the remediation fixes use. Nothing here opens its own
    /// <see cref="SerializedObject"/>, and nothing here resolves an ambiguity: operations that would
    /// pick between two duplicate ids or guess which dependency edge is wrong are simply absent.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── Read-only: molca_content_settings ────────────────────────────────

        private static McpToolDefinition CreateContentSettingsTool() => new McpToolDefinition(
            name: "molca_content_settings",
            description: "Describes ContentPackageSettings assets in the project: package definitions "
                       + "(id, display name, required/visible, labels, dependencies), release-protocol "
                       + "configuration (enabled, content service id, path prefix, trusted signing key "
                       + "ids), validation findings from the shared engine, and whether the asset is "
                       + "writable. Never reports key modulus material. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"assetPath\":{\"type\":\"string\",\"description\":\"Specific settings asset; omit for all.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteContentSettings,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteContentSettings(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            string only = args.Value<string>("assetPath");

            var assets = new JArray();
            foreach (var (settings, path) in FindContentSettings())
            {
                if (!string.IsNullOrEmpty(only) &&
                    !string.Equals(path, only, StringComparison.OrdinalIgnoreCase)) continue;

                var service = new ContentPackageEditingService(settings);
                var report = service.Validate();

                var packages = new JArray();
                foreach (var config in settings.packageConfigs ?? new System.Collections.Generic.List<ContentPackageSettings.PackageConfig>())
                {
                    if (config == null) continue;
                    packages.Add(new JObject
                    {
                        ["packageId"] = config.packageId ?? "",
                        ["displayName"] = config.displayName ?? "",
                        ["version"] = config.metadata?.version ?? "",
                        ["required"] = config.isRequired,
                        ["visible"] = config.isVisible,
                        ["labels"] = new JArray(config.addressableLabels ?? Array.Empty<string>()),
                        ["dependencies"] = new JArray(
                            (config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>())
                            .Where(dependency => dependency != null)
                            .Select(dependency => dependency.packageId ?? "")),
                    });
                }

                assets.Add(new JObject
                {
                    ["assetPath"] = path,
                    ["writable"] = service.ReadOnlyReason() == null,
                    ["readOnlyReason"] = service.ReadOnlyReason() ?? "",
                    ["packages"] = packages,
                    ["releaseProtocol"] = new JObject
                    {
                        ["enabled"] = settings.EnableReleaseProtocol,
                        ["contentServiceId"] = settings.ContentServiceId ?? "",
                        ["contentPathPrefix"] = settings.ContentPathPrefix ?? "",
                        // Key ids only. The modulus is public material, but echoing key bytes into a
                        // transcript invites them being pasted somewhere as if they were a secret,
                        // and the id is what an operator actually needs to check against the server.
                        ["trustedKeyIds"] = new JArray(settings.TrustedReleaseKeys.Select(key => key?.KeyId ?? "")),
                    },
                    ["validation"] = new JObject
                    {
                        ["errors"] = report.ErrorCount,
                        ["warnings"] = report.WarningCount,
                        ["canPublish"] = report.CanPublish,
                        ["issues"] = new JArray(report.Issues.Select(issue => new JObject
                        {
                            ["code"] = issue.Code,
                            ["severity"] = issue.Severity.ToString(),
                            ["packageId"] = issue.PackageId ?? "",
                            ["message"] = issue.Message ?? "",
                        })),
                    },
                });
            }

            return new JObject { ["assets"] = assets }.ToString(Formatting.None);
        }

        // ── Action: molca_content_settings_edit ──────────────────────────────

        private static McpToolDefinition CreateContentSettingsEditTool() => new McpToolDefinition(
            name: "molca_content_settings_edit",
            description: "Authors ContentPackageSettings through the shared editing service. 'operation' "
                       + "is one of: add_package, remove_package, set_display_name, derive_display_name, "
                       + "set_labels, dedupe_labels, remove_empty_labels, dedupe_dependencies, "
                       + "remove_self_dependency, set_dependency, set_release_protocol_enabled, "
                       + "set_content_service_id, set_content_path_prefix, set_trusted_keys. Refuses "
                       + "assets inside a package or the SDK layer. Resolves no ambiguity: duplicate ids "
                       + "and wrong dependency edges stay for a human. One Undo entry per operation.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"operation\":{\"type\":\"string\"}," +
                "\"assetPath\":{\"type\":\"string\",\"description\":\"Target asset; required when the project has more than one.\"}," +
                "\"packageId\":{\"type\":\"string\"}," +
                "\"displayName\":{\"type\":\"string\"}," +
                "\"labels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
                "\"dependencyIndex\":{\"type\":\"integer\"},\"targetPackageId\":{\"type\":\"string\"}," +
                "\"enabled\":{\"type\":\"boolean\"}," +
                "\"serviceId\":{\"type\":\"string\"},\"pathPrefix\":{\"type\":\"string\"}," +
                "\"keys\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"keyId\":{\"type\":\"string\"},\"modulusBase64\":{\"type\":\"string\"}," +
                "\"exponentBase64\":{\"type\":\"string\"}},\"additionalProperties\":false}}}," +
                "\"required\":[\"operation\"],\"additionalProperties\":false}",
            execute: ExecuteContentSettingsEdit,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteContentSettingsEdit(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            string operation = (args.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(operation)) return Error("Supply an 'operation'.");

            var service = ResolveEditingService(args, out string resolveError);
            if (service == null) return Error(resolveError);

            string packageId = args.Value<string>("packageId");

            ContentEditResult result;
            try
            {
                result = Dispatch(service, operation, packageId, args);
            }
            catch (ArgumentException exception)
            {
                // A missing required argument is the caller's mistake, not a tool fault. Returned as
                // an error string so the assistant can correct and retry, rather than thrown into the
                // bridge where it surfaces as an opaque failure.
                return Error(exception.Message);
            }
            if (result == null) return Error($"Unknown operation '{operation}'.");

            if (result.Changed) AssetDatabase.SaveAssets();

            var validation = service.Validate();
            return new JObject
            {
                ["changed"] = result.Changed,
                ["message"] = result.Message,
                ["before"] = result.Before,
                ["after"] = result.After,
                // Returned every time so a caller sees immediately whether an edit resolved a finding
                // or introduced one -- the useful thing to know right after a write.
                ["validation"] = new JObject
                {
                    ["errors"] = validation.ErrorCount,
                    ["warnings"] = validation.WarningCount,
                    ["canPublish"] = validation.CanPublish,
                },
            }.ToString(Formatting.None);
        }

        private static ContentEditResult Dispatch(
            ContentPackageEditingService service, string operation, string packageId, JObject args)
        {
            switch (operation)
            {
                case "add_package":
                    return service.AddPackage(packageId);
                case "remove_package":
                    return service.RemovePackage(Require(packageId, "packageId"));
                case "set_display_name":
                    return service.SetDisplayName(
                        Require(packageId, "packageId"), args.Value<string>("displayName"));
                case "derive_display_name":
                    return service.DeriveDisplayNameFromId(Require(packageId, "packageId"));
                case "set_labels":
                    return service.SetLabels(
                        Require(packageId, "packageId"),
                        (args["labels"] as JArray)?.Select(token => token.ToString()) ?? Enumerable.Empty<string>());
                case "dedupe_labels":
                    return service.DedupeLabels(Require(packageId, "packageId"));
                case "remove_empty_labels":
                    return service.RemoveEmptyLabels(Require(packageId, "packageId"));
                case "dedupe_dependencies":
                    return service.DedupeDependencies(Require(packageId, "packageId"));
                case "remove_self_dependency":
                    return service.RemoveSelfDependency(Require(packageId, "packageId"));
                case "set_dependency":
                    return service.SetDependency(
                        Require(packageId, "packageId"),
                        args.Value<int?>("dependencyIndex") ?? -1,
                        args.Value<string>("targetPackageId"));
                case "set_release_protocol_enabled":
                    return service.SetReleaseProtocolEnabled(args.Value<bool?>("enabled") ?? false);
                case "set_content_service_id":
                    return service.SetContentServiceId(args.Value<string>("serviceId"));
                case "set_content_path_prefix":
                    return service.SetContentPathPrefix(args.Value<string>("pathPrefix"));
                case "set_trusted_keys":
                    return service.SetTrustedReleaseKeys(
                        ((args["keys"] as JArray) ?? new JArray())
                        .Select(token => new ReleaseTrustedKey
                        {
                            KeyId = token.Value<string>("keyId"),
                            ModulusBase64 = token.Value<string>("modulusBase64"),
                            ExponentBase64 = token.Value<string>("exponentBase64"),
                        }).ToList());
                default:
                    return null;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static System.Collections.Generic.IEnumerable<(ContentPackageSettings settings, string path)>
            FindContentSettings()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:ContentPackageSettings"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<ContentPackageSettings>(path);
                if (settings != null) yield return (settings, path);
            }
        }

        /// <summary>
        /// Resolves the settings asset to edit, refusing to pick when the choice is not obvious.
        /// </summary>
        /// <remarks>
        /// With several settings assets in a project, guessing which one an unqualified call meant
        /// would write to the wrong content configuration and report success. The caller is told to
        /// name one, and given the list.
        /// </remarks>
        private static ContentPackageEditingService ResolveEditingService(JObject args, out string error)
        {
            error = null;
            string assetPath = args.Value<string>("assetPath");
            var found = FindContentSettings().ToList();

            if (found.Count == 0)
            {
                error = "No ContentPackageSettings asset exists in this project.";
                return null;
            }

            if (!string.IsNullOrEmpty(assetPath))
            {
                var match = found.FirstOrDefault(entry =>
                    string.Equals(entry.path, assetPath, StringComparison.OrdinalIgnoreCase));
                if (match.settings == null)
                {
                    error = $"No ContentPackageSettings at '{assetPath}'. Found: {string.Join(", ", found.Select(e => e.path))}";
                    return null;
                }
                return Writable(match.settings, match.path, ref error);
            }

            if (found.Count > 1)
            {
                error = "Several ContentPackageSettings assets exist; supply 'assetPath'. Found: " +
                        string.Join(", ", found.Select(entry => entry.path));
                return null;
            }

            return Writable(found[0].settings, found[0].path, ref error);
        }

        private static ContentPackageEditingService Writable(
            ContentPackageSettings settings, string path, ref string error)
        {
            if (IsProtectedPath(path))
            {
                error = $"'{path}' is in a read-only protected zone.";
                return null;
            }

            var service = new ContentPackageEditingService(settings);
            string reason = service.ReadOnlyReason();
            if (reason != null)
            {
                error = reason;
                return null;
            }
            return service;
        }

        private static string Require(string value, string name) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException($"Supply '{name}'.")
                : value;
    }
}
