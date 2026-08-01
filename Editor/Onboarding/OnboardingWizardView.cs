using System;
using System.IO;
using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using Molca.Editor.Hub;
using Molca.Editor.KnowledgeGraph;
using Molca.Editor.Mcp;
using Molca.Editor.Remediation;
using Molca.Editor.Remediation.Hub;
using Molca.Editor.Remediation.Provisioning;
using Molca.Editor.Starter;
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
    /// Implements the contract in <c>Documentation~/internal/ONBOARDING_WIZARD.md</c>: every action here
    /// writes only into consumer space (<c>Assets/</c>), never into <c>Packages/</c>, and nothing here is
    /// required for the project to compile or boot — <see cref="Molca.MolcaProjectSettings"/> already
    /// auto-seeds lazily on first access. Core must never hard-reference the SDK assembly (layering rule
    /// in <c>architecture.md</c>), so the SDK Quick Setup step is invoked through reflection and only
    /// shown when that type is actually present.
    /// </remarks>
    public sealed class OnboardingWizardView : VisualElement
    {
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
            scroll.Add(BuildStarterCard());
            scroll.Add(BuildBootstrapCard());
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
        // Starter — the opinionated "set me up with everything" step
        // -------------------------------------------------------------------

        /// <summary>
        /// Installs the recommended, fully-featured configuration.
        /// </summary>
        /// <remarks>
        /// Distinct from the Bootstrap card below, which repairs what is broken. This one has an opinion:
        /// it creates one of every setting module the framework offers so all features are present. Every
        /// asset is generated from code, so the packages ship nothing editable for it to copy.
        /// </remarks>
        private static VisualElement BuildStarterCard()
        {
            var card = new MolcaSectionCard("Project Starter");

            var body = new Label(
                "Sets up a fully-featured project: a GlobalSettings asset, one of every setting module " +
                "with its own defaults, and the RuntimeManager reference. Everything is generated into " +
                "Assets/_Molca/Settings/ — no configuration is copied out of a package. Safe to re-run; " +
                "anything already set up is left alone.");
            body.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(body);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-onboarding__status");
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(statusLabel);

            var detail = new Label { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f } };
            card.Body.Add(detail);

            void Refresh(MolcaStarterReport report = null)
            {
                statusLabel.text = MolcaStarter.IsFullyConfigured()
                    ? "Everything is set up."
                    : "Some features are not set up yet.";

                detail.text = report == null
                    ? string.Join("\n", MolcaStarter.Steps.Select(
                        s => (s.IsSatisfied() ? "• [done] " : "• [todo] ") + s.Title + " — " + s.Description))
                    : string.Join("\n", report.Steps.Select(
                        s => $"• {s.Title}: {s.Outcome.Message}"));
            }

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            card.Body.Add(row);

            row.Add(MolcaButtons.Primary("Set Up Everything", () =>
            {
                var report = MolcaStarter.Install();
                Debug.Log($"[Onboarding] Project starter: {report.Summarize()}");
                Refresh(report);
            }));

            row.Add(MolcaButtons.Mini("Preview", () => Refresh(MolcaStarter.Preview())));
            row.Add(MolcaButtons.Mini("Re-check", () => Refresh()));

            Refresh();
            return card;
        }

        // -------------------------------------------------------------------
        // Bootstrap configuration
        // -------------------------------------------------------------------

        /// <summary>
        /// Surfaces the bootstrap configuration check and offers its safe repairs — the day-one
        /// "nothing is wired up yet" step.
        /// </summary>
        /// <remarks>
        /// Delegates to the same <see cref="Molca.Editor.Settings.BootstrapCheck"/> the console validator
        /// logs and the same remediation pass the Hub button runs, so the wizard cannot develop its own
        /// opinion about what "configured" means. It offers only the safe pass; the fixes that create assets
        /// live behind the Remediation workspace's review step, which is the right place for a decision the
        /// user should see previewed.
        /// </remarks>
        private static VisualElement BuildBootstrapCard()
        {
            var card = new MolcaSectionCard("Bootstrap Configuration");

            var body = new Label(
                "Checks the pieces the application needs in order to start: a RuntimeManager prefab, a " +
                "GlobalSettings asset, and the setting modules your subsystems declare. Repairs the ones " +
                "with a single correct answer and lists the rest.");
            body.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(body);

            var statusLabel = new Label();
            statusLabel.AddToClassList("molca-onboarding__status");
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(statusLabel);

            var findingsLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, opacity = 0.85f } };
            card.Body.Add(findingsLabel);

            void Refresh()
            {
                var findings = Molca.Editor.Settings.BootstrapCheck.Run();
                statusLabel.text = findings.Count == 0
                    ? "Bootstrap is correctly configured."
                    : $"{findings.Count} issue(s) found.";
                findingsLabel.text = findings.Count == 0
                    ? string.Empty
                    : string.Join("\n", findings.Select(f => $"• [{f.Code}] {f.Message}"));
            }

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            card.Body.Add(row);

            row.Add(MolcaButtons.Primary("Fix Safe Issues", () =>
            {
                var report = MolcaRemediationPass.Apply(
                    BootstrapRemediationBridge.Request(RemediationPolicy.SafeOnly));
                Debug.Log($"[Onboarding] Bootstrap remediation: {report.Summarize()}");
                Refresh();
            }));

            row.Add(MolcaButtons.Mini("Re-check", Refresh));

            row.Add(MolcaButtons.Mini("Open Remediation", () =>
                MolcaHubWindow.OpenWorkspace(RemediationWorkspaceProvider.WorkspaceId)));

            Refresh();
            return card;
        }

        // -------------------------------------------------------------------
        // (Removed) SDK Quick Setup
        // -------------------------------------------------------------------
        //
        // This wizard used to look up "MolcaSDK.Editor.Setup.QuickSetupInstaller" by name across the
        // loaded assemblies and invoke it, so that Core could offer the SDK's setup without referencing
        // the SDK assembly. Avoiding the assembly reference was the wrong goal: the dependency was real
        // either way, and expressing it reflectively only hid it from the compiler. The effect was that
        // com.molca.sdk could not be deleted without silently breaking a button in Core.
        //
        // Nothing replaces it here, because the replacement already exists. A layer that wants to
        // contribute setup implements IMolcaStarterStep, which MolcaStarter discovers via TypeCache and
        // the Project Starter card renders. Contribution flows upward through an interface Core owns,
        // instead of Core reaching down for a type name it had to spell correctly.
        //
        // The generated half of what QuickSetupInstaller copied — the GlobalSettings graph and its
        // setting modules — is what the starter already produces, from code, into project space. The
        // remainder (input actions, lighting settings) is content, and content arrives by importing a
        // sample, not by a bespoke copier.

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

            var lines = new System.Text.StringBuilder();
            lines.AppendLine("# Molca Framework Project");
            lines.AppendLine();
            lines.AppendLine("This project uses the Molca Unity framework, installed as a read-only UPM package:");
            lines.AppendLine();
            lines.AppendLine("- `Packages/com.molca.core` — never modify; subclass or extend from `Assets/`.");
            lines.AppendLine();
            lines.AppendLine("Reference docs (read these before assuming an API's shape):");
            lines.AppendLine();
            lines.AppendLine("- `Packages/com.molca.core/Documentation~/reference/` — Core conventions, subsystem " +
                              "lifecycle, DI, events, settings.");
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
