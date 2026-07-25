using System.Collections.Generic;
using System.Linq;
using Molca.ContentPackage;
using Molca.Editor.ContentPackage;
using Molca.Editor.Doctor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Automation.BuiltIn
{
    /// <summary>
    /// The Content Verify workflow (§11.5): a composed, read-only pass over the project's content
    /// configuration — content-package settings, their dependency graph, and Addressables wiring — that
    /// returns one versioned evidence bundle instead of a caller scraping the Content Package editor. It
    /// reuses the existing <c>content-package-valid</c> Doctor check and
    /// <see cref="ContentPackageSettings.ValidateConfigurations"/> rather than re-deriving those rules.
    /// </summary>
    /// <remarks>
    /// This verifies <em>authoring</em>, not a deployment: it never builds Addressables, contacts a CDN,
    /// or checks bundle signatures. Deployment and bundle-signature verification are separately
    /// permissioned and deliberately excluded from the default preflight surface (§11.5). Because every
    /// step only reads assets, the workflow is classified <see cref="MolcaCommandKind.ReadOnly"/> and runs
    /// under any policy profile.
    /// </remarks>
    public static class ContentVerifyWorkflow
    {
        /// <summary>The stable command id of the Content Verify workflow.</summary>
        public const string Id = "molca.content-verify";

        /// <summary>The Doctor check id this workflow reuses for relational package validation.</summary>
        private const string ContentPackageCheckId = "content-package-valid";

        /// <summary>Builds the Content Verify workflow definition.</summary>
        /// <returns>The workflow definition.</returns>
        public static MolcaWorkflowDefinition Create() => new MolcaWorkflowDefinition(
            id: Id,
            displayName: "Content Verify",
            description: "Read-only content check: validates content-package configs, their dependency graph, and Addressables wiring.",
            steps: new[]
            {
                new MolcaWorkflowStep("configuration", "Validate every content-package config and its dependency graph.", ConfigurationStep),
                new MolcaWorkflowStep("addressables", "Confirm Addressables is configured and packages reference real labels.", AddressablesStep, critical: false),
                new MolcaWorkflowStep("delivery", "Check remote-manifest delivery configuration for authored packages.", DeliveryStep, critical: false),
            },
            mode: MolcaCommandMode.Edit,
            kind: MolcaCommandKind.ReadOnly);

        /// <summary>
        /// Aggregates <see cref="ContentPackageSettings.ValidateConfigurations"/> (missing ids/names, empty
        /// label lists) with the relational <c>content-package-valid</c> Doctor check (duplicate ids,
        /// unresolved/cyclic dependencies). Any error here is a critical failure — install resolution
        /// silently breaks otherwise.
        /// </summary>
        private static async Awaitable<MolcaStepResult> ConfigurationStep(MolcaCommandContext context)
        {
            var assets = LoadAllSettings();
            var diagnostics = new List<MolcaDiagnostic>();
            int packageCount = 0;

            foreach (var (path, settings) in assets)
            {
                packageCount += settings.packageConfigs?.Count ?? 0;
                foreach (var error in settings.ValidateConfigurations())
                    diagnostics.Add(new MolcaDiagnostic("content.config", error, MolcaDiagnosticSeverity.Error, path));
            }

            // Reuse the Doctor check for the relational rules (dup ids, dependency resolution, cycles).
            var issues = await MolcaDoctor.RunAllAsync(
                enabledIds: new HashSet<string> { ContentPackageCheckId },
                cancellationToken: context.CancellationToken);
            foreach (var issue in issues)
            {
                var severity = issue.Severity == DoctorSeverity.Error
                    ? MolcaDiagnosticSeverity.Error
                    : MolcaDiagnosticSeverity.Warning;
                diagnostics.Add(new MolcaDiagnostic(issue.CheckId, issue.Message, severity, issue.Path, issue.Line));
            }

            var data = new JObject
            {
                ["settingsAssetCount"] = assets.Count,
                ["packageCount"] = packageCount,
                ["errorCount"] = diagnostics.Count(d => d.Severity == MolcaDiagnosticSeverity.Error),
                ["warningCount"] = diagnostics.Count(d => d.Severity == MolcaDiagnosticSeverity.Warning)
            };

            return diagnostics.Any(d => d.Severity == MolcaDiagnosticSeverity.Error)
                ? MolcaStepResult.Fail(diagnostics, data)
                : MolcaStepResult.Pass(data, diagnostics);
        }

        /// <summary>
        /// Confirms Addressables is configured whenever content packages exist, and captures the group and
        /// profile inventory as evidence. Non-critical: authoring can precede Addressables setup, so a
        /// missing configuration is a warning rather than a hard failure at this stage.
        /// </summary>
        private static Awaitable<MolcaStepResult> AddressablesStep(MolcaCommandContext context)
        {
            var assets = LoadAllSettings();
            bool hasPackages = assets.Any(a => a.settings.packageConfigs != null && a.settings.packageConfigs.Count > 0);

            var groups = AddressablesBuildUtility.GetAllGroupNames();
            var profile = AddressablesBuildUtility.GetActiveProfileName();
            bool addressablesConfigured = profile != "None";

            var diagnostics = new List<MolcaDiagnostic>();
            if (hasPackages && !addressablesConfigured)
            {
                diagnostics.Add(new MolcaDiagnostic("content.addressables_missing",
                    "Content packages are authored but Addressables is not configured — their labels cannot resolve to bundles.",
                    MolcaDiagnosticSeverity.Warning));
            }
            else if (hasPackages && groups.Count == 0)
            {
                diagnostics.Add(new MolcaDiagnostic("content.addressables_no_groups",
                    "Addressables is configured but declares no bundled groups — packages have nothing to download.",
                    MolcaDiagnosticSeverity.Warning));
            }

            var data = new JObject
            {
                ["addressablesConfigured"] = addressablesConfigured,
                ["activeProfile"] = profile,
                ["groupCount"] = groups.Count,
                ["groups"] = new JArray(groups.Take(50))
            };
            return Completed(MolcaStepResult.Pass(data, diagnostics));
        }

        /// <summary>
        /// Checks that authored, deliverable packages have somewhere to be fetched from: a package that is
        /// visible to users but whose settings asset declares no remote manifest URL can never install at
        /// runtime. Warnings only — a project may intentionally ship all content locally.
        /// </summary>
        private static Awaitable<MolcaStepResult> DeliveryStep(MolcaCommandContext context)
        {
            var assets = LoadAllSettings();
            var diagnostics = new List<MolcaDiagnostic>();
            var perAsset = new JArray();

            foreach (var (path, settings) in assets)
            {
                int visible = settings.packageConfigs?.Count(c => c != null && c.isVisible) ?? 0;
                bool hasManifestUrl = !string.IsNullOrEmpty(settings.RemotePackagesManifestUrl);

                if (visible > 0 && !hasManifestUrl)
                {
                    diagnostics.Add(new MolcaDiagnostic("content.no_manifest_url",
                        $"{visible} visible package(s) but no RemotePackagesManifestUrl — remote content cannot be discovered at runtime.",
                        MolcaDiagnosticSeverity.Warning, path));
                }

                perAsset.Add(new JObject
                {
                    ["path"] = path,
                    ["visiblePackages"] = visible,
                    ["hasManifestUrl"] = hasManifestUrl,
                    ["contentVersioning"] = settings.EnableContentVersioning
                });
            }

            var data = new JObject { ["assets"] = perAsset };
            return Completed(MolcaStepResult.Pass(data, diagnostics));
        }

        /// <summary>Loads every <see cref="ContentPackageSettings"/> asset with its project path.</summary>
        private static List<(string path, ContentPackageSettings settings)> LoadAllSettings()
        {
            var results = new List<(string, ContentPackageSettings)>();
            foreach (var guid in AssetDatabase.FindAssets("t:ContentPackageSettings"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<ContentPackageSettings>(path);
                if (settings != null)
                    results.Add((path, settings));
            }
            return results;
        }

        /// <summary>Wraps a synchronous step result in an already-completed awaitable (no yield).</summary>
        private static Awaitable<MolcaStepResult> Completed(MolcaStepResult result)
        {
            var source = new AwaitableCompletionSource<MolcaStepResult>();
            source.SetResult(result);
            return source.Awaitable;
        }
    }
}
