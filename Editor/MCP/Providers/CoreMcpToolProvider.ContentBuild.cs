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
    /// authoring tools for the <b>legacy schema-v1 delivery path</b>: create/patch a
    /// <see cref="ContentPackageBuildConfig"/>, build Addressables content (full or incremental) and write
    /// <c>packages.json</c>, and verify per-package bundle output.
    /// </summary>
    /// <remarks>
    /// There is no deploy tool. <c>molca_content_deploy</c> spawned an external <c>aws</c>/<c>gcloud</c>
    /// process configured by a storage-provider asset held in the project — the credential handling the
    /// release protocol exists to remove. Publishing goes through the Hub's Content workspace, which
    /// uploads to short-lived presigned URLs.
    ///
    /// <c>molca_content_build</c> is a heavy, disk-bound <see cref="McpToolReversibility.Irreversible"/>
    /// <see cref="McpToolKind.Action"/> tool (allowlist + confirmation gated). It wraps
    /// <see cref="AddressablesBuildUtility"/> and mirrors the inspector's Build panel. It no longer syncs
    /// Addressables profile paths: that wrote into a shared, version-controlled asset on every build.
    /// Build-config resolution mirrors the inspector: the active config is
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
                       + "remote load URL. Edit mode only; "
                       + "revert with Ctrl+Z.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"configPath\":{\"type\":\"string\",\"description\":\"Build config asset path; omit to use the active one.\"}," +
                "\"localBuildPath\":{\"type\":\"string\"}," +
                "\"remoteLoadURL\":{\"type\":\"string\"}}," +
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
            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssetIfDirty(cfg);
            return BuildConfigToJson(cfg, AssetDatabase.GetAssetPath(cfg));
        }

        // ── molca_content_build ──────────────────────────────────────────────────────────────

        private static McpToolDefinition CreateContentBuildTool() => new McpToolDefinition(
            name: "molca_content_build",
            description: "Builds Addressables content for the content packages and writes packages.json. "
                       + "Full build by default; pass incremental=true to rebuild only changed groups (requires "
                       + "a prior full build). Reports (never rewrites) any Addressables profile mismatch. "
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

            // Reported, never written. This used to rewrite the shared AddressableAssetSettings asset
            // from the build config -- version-controlled configuration silently changed to whichever
            // machine built last, surfacing as a diff in someone else's commit. An automated build is
            // the worst place for that, because nobody is watching it happen.
            string profileMismatch = DescribeProfileMismatch(addrSettings, cfg);

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
                ["error"] = result.ErrorMessage,
                ["profileMismatch"] = profileMismatch
            }.ToString(Formatting.None);
        }

        // ── molca_content_verify (read-only) ─────────────────────────────────────────────────

        private static McpToolDefinition CreateContentVerifyTool() => new McpToolDefinition(
            name: "molca_content_verify",
            description: "Verifies the last build: for each package, reports the number of bundles "
                       + "and total bytes found in the build output for its labels, including hidden "
                       + "packages. Read-only.",
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
            // Every package, not only the visible ones. Visibility affects presentation, not
            // correctness -- a hidden *required* package is still built, uploaded, and installed, and
            // skipping it here meant the one package a player cannot run without went unverified.
            foreach (var pkg in settings.packageConfigs.Where(config => config != null &&
                                                              !string.IsNullOrEmpty(config.packageId)))
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
        /// Describes where the active Addressables profile disagrees with the build config, or null.
        /// </summary>
        /// <remarks>
        /// Read-only by design; see the call site. The result is returned in the build response so an
        /// automated caller can act on it, rather than discovering later that its build used paths
        /// nobody set deliberately.
        /// </remarks>
        private static string DescribeProfileMismatch(
            AddressableAssetSettings addrSettings, ContentPackageBuildConfig cfg)
        {
            if (addrSettings == null || cfg == null) return null;

            var profileId = addrSettings.activeProfileId;
            var problems = new System.Collections.Generic.List<string>();

            void Compare(string key, string expected)
            {
                string actual = addrSettings.profileSettings.GetValueByName(profileId, key);
                if (actual == null) problems.Add($"Profile variable '{key}' does not exist.");
                else if (!string.Equals(actual, expected, System.StringComparison.Ordinal))
                    problems.Add($"'{key}' is '{actual}', build config expects '{expected}'.");
            }

            Compare("RemoteBuildPath", cfg.localBuildPath);
            Compare("RemoteLoadPath", cfg.remoteLoadURL);
            if (!addrSettings.BuildRemoteCatalog)
                problems.Add("Build Remote Catalog is off, so no catalog will be produced.");

            return problems.Count == 0 ? null : string.Join(" ", problems);
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
            var obj = new JObject
            {
                ["path"] = path,
                ["localBuildPath"] = cfg.localBuildPath,
                ["remoteLoadURL"] = cfg.remoteLoadURL,
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
