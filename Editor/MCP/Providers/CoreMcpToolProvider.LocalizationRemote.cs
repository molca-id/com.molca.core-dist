using System;
using System.Linq;
using Molca.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>Remote localization configuration, runtime status, and allowlist repair tools.</summary>
    public partial class CoreMcpToolProvider
    {
        private static McpToolDefinition CreateLocalizationRemoteStatusTool() => new(
            name: "molca_localization_remote_status",
            description: "Reports the remote localization settings, trust/allowlist readiness, and the "
                       + "active runtime overlay without returning credentials or private signing material.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: _ => ExecuteLocalizationRemoteStatus());

        private static McpToolDefinition CreateLocalizationSyncRemoteAllowlistTool() => new(
            name: "molca_localization_sync_remote_allowlist",
            description: "Rebuilds the shipped remote-catalog identity and placeholder allowlist from "
                       + "the current stable Unity StringTable identities. One Unity Undo transaction.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: _ => ExecuteLocalizationSyncRemoteAllowlist(),
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteLocalizationRemoteStatus()
        {
            var settings = ResolveRemoteCatalogSettings();
            var active = LocalizationManager.ActiveOverlay;
            return new JObject
            {
                ["configured"] = settings != null,
                ["enabled"] = settings?.Enabled == true,
                ["projectId"] = settings?.ProjectId ?? string.Empty,
                ["channel"] = settings?.Channel ?? string.Empty,
                ["manifestSource"] = string.IsNullOrWhiteSpace(settings?.ManifestUrl)
                    ? "licensed-server-default"
                    : settings.ManifestUrl,
                ["trustedKeyCount"] = settings?.TrustedKeys.Count ?? 0,
                ["allowlistedIdentityCount"] = settings?.AllowedEntries.Count ?? 0,
                ["retainedVersions"] = settings?.RetainedVersions ?? 0,
                ["runtimeStatus"] = LocalizationManager.OverlayStatus.ToString(),
                ["activeVersion"] = active?.Version ?? string.Empty,
                ["activeChannel"] = active?.Channel ?? string.Empty,
            }.ToString(Formatting.None);
        }

        private static string ExecuteLocalizationSyncRemoteAllowlist()
        {
            var settings = ResolveRemoteCatalogSettings();
            if (settings == null)
                return Error(
                    "No LocalizationRemoteCatalogSettings is assigned to a LocalizationModule.");
            var count = LocalizationRemoteCatalogAuthoringService.SyncAllowlist(settings);
            return new JObject
            {
                ["updated"] = true,
                ["identityCount"] = count,
                ["settingsPath"] = AssetDatabase.GetAssetPath(settings),
                ["revertible"] = "unity-undo",
            }.ToString(Formatting.None);
        }

        private static LocalizationRemoteCatalogSettings ResolveRemoteCatalogSettings() =>
            AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationModule>)
                .Where(module => module != null)
                .Select(module => module.RemoteCatalog)
                .FirstOrDefault(settings => settings != null);
    }
}
