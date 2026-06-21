using System.Diagnostics;
using System.IO;
using System.Linq;
using Molca.ContentPackage;
using Molca.Editor.ContentPackage;
using Molca.Editor.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Content Package build/deploy MCP tool family (Tier 3) — the edit-time pipeline complement to the
    /// authoring tools: create/patch a <see cref="ContentPackageBuildConfig"/>, build Addressables content
    /// (full or incremental) and write <c>packages.json</c>, verify per-package bundle output, and deploy
    /// the build folder to the configured CDN via the storage provider's CLI.
    /// </summary>
    /// <remarks>
    /// These are heavy, network/disk-bound operations: <c>molca_content_build</c> and
    /// <c>molca_content_deploy</c> are <see cref="McpToolReversibility.Irreversible"/>
    /// <see cref="McpToolKind.Action"/> tools (allowlist + confirmation gated, Sprint 17). Build wraps
    /// <see cref="AddressablesBuildUtility"/> and mirrors the inspector's Build &amp; Deploy panel, including
    /// the Addressables profile-path sync. Deploy spawns the provider's external CLI process on a background
    /// thread per the async contract. Build-config resolution mirrors the inspector: the active config is
    /// the one stored under the inspector's EditorPrefs GUID, else the project's single config, else pass
    /// <c>configPath</c> explicitly.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        /// <summary>EditorPrefs key (matches the inspector) holding the active build config's asset GUID.</summary>
        private const string BuildConfigPrefKey = "Molca.ContentPackage.BuildConfigGuid";

        // ── molca_content_create_build_config ────────────────────────────────────────────────

        private static McpToolDefinition CreateContentCreateBuildConfigTool() => new McpToolDefinition(
            name: "molca_content_create_build_config",
            description: "Creates (or updates if it already exists) a ContentPackageBuildConfig asset at "
                       + "'path' and sets the local build path and remote load URL. Marks it as the active "
                       + "build config used by the other build tools. Edit mode only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"path\":{\"type\":\"string\",\"description\":\"Asset path, e.g. 'Assets/Content/ContentPackageBuildConfig.asset'.\"}," +
                "\"localBuildPath\":{\"type\":\"string\",\"description\":\"Local bundle output folder (use [BuildTarget] token).\"}," +
                "\"remoteLoadURL\":{\"type\":\"string\",\"description\":\"Runtime CDN URL the app loads bundles from.\"}," +
                "\"makeActive\":{\"type\":\"boolean\",\"description\":\"Set as the active build config (default true).\"}}," +
                "\"required\":[\"path\"],\"additionalProperties\":false}",
            execute: ExecuteContentCreateBuildConfig,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteContentCreateBuildConfig(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var path = args.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path)) return Error("'path' is required.");
            if (!path.EndsWith(".asset")) return Error("'path' must end with '.asset'.");

            var cfg = AssetDatabase.LoadAssetAtPath<ContentPackageBuildConfig>(path);
            bool created = cfg == null;
            if (created)
            {
                var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                    return Error($"Folder '{dir}' does not exist. Create it first.");
                cfg = ScriptableObject.CreateInstance<ContentPackageBuildConfig>();
                AssetDatabase.CreateAsset(cfg, path);
            }

            if (args["localBuildPath"] != null) cfg.localBuildPath = args.Value<string>("localBuildPath");
            if (args["remoteLoadURL"] != null)  cfg.remoteLoadURL  = args.Value<string>("remoteLoadURL");

            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();

            bool makeActive = args["makeActive"] == null || args.Value<bool>("makeActive");
            if (makeActive)
                MolcaEditorPrefs.SetString(BuildConfigPrefKey, AssetDatabase.AssetPathToGUID(path));

            return BuildConfigToJson(cfg, path, new JObject { ["created"] = created, ["active"] = makeActive });
        }

        // ── molca_content_set_build_config ───────────────────────────────────────────────────

        private static McpToolDefinition CreateContentSetBuildConfigTool() => new McpToolDefinition(
            name: "molca_content_set_build_config",
            description: "Patches the active (or specified) ContentPackageBuildConfig: local build path, "
                       + "remote load URL, and/or storage provider (by asset path). Edit mode only; "
                       + "revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"configPath\":{\"type\":\"string\",\"description\":\"Build config asset path; omit to use the active one.\"}," +
                "\"localBuildPath\":{\"type\":\"string\"}," +
                "\"remoteLoadURL\":{\"type\":\"string\"}," +
                "\"storageProviderPath\":{\"type\":\"string\",\"description\":\"Asset path of a ContentPackageStorageProvider.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteContentSetBuildConfig,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.UnityUndo);

        private static string ExecuteContentSetBuildConfig(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var cfg = ResolveBuildConfig(args.Value<string>("configPath"), out var error);
            if (cfg == null) return Error(error);

            Undo.RecordObject(cfg, "Set Content Build Config");

            if (args["localBuildPath"] != null) cfg.localBuildPath = args.Value<string>("localBuildPath");
            if (args["remoteLoadURL"] != null)  cfg.remoteLoadURL  = args.Value<string>("remoteLoadURL");
            if (args["storageProviderPath"] != null)
            {
                var provPath = args.Value<string>("storageProviderPath");
                var provider = AssetDatabase.LoadAssetAtPath<ContentPackageStorageProvider>(provPath);
                if (provider == null) return Error($"No ContentPackageStorageProvider at '{provPath}'.");
                cfg.storageProvider = provider;
            }

            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssetIfDirty(cfg);
            return BuildConfigToJson(cfg, AssetDatabase.GetAssetPath(cfg));
        }

        // ── molca_content_build ──────────────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentBuildTool() => new McpToolDefinition(
            name: "molca_content_build",
            description: "Builds Addressables content for the content packages and writes packages.json. "
                       + "Full build by default; pass incremental=true to rebuild only changed groups (requires "
                       + "a prior full build). Syncs Addressables profile paths from the build config first. "
                       + "Edit mode only; writes build artifacts to disk (not undoable).",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"configPath\":{\"type\":\"string\",\"description\":\"Build config asset path; omit to use the active one.\"}," +
                "\"incremental\":{\"type\":\"boolean\",\"description\":\"Content-update build of changed groups only (default false).\"}," +
                "\"clean\":{\"type\":\"boolean\",\"description\":\"Clean player content before a full build (default false; ignored for incremental).\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteContentBuild,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static string ExecuteContentBuild(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var cfg = ResolveBuildConfig(args.Value<string>("configPath"), out var cfgError);
            if (cfg == null) return Error(cfgError);

            var settings = ResolveContentSettings(out var setError);
            if (settings == null) return Error(setError);

            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings == null) return Error("Addressables is not configured in this project.");

            bool incremental = args["incremental"] != null && args.Value<bool>("incremental");
            bool clean       = args["clean"] != null && args.Value<bool>("clean");

            McpProgress.Report("Syncing Addressables profile paths…", 0.1f, "build");
            SyncAddressablesPaths(addrSettings, cfg);

            var options = new AddressablesBuildUtility.BuildOptions
            {
                ProfileName = AddressablesBuildUtility.GetActiveProfileName(),
                CleanBuild  = clean,
            };

            AddressablesBuildUtility.BuildResult result;
            if (incremental)
            {
                var binPath = ContentUpdateStatePath();
                if (string.IsNullOrEmpty(binPath) || !File.Exists(binPath))
                    return Error("No previous build state found; run a full build (incremental=false) first.");
                McpProgress.Report("Building changed Addressables groups…", 0.3f, "build");
                result = AddressablesBuildUtility.BuildContentUpdate(binPath, options);
            }
            else
            {
                McpProgress.Report("Building Addressables content…", 0.3f, "build");
                result = AddressablesBuildUtility.BuildAllContent(options);
            }

            if (result.Success && !string.IsNullOrEmpty(result.BuildPath))
            {
                McpProgress.Report("Writing package manifest…", 0.85f, "build");
                AddressablesBuildUtility.WritePackageManifest(result.BuildPath, settings, addrSettings, cfg);
            }

            McpProgress.Report(result.Success ? "Build complete." : "Build failed.", 1f, "build");

            return new JObject
            {
                ["incremental"] = incremental,
                ["success"] = result.Success,
                ["buildPath"] = result.BuildPath,
                ["totalBytes"] = result.TotalSize,
                ["durationSeconds"] = result.Duration,
                ["builtGroups"] = new JArray(result.BuiltGroups),
                ["error"] = result.ErrorMessage
            }.ToString(Formatting.None);
        }

        // ── molca_content_verify (read-only) ─────────────────────────────────────────────────

        private static McpToolDefinition CreateContentVerifyTool() => new McpToolDefinition(
            name: "molca_content_verify",
            description: "Verifies the last build: for each visible package, reports the number of bundles "
                       + "and total bytes found in the build output for its labels. Read-only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"configPath\":{\"type\":\"string\",\"description\":\"Build config asset path; omit to use the active one.\"}}," +
                "\"additionalProperties\":false}",
            execute: ExecuteContentVerify,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static string ExecuteContentVerify(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var cfg = ResolveBuildConfig(args.Value<string>("configPath"), out var cfgError);
            if (cfg == null) return Error(cfgError);

            var settings = ResolveContentSettings(out var setError);
            if (settings == null) return Error(setError);

            var buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            var buildPath = cfg.ResolvedLocalBuildPath(buildTarget);
            if (!Directory.Exists(buildPath))
                return Error($"No build output found at '{buildPath}'. Run molca_content_build first.");

            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings == null) return Error("Addressables is not configured in this project.");

            var packages = new JArray();
            foreach (var pkg in settings.GetVisiblePackages())
            {
                if (string.IsNullOrEmpty(pkg.packageId)) continue;

                if (pkg.addressableLabels == null || pkg.addressableLabels.Length == 0)
                {
                    packages.Add(new JObject
                    {
                        ["packageId"] = pkg.packageId, ["bundles"] = 0, ["bytes"] = 0,
                        ["ok"] = false, ["error"] = "no labels configured"
                    });
                    continue;
                }

                var (count, bytes) = AddressablesBuildUtility.GetPackageBundleInfo(pkg, addrSettings, buildPath);
                packages.Add(new JObject
                {
                    ["packageId"] = pkg.packageId,
                    ["bundles"] = count,
                    ["bytes"] = bytes,
                    ["ok"] = count > 0,
                    ["error"] = count > 0 ? null : "no bundles found"
                });
            }

            return new JObject { ["buildPath"] = buildPath, ["packages"] = packages }.ToString(Formatting.None);
        }

        // ── molca_content_deploy ─────────────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentDeployTool() => new McpToolDefinition(
            name: "molca_content_deploy",
            description: "Deploys the local build output to the configured CDN by running the storage "
                       + "provider's CLI (e.g. aws/gsutil). Uploads bytes to a remote bucket — irreversible. "
                       + "Requires a built output folder and a storage provider assigned on the build config. "
                       + "Edit mode only.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"configPath\":{\"type\":\"string\",\"description\":\"Build config asset path; omit to use the active one.\"}}," +
                "\"additionalProperties\":false}",
            executeAsync: ExecuteContentDeployAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private static async Awaitable<string> ExecuteContentDeployAsync(string argumentsJson)
        {
            var args = ParseArgs(argumentsJson);
            var cfg = ResolveBuildConfig(args.Value<string>("configPath"), out var cfgError);
            if (cfg == null) return Error(cfgError);

            var provider = cfg.storageProvider;
            if (provider == null)
                return Error("No storage provider assigned on the build config. Set one with molca_content_set_build_config.");

            var buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            var localPath = cfg.ResolvedLocalBuildPath(buildTarget);
            if (!Directory.Exists(localPath))
                return Error($"Build output folder not found at '{localPath}'. Run molca_content_build first.");

            McpProgress.Report("Checking storage provider…", 0.1f, "deploy");
            if (!provider.CheckAvailability(out var availError))
                return Error($"Storage provider not ready: {availError}");

            var fileName  = provider.ExecutableName;
            var arguments = provider.BuildDeployArguments(localPath, buildTarget);
            var command   = provider.BuildDeployCommand(localPath, buildTarget);
            Debug.Log($"[ContentPackage] Deploy (MCP): {command}");

            int exitCode;
            string stdout, stderr;

            // Duration is unknown (depends on bundle size + network), so report indeterminate progress
            // before handing off; the main thread is free during the upload so the row stays live.
            McpProgress.Report($"Uploading to {provider.GetDestinationDescription(buildTarget)}…", null, "deploy");

            // The CLI upload blocks; run it off the main thread per the async contract.
            await Awaitable.BackgroundThreadAsync();
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }
            catch (System.Exception ex)
            {
                await Awaitable.MainThreadAsync();
                return Error($"Deploy failed to launch '{fileName}': {ex.Message}");
            }
            await Awaitable.MainThreadAsync();

            McpProgress.Report(exitCode == 0 ? "Deploy complete." : $"Deploy failed (exit {exitCode}).", 1f, "deploy");

            return new JObject
            {
                ["provider"] = provider.DisplayName,
                ["command"] = command,
                ["destination"] = provider.GetDestinationDescription(buildTarget),
                ["exitCode"] = exitCode,
                ["success"] = exitCode == 0,
                ["stdout"] = Tail(stdout, 4000),
                ["stderr"] = Tail(stderr, 4000)
            }.ToString(Formatting.None);
        }

        // ── Shared plumbing ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the build config: an explicit <paramref name="explicitPath"/>, else the active one
        /// (inspector EditorPrefs GUID), else the project's single config; otherwise sets an error.
        /// </summary>
        private static ContentPackageBuildConfig ResolveBuildConfig(string explicitPath, out string error)
        {
            error = null;

            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var atPath = AssetDatabase.LoadAssetAtPath<ContentPackageBuildConfig>(explicitPath);
                if (atPath == null) error = $"No ContentPackageBuildConfig at '{explicitPath}'.";
                return atPath;
            }

            var guid = MolcaEditorPrefs.GetString(BuildConfigPrefKey, "");
            if (!string.IsNullOrEmpty(guid))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(p))
                {
                    var active = AssetDatabase.LoadAssetAtPath<ContentPackageBuildConfig>(p);
                    if (active != null) return active;
                }
            }

            var guids = AssetDatabase.FindAssets("t:ContentPackageBuildConfig");
            if (guids.Length == 0)
            {
                error = "No ContentPackageBuildConfig asset found. Create one with molca_content_create_build_config.";
                return null;
            }
            if (guids.Length > 1)
            {
                error = "Multiple ContentPackageBuildConfig assets found; pass 'configPath' to choose one.";
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<ContentPackageBuildConfig>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        /// <summary>
        /// Syncs the active Addressables profile's remote build/load paths and remote-catalog settings from
        /// the build config — a static port of the inspector's pre-build sync so the MCP build matches it.
        /// </summary>
        private static void SyncAddressablesPaths(AddressableAssetSettings addrSettings, ContentPackageBuildConfig cfg)
        {
            var profileId = addrSettings.activeProfileId;

            void SetVar(string key, string value)
            {
                if (addrSettings.profileSettings.GetValueByName(profileId, key) != null)
                    addrSettings.profileSettings.SetValue(profileId, key, value);
                else
                    Debug.LogWarning($"[ContentPackage] Addressables profile variable '{key}' not found. Create it in the Addressables Profiles window.");
            }

            SetVar("RemoteBuildPath", cfg.localBuildPath);
            SetVar("RemoteLoadPath",  cfg.remoteLoadURL);

            addrSettings.BuildRemoteCatalog = true;
            addrSettings.RemoteCatalogBuildPath.SetVariableByName(addrSettings, "RemoteBuildPath");
            addrSettings.RemoteCatalogLoadPath.SetVariableByName(addrSettings, "RemoteLoadPath");
            EditorUtility.SetDirty(addrSettings);
        }

        /// <summary>Path to the Addressables content-state file used as the incremental-build baseline.</summary>
        private static string ContentUpdateStatePath()
        {
            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings == null) return "";
            return ContentUpdateScript.GetContentStateDataPath(false, addrSettings);
        }

        private static string BuildConfigToJson(ContentPackageBuildConfig cfg, string path, JObject extra = null)
        {
            var providerPath = cfg.storageProvider != null ? AssetDatabase.GetAssetPath(cfg.storageProvider) : null;
            var obj = new JObject
            {
                ["path"] = path,
                ["localBuildPath"] = cfg.localBuildPath,
                ["remoteLoadURL"] = cfg.remoteLoadURL,
                ["storageProvider"] = providerPath,
                ["storageProviderName"] = cfg.storageProvider != null ? cfg.storageProvider.DisplayName : null
            };
            if (extra != null)
                foreach (var prop in extra.Properties())
                    obj[prop.Name] = prop.Value;
            return obj.ToString(Formatting.None);
        }

        /// <summary>Returns the last <paramref name="maxChars"/> characters of <paramref name="text"/>.</summary>
        private static string Tail(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
            return "…" + text.Substring(text.Length - maxChars);
        }
    }
}
