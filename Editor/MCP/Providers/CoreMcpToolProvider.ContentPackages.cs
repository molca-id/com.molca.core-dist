using System.Linq;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Content Package (DLC) MCP tool family (Sprint 21): a read-only introspection half (list, download
    /// size, queue status) and an action half (install / uninstall / update / switch / cancel + queue
    /// control) over the runtime <see cref="PackageService"/> and <see cref="PackageDownloadQueue"/>
    /// (Sprints 12–13). All tools are <see cref="McpToolMode.Play"/> — the service only exists once
    /// <c>RuntimeManager</c> has bootstrapped the <see cref="PackageSubsystem"/>. Actions are
    /// <see cref="McpToolReversibility.Irreversible"/> (network/storage-bound) and, being
    /// <see cref="McpToolKind.Action"/>, are withheld from the assistant unless explicitly allowlisted.
    /// No Content Package runtime changes — this is purely the provider layer.
    /// </summary>
    public partial class CoreMcpToolProvider
    {
        // ── Read-only: molca_content_list (21.1) ─────────────────────────────────────────────

        private static McpToolDefinition CreateContentListTool() => new McpToolDefinition(
            name: "molca_content_list",
            description: "Lists available and installed content packages with their live state (status, "
                       + "version, progress, sizes). Play mode only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteContentList,
            mode: McpToolMode.Play,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteContentList(string argumentsJson)
        {
            var service = ResolvePackageService(out var error);
            if (service == null) return Error(error);

            return new JObject
            {
                ["available"] = StatesToJson(service.GetAvailablePackages()),
                ["installed"] = StatesToJson(service.GetInstalledPackages())
            }.ToString(Formatting.None);
        }

        // ── Read-only: molca_content_download_size (21.2) ────────────────────────────────────

        private static McpToolDefinition CreateContentDownloadSizeTool() => new McpToolDefinition(
            name: "molca_content_download_size",
            description: "Reports the download size of a package plus current cache usage and available "
                       + "disk, so you can tell whether it will fit before installing. Play mode only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"packageId\":{\"type\":\"string\",\"description\":\"Package id to size.\"}}," +
                "\"required\":[\"packageId\"],\"additionalProperties\":false}",
            executeAsync: ExecuteContentDownloadSizeAsync,
            mode: McpToolMode.Play,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteContentDownloadSizeAsync(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var service = ResolvePackageService(out var error);
            if (service == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            long size = await service.GetDownloadSizeAsync(packageId);
            long available = service.GetAvailableDiskBytes();
            return new JObject
            {
                ["packageId"] = packageId,
                ["downloadBytes"] = size,
                ["cacheUsageBytes"] = service.GetCacheUsageBytes(),
                ["availableDiskBytes"] = available,
                ["fits"] = size <= available
            }.ToString(Formatting.None);
        }

        // ── Read-only: molca_content_queue_status (21.7) ─────────────────────────────────────

        private static McpToolDefinition CreateContentQueueStatusTool() => new McpToolDefinition(
            name: "molca_content_queue_status",
            description: "Reports the download queue: per-item status, queued/active/completed/failed "
                       + "counts, paused flag, and aggregate progress. Play mode only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: ExecuteContentQueueStatus,
            mode: McpToolMode.Play,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteContentQueueStatus(string argumentsJson)
        {
            var queue = ResolveDownloadQueue(out var error);
            if (queue == null) return Error(error);

            var snapshot = queue.GetSnapshot();
            var items = new JArray();
            foreach (var item in snapshot.Items)
            {
                items.Add(new JObject
                {
                    ["packageId"] = item.PackageId,
                    ["status"] = item.Status.ToString(),
                    ["progress"] = item.Progress,
                    ["error"] = item.Error
                });
            }

            return new JObject
            {
                ["queuedCount"] = snapshot.QueuedCount,
                ["activeCount"] = snapshot.ActiveCount,
                ["completedCount"] = snapshot.CompletedCount,
                ["failedCount"] = snapshot.FailedCount,
                ["isPaused"] = snapshot.IsPaused,
                ["aggregateProgress"] = snapshot.AggregateProgress,
                ["items"] = items
            }.ToString(Formatting.None);
        }

        // ── Action: molca_content_install (21.3) ─────────────────────────────────────────────

        private static McpToolDefinition CreateContentInstallTool() => new McpToolDefinition(
            name: "molca_content_install",
            description: "Installs a content package (downloads its content). Refuses if the download will "
                       + "not fit in available disk. Play mode only.",
            inputSchemaJson: SinglePackageSchema,
            executeAsync: ExecuteContentInstallAsync,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteContentInstallAsync(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var service = ResolvePackageService(out var error);
            if (service == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            // Pre-install space check (13.2): bail before downloading if it cannot fit.
            long size = await service.GetDownloadSizeAsync(packageId);
            long available = service.GetAvailableDiskBytes();
            if (size > available)
                return Error($"Insufficient disk for '{packageId}': needs {size} bytes, {available} available.");

            var result = await service.InstallPackageAsync(packageId);
            return OperationToJson(result, packageId, extra: new JObject { ["downloadBytes"] = size });
        }

        // ── Action: molca_content_uninstall (21.4) ───────────────────────────────────────────

        private static McpToolDefinition CreateContentUninstallTool() => new McpToolDefinition(
            name: "molca_content_uninstall",
            description: "Uninstalls a content package. Refused if another installed package depends on it "
                       + "or it is required (per ValidatePackageUninstallation). Play mode only.",
            inputSchemaJson: SinglePackageSchema,
            executeAsync: ExecuteContentUninstallAsync,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteContentUninstallAsync(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var service = ResolvePackageService(out var error);
            if (service == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            // Refuse if validation (dependents / required) fails — surfaces the break before acting.
            var validation = service.ValidatePackageUninstallation(packageId);
            if (!validation.Success)
                return Error($"Cannot uninstall '{packageId}': {validation.ErrorMessage}");

            var result = await service.UninstallPackageAsync(packageId);
            return OperationToJson(result, packageId);
        }

        // ── Action: molca_content_update (21.5) ──────────────────────────────────────────────

        private static McpToolDefinition CreateContentUpdateTool() => new McpToolDefinition(
            name: "molca_content_update",
            description: "Updates an installed content package to the latest catalog version. Play mode only.",
            inputSchemaJson: SinglePackageSchema,
            executeAsync: ExecuteContentUpdateAsync,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteContentUpdateAsync(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var service = ResolvePackageService(out var error);
            if (service == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            var result = await service.UpdatePackageAsync(packageId);
            return OperationToJson(result, packageId);
        }

        // ── Action: molca_content_switch_version (21.5) ──────────────────────────────────────

        private static McpToolDefinition CreateContentSwitchVersionTool() => new McpToolDefinition(
            name: "molca_content_switch_version",
            description: "Switches the active content version. Non-required optional packages not present "
                       + "in the target version are dropped (re-downloadable later). Play mode only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"targetVersion\":{\"type\":\"string\",\"description\":\"Content version to switch to.\"}}," +
                "\"required\":[\"targetVersion\"],\"additionalProperties\":false}",
            executeAsync: ExecuteContentSwitchVersionAsync,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteContentSwitchVersionAsync(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var service = ResolvePackageService(out var error);
            if (service == null) return Error(error);

            var targetVersion = args.Value<string>("targetVersion");
            if (string.IsNullOrWhiteSpace(targetVersion)) return Error("'targetVersion' is required.");

            var result = await service.SwitchContentVersionAsync(targetVersion);
            return OperationToJson(result, targetVersion);
        }

        // ── Action: molca_content_cancel (21.6) ──────────────────────────────────────────────

        private static McpToolDefinition CreateContentCancelTool() => new McpToolDefinition(
            name: "molca_content_cancel",
            description: "Cancels an in-flight install for a package (via its stored cancellation source). "
                       + "Play mode only.",
            inputSchemaJson: SinglePackageSchema,
            execute: ExecuteContentCancel,
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteContentCancel(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var service = ResolvePackageService(out var error);
            if (service == null) return Error(error);

            var packageId = args.Value<string>("packageId");
            if (string.IsNullOrWhiteSpace(packageId)) return Error("'packageId' is required.");

            bool cancelled = service.CancelPackageInstall(packageId);
            return new JObject
            {
                ["packageId"] = packageId,
                ["cancelled"] = cancelled,
                ["message"] = cancelled ? "Install cancellation requested." : "No in-flight install for that package."
            }.ToString(Formatting.None);
        }

        // ── Action: queue control molca_content_queue_pause/_resume/_cancel_all (21.7) ───────

        private static McpToolDefinition CreateContentQueuePauseTool() => QueueActionTool(
            "molca_content_queue_pause", "Pauses the content download queue (no new downloads start).",
            q => { q.Pause(); return "paused"; });

        private static McpToolDefinition CreateContentQueueResumeTool() => QueueActionTool(
            "molca_content_queue_resume", "Resumes a paused content download queue.",
            q => { q.Resume(); return "resumed"; });

        private static McpToolDefinition CreateContentQueueCancelAllTool() => QueueActionTool(
            "molca_content_queue_cancel_all", "Cancels all queued and active content downloads.",
            q => { q.CancelAll(); return "cancelledAll"; });

        private static McpToolDefinition QueueActionTool(string name, string description,
            System.Func<PackageDownloadQueue, string> action) => new McpToolDefinition(
            name: name,
            description: description + " Play mode only.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            execute: _ =>
            {
                var queue = ResolveDownloadQueue(out var error);
                if (queue == null) return Error(error);
                var outcome = action(queue);
                return new JObject { ["result"] = outcome, ["isPaused"] = queue.IsPaused }.ToString(Formatting.None);
            },
            mode: McpToolMode.Play,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        // ── Shared plumbing ──────────────────────────────────────────────────────────────────

        private const string SinglePackageSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"packageId\":{\"type\":\"string\",\"description\":\"Package id.\"}}," +
            "\"required\":[\"packageId\"],\"additionalProperties\":false}";

        /// <summary>Resolves the runtime <see cref="PackageService"/>, or sets an error if unavailable.</summary>
        private static PackageService ResolvePackageService(out string error)
        {
            error = null;
            var subsystem = RuntimeManager.GetSubsystem<PackageSubsystem>();
            if (subsystem == null)
            {
                error = "PackageSubsystem not found (is it on the RuntimeManager prefab and is the app running?).";
                return null;
            }
            if (subsystem.PackageService == null)
            {
                error = "PackageService is not initialized yet.";
                return null;
            }
            return subsystem.PackageService;
        }

        /// <summary>Resolves the runtime <see cref="PackageDownloadQueue"/>, or sets an error if unavailable.</summary>
        private static PackageDownloadQueue ResolveDownloadQueue(out string error)
        {
            error = null;
            var subsystem = RuntimeManager.GetSubsystem<PackageSubsystem>();
            if (subsystem == null)
            {
                error = "PackageSubsystem not found (is it on the RuntimeManager prefab and is the app running?).";
                return null;
            }
            if (subsystem.DownloadQueue == null)
            {
                error = "Download queue is not initialized yet.";
                return null;
            }
            return subsystem.DownloadQueue;
        }

        private static JArray StatesToJson(System.Collections.Generic.IEnumerable<PackageState> states)
        {
            var arr = new JArray();
            foreach (var s in states)
            {
                if (s == null) continue;
                arr.Add(new JObject
                {
                    ["packageId"] = s.packageId,
                    ["status"] = s.status.ToString(),
                    ["installedVersion"] = s.installedVersion,
                    ["downloadProgress"] = s.downloadProgress,
                    ["downloadedBytes"] = s.downloadedBytes,
                    ["totalBytes"] = s.totalBytes,
                    ["installedSizeBytes"] = s.installedSizeBytes,
                    ["lastModified"] = s.lastModified,
                    ["errorMessage"] = s.errorMessage
                });
            }
            return arr;
        }

        private static string OperationToJson(OperationResult result, string subject, JObject extra = null)
        {
            var obj = new JObject
            {
                ["subject"] = subject,
                ["success"] = result.Success,
                ["cancelled"] = result.WasCancelled,
                ["error"] = result.ErrorMessage
            };
            if (extra != null)
                foreach (var prop in extra.Properties())
                    obj[prop.Name] = prop.Value;
            return obj.ToString(Formatting.None);
        }
    }
}
