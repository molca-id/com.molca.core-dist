using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Molca.Editor.Doctor;
using Molca.Editor.KnowledgeGraph;
using Molca.Editor.Mcp;
using Molca.Editor.UI;
using Molca.Editor.UI.Components;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Onboarding
{
    /// <summary>
    /// Reusable Onboarding Wizard UI as a <see cref="VisualElement"/>: a set of independent, idempotent
    /// setup steps hosted in the standalone <see cref="OnboardingWizardWindow"/>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Onboarding/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Implements the contract in <c>Documentation~/reference/ONBOARDING_WIZARD.md</c>: every action here
    /// writes only into consumer space (<c>Assets/</c>), never into <c>Packages/</c>, and nothing here is
    /// required for the project to compile or boot — <see cref="Molca.MolcaProjectSettings"/> already
    /// auto-seeds lazily on first access. Core must never hard-reference the SDK assembly (layering rule
    /// in <c>architecture.md</c>), so the SDK Quick Setup step is invoked through reflection and only
    /// shown when that type is actually present.
    /// </remarks>
    public sealed class OnboardingWizardView : VisualElement
    {
        private const string SdkQuickSetupTypeName = "MolcaSDK.Editor.Setup.QuickSetupInstaller";
        private const string SdkSettingsSeedDir = "Assets/_MolcaSDK/Settings";
        private const string ClaudeMdPath = "CLAUDE.md";

        private CancellationTokenSource _doctorCts;
        private string _doctorSummary = string.Empty;
        private bool _doctorRunning;

        public OnboardingWizardView()
        {
            AddToClassList("molca-onboarding");
            style.flexGrow = 1;
            MolcaEditorUi.Apply(this);

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            Add(scroll);

            var intro = new Label(
                "One-time setup steps for a freshly installed project. Each step is optional and safe to " +
                "re-run — nothing here is required for the project to compile.");
            intro.style.whiteSpace = WhiteSpace.Normal;
            intro.style.marginBottom = 8;
            scroll.Add(intro);

            scroll.Add(BuildProjectSettingsCard());
            BuildSdkQuickSetupCardIfPresent(scroll);
            scroll.Add(BuildAgentInstructionsCard());
            scroll.Add(BuildMcpProxyCard());
            scroll.Add(BuildToolingChecksCard());

            RegisterCallback<DetachFromPanelEvent>(_ => _doctorCts?.Cancel());
        }

        // -------------------------------------------------------------------
        // Project settings
        // -------------------------------------------------------------------

        private VisualElement BuildProjectSettingsCard()
        {
            var card = new MolcaSectionCard("Project Settings");

            var body = new Label(
                "Clones the read-only Core defaults into Assets/_Molca/Settings/MolcaProjectSettings.asset " +
                "so this project has an editable copy. Safe to run even if it already exists.");
            body.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(body);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-onboarding__status");
            card.Body.Add(statusLabel);

            var open = MolcaButtons.Primary("Open Project Settings", () =>
            {
                var instance = global::Molca.MolcaProjectSettings.Instance;
                if (instance != null)
                {
                    Selection.activeObject = instance;
                    EditorGUIUtility.PingObject(instance);
                }
                Refresh();
            });
            card.Body.Add(open);

            void Refresh()
            {
                bool exists = global::Molca.MolcaProjectSettings.LiveAssetExists;
                statusLabel.text = exists
                    ? "Assets/_Molca/Settings/MolcaProjectSettings.asset exists."
                    : "Not created yet.";
            }
            Refresh();

            return card;
        }

        // -------------------------------------------------------------------
        // SDK Quick Setup (reflection-only — Core must not reference the SDK assembly)
        // -------------------------------------------------------------------

        private static void BuildSdkQuickSetupCardIfPresent(VisualElement parent)
        {
            var sdkType = FindSdkQuickSetupType();
            if (sdkType == null)
                return;

            var card = new MolcaSectionCard("SDK Quick Setup");

            var body = new Label(
                "Copies com.molca.sdk's starter settings (GlobalSettings, input actions, lighting) into " +
                $"{SdkSettingsSeedDir}/ so the SDK layer has a working default config. Existing files are " +
                "kept unless you choose Repair.");
            body.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(body);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-onboarding__status");
            card.Body.Add(statusLabel);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            card.Body.Add(row);

            void Refresh() =>
                statusLabel.text = Directory.Exists(SdkSettingsSeedDir)
                    ? $"{SdkSettingsSeedDir}/ exists."
                    : "Not seeded yet.";

            var install = MolcaButtons.Primary("Install Starter Settings", () =>
            {
                InvokeStatic(sdkType, "InstallKeepingExisting");
                Refresh();
            });
            row.Add(install);

            var repair = MolcaButtons.Mini("Repair (Overwrite)", () =>
            {
                InvokeStatic(sdkType, "InstallOverwriting");
                Refresh();
            });
            row.Add(repair);

            Refresh();
            parent.Add(card);
        }

        private static Type FindSdkQuickSetupType() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(SdkQuickSetupTypeName, throwOnError: false))
                .FirstOrDefault(t => t != null);

        private static void InvokeStatic(Type type, string methodName) =>
            type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

        // -------------------------------------------------------------------
        // Coding-agent instructions
        // -------------------------------------------------------------------

        private VisualElement BuildAgentInstructionsCard()
        {
            var card = new MolcaSectionCard("Coding-Agent Instructions");

            var body = new Label(
                "Writes a project-root CLAUDE.md pointing at the installed packages' " +
                "Documentation~/reference/ docs, and states that Core/SDK are read-only. Only writes when " +
                "CLAUDE.md is absent — never overwrites existing content.");
            body.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(body);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-onboarding__status");
            card.Body.Add(statusLabel);

            Button generate = null;
            generate = MolcaButtons.Primary("Generate CLAUDE.md", () =>
            {
                GenerateClaudeMdStub();
                Refresh();
            });
            card.Body.Add(generate);

            void Refresh()
            {
                bool exists = File.Exists(ProjectRootPath(ClaudeMdPath));
                statusLabel.text = exists ? "CLAUDE.md already exists — left untouched." : "Not created yet.";
                generate.SetEnabled(!exists);
            }
            Refresh();

            return card;
        }

        private static void GenerateClaudeMdStub()
        {
            string path = ProjectRootPath(ClaudeMdPath);
            if (File.Exists(path))
                return;

            bool sdkInstalled = FindSdkQuickSetupType() != null;
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("# Molca Framework Project");
            lines.AppendLine();
            lines.AppendLine("This project uses the Molca Unity framework, installed as read-only UPM package(s):");
            lines.AppendLine();
            lines.AppendLine("- `Packages/com.molca.core` — never modify; subclass or extend from `Assets/`.");
            if (sdkInstalled)
                lines.AppendLine("- `Packages/com.molca.sdk` — never modify; subclass or extend from `Assets/`.");
            lines.AppendLine();
            lines.AppendLine("Reference docs (read these before assuming an API's shape):");
            lines.AppendLine();
            lines.AppendLine("- `Packages/com.molca.core/Documentation~/reference/` — Core conventions, subsystem " +
                              "lifecycle, DI, events, settings.");
            if (sdkInstalled)
                lines.AppendLine("- `Packages/com.molca.sdk/Documentation~/` — SDK-layer conventions, if present.");
            lines.AppendLine();
            lines.AppendLine("All project-specific code belongs under `Assets/` (e.g. `Assets/YourProject/Scripts/`).");

            File.WriteAllText(path, lines.ToString());
            Debug.Log($"[Onboarding] Wrote {path}.");
        }

        private static string ProjectRootPath(string relative)
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return Path.Combine(root, relative);
        }

        // -------------------------------------------------------------------
        // MCP proxy (reuses the existing builder — mirrors the Hub's MCP section)
        // -------------------------------------------------------------------

        private VisualElement BuildMcpProxyCard()
        {
            var card = new MolcaSectionCard("MCP Proxy");

            var body = new Label(
                "Builds the TypeScript MCP proxy (npm install + build) from the installed package's " +
                "Tools~/molca-mcp source into a writable <project>/molca-mcp/ folder.");
            body.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(body);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-onboarding__status");
            card.Body.Add(statusLabel);

            var build = MolcaButtons.Primary("Build MCP Proxy", () => McpProxyBuilder.Build());
            card.Body.Add(build);

            void Refresh()
            {
                bool built = McpProxyBuilder.IsBuilt;
                bool building = McpProxyBuilder.IsBuilding;
                build.SetEnabled(!building);
                build.text = built ? "Rebuild MCP Proxy" : "Build MCP Proxy";
                statusLabel.text = building ? McpProxyBuilder.Status : built ? "dist/index.js present." : "Not built yet.";
            }
            Refresh();
            card.schedule.Execute(Refresh).Every(500);

            return card;
        }

        // -------------------------------------------------------------------
        // Optional tooling checks
        // -------------------------------------------------------------------

        private VisualElement BuildToolingChecksCard()
        {
            var card = new MolcaSectionCard("Optional Tooling Checks");

            var doctorRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            card.Body.Add(doctorRow);

            var doctorButton = MolcaButtons.Primary("Run Doctor Smoke Test", () => _ = RunDoctorAsync());
            doctorRow.Add(doctorButton);

            var doctorStatus = new Label();
            doctorStatus.AddToClassList("molca-onboarding__status");
            card.Body.Add(doctorStatus);

            var graphRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            card.Body.Add(graphRow);

            var graphButton = MolcaButtons.Primary("Build Knowledge Graph", () => GraphifyBuild.Run(full: false));
            graphRow.Add(graphButton);

            var graphStatus = new Label();
            graphStatus.AddToClassList("molca-onboarding__status");
            card.Body.Add(graphStatus);

            void Refresh()
            {
                doctorButton.SetEnabled(!_doctorRunning);
                doctorButton.text = _doctorRunning ? "Running…" : "Run Doctor Smoke Test";
                doctorStatus.text = _doctorSummary;

                bool graphBuilding = GraphifyBuild.IsBuilding;
                graphButton.SetEnabled(!graphBuilding);
                graphButton.text = GraphifyCli.GraphExists ? "Update Knowledge Graph" : "Build Knowledge Graph";
                graphStatus.text = graphBuilding
                    ? GraphifyBuild.Status
                    : GraphifyCli.GraphExists ? "Graph present." : "Not built yet.";
            }
            Refresh();
            card.schedule.Execute(Refresh).Every(500);

            return card;
        }

        private async Awaitable RunDoctorAsync()
        {
            if (_doctorRunning) return;
            _doctorRunning = true;
            _doctorSummary = "Running checks…";
            _doctorCts = new CancellationTokenSource();

            try
            {
                var issues = await MolcaDoctor.RunAllAsync(cancellationToken: _doctorCts.Token);
                int errors = issues.Count(i => i.Severity == DoctorSeverity.Error);
                int warnings = issues.Count(i => i.Severity == DoctorSeverity.Warning);
                _doctorSummary = errors == 0 && warnings == 0
                    ? "No issues found."
                    : $"{errors} error(s), {warnings} warning(s). See Molca > Doctor for details.";
            }
            catch (OperationCanceledException)
            {
                _doctorSummary = "Cancelled.";
            }
            finally
            {
                _doctorRunning = false;
            }
        }
    }
}
