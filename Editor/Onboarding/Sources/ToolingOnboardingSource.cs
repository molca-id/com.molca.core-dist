using System.Collections.Generic;
using System.IO;
using Molca.Editor.KnowledgeGraph;
using Molca.Editor.Mcp;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Onboarding.Sources
{
    /// <summary>
    /// The optional developer tooling a Molca project can set up: the coding-agent instructions, the MCP
    /// proxy, and the knowledge graph.
    /// </summary>
    /// <remarks>
    /// <para>These are the wizard cards that survived the move to a checklist, and they survived on one
    /// test: each has a state that can be observed cheaply and truthfully — a file exists, a build output
    /// exists, a graph exists. The Doctor card did not, and was dropped: a smoke run is neither cheap nor
    /// something that can be "done", so a row for it could only ever have shown a button next to a status it
    /// had to invent. Doctor is a workspace in the Hub's Quality group, which is where it belongs.</para>
    /// <para>Everything here writes into consumer space only, per
    /// <c>Documentation~/internal/ONBOARDING_CHECKLIST.md</c>, and nothing here is required to compile or
    /// boot.</para>
    /// </remarks>
    internal sealed class ToolingOnboardingSource : IMolcaOnboardingItemProvider
    {
        private const string ClaudeMdFileName = "CLAUDE.md";

        /// <inheritdoc/>
        public IEnumerable<MolcaOnboardingItem> GetItems() => new[]
        {
            ProjectSettingsItem(),
            AgentInstructionsItem(),
            McpProxyItem(),
            KnowledgeGraphItem(),
        };

        // -------------------------------------------------------------------
        // Project settings
        // -------------------------------------------------------------------

        /// <summary>
        /// The consumer-space settings asset. Informational: Core seeds it lazily on first access, so this
        /// row exists to say where it is, not to create it.
        /// </summary>
        private static MolcaOnboardingItem ProjectSettingsItem() => new MolcaOnboardingItem(
            id: "onboarding.project-settings",
            title: "Project Settings",
            summary: "The editable MolcaProjectSettings asset in Assets/_Molca/Settings/ that this project "
                     + "configures Core through.",
            check: () => global::Molca.MolcaProjectSettings.LiveAssetExists
                ? MolcaOnboardingCheck.Done("Assets/_Molca/Settings/MolcaProjectSettings.asset exists.")
                : MolcaOnboardingCheck.Todo(
                    "Not created yet — it is generated on first access, or open it now."),
            severity: MolcaOnboardingSeverity.Recommended,
            order: 2000,
            actionLabel: "Open",
            act: OpenProjectSettings,
            why: "Core reads its defaults from a read-only copy inside the package; this is the copy the "
                 + "project owns and an upgrade will not replace.",
            docId: "GETTING_STARTED");

        private static void OpenProjectSettings()
        {
            var instance = global::Molca.MolcaProjectSettings.Instance;
            if (instance == null)
            {
                Debug.LogWarning("[Molca Onboarding] No MolcaProjectSettings asset could be created.");
                return;
            }

            Selection.activeObject = instance;
            EditorGUIUtility.PingObject(instance);
        }

        // -------------------------------------------------------------------
        // Coding-agent instructions
        // -------------------------------------------------------------------

        private static MolcaOnboardingItem AgentInstructionsItem() => new MolcaOnboardingItem(
            id: "onboarding.agent-instructions",
            title: "Coding-Agent Instructions",
            summary: "A project-root CLAUDE.md pointing at the installed packages' reference docs and "
                     + "stating that they are read-only.",
            check: () => File.Exists(ProjectRootPath(ClaudeMdFileName))
                ? MolcaOnboardingCheck.Done($"{ClaudeMdFileName} exists — left untouched.")
                : MolcaOnboardingCheck.Todo("Not created yet."),
            severity: MolcaOnboardingSeverity.Recommended,
            order: 2010,
            actionLabel: "Generate",
            act: GenerateAgentInstructions,
            why: "Without it an agent working in this project will read the package as editable source and "
                 + "propose changes inside Packages/, which an upgrade discards.");

        /// <summary>
        /// Writes the stub, and only when nothing is there. Never merges, never overwrites: an existing
        /// CLAUDE.md is the author's, and the row reports Done precisely so this is never reached.
        /// </summary>
        private static void GenerateAgentInstructions()
        {
            var path = ProjectRootPath(ClaudeMdFileName);
            if (File.Exists(path))
            {
                Debug.Log($"[Molca Onboarding] {ClaudeMdFileName} already exists; left untouched.");
                return;
            }

            var text = new System.Text.StringBuilder();
            text.AppendLine("# Molca Framework Project");
            text.AppendLine();
            text.AppendLine("This project uses the Molca Unity framework, installed as a read-only UPM package:");
            text.AppendLine();
            text.AppendLine("- `Packages/com.molca.core` — never modify; subclass or extend from `Assets/`.");
            text.AppendLine();
            text.AppendLine("Reference docs (read these before assuming an API's shape):");
            text.AppendLine();
            text.AppendLine("- `Packages/com.molca.core/Documentation~/reference/` — Core conventions, subsystem "
                            + "lifecycle, DI, events, settings.");
            text.AppendLine();
            text.AppendLine("All project-specific code belongs under `Assets/` (e.g. `Assets/YourProject/Scripts/`).");

            File.WriteAllText(path, text.ToString());
            Debug.Log($"[Molca Onboarding] Wrote {path}.");
        }

        private static string ProjectRootPath(string relative)
        {
            var root = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return Path.Combine(root, relative);
        }

        // -------------------------------------------------------------------
        // MCP proxy
        // -------------------------------------------------------------------

        private static MolcaOnboardingItem McpProxyItem() => new MolcaOnboardingItem(
            id: "onboarding.mcp-proxy",
            title: "MCP Proxy",
            summary: "Builds the TypeScript MCP proxy from the package's Tools~/molca-mcp source into a "
                     + "writable <project>/molca-mcp/ folder.",
            check: CheckMcpProxy,
            severity: MolcaOnboardingSeverity.Recommended,
            order: 2020,
            actionLabel: "Build",
            act: () => McpProxyBuilder.Build(),
            why: "It is what lets an external agent drive this editor; the source is copied out first because "
                 + "the package itself is immutable.");

        private static MolcaOnboardingCheck CheckMcpProxy()
        {
            if (McpProxyBuilder.IsBuilding)
                return MolcaOnboardingCheck.Blocked(McpProxyBuilder.Status);

            return McpProxyBuilder.IsBuilt
                ? MolcaOnboardingCheck.Done("dist/index.js present.")
                : MolcaOnboardingCheck.Todo("Not built yet.");
        }

        // -------------------------------------------------------------------
        // Knowledge graph
        // -------------------------------------------------------------------

        private static MolcaOnboardingItem KnowledgeGraphItem() => new MolcaOnboardingItem(
            id: "onboarding.knowledge-graph",
            title: "Knowledge Graph",
            summary: "Builds the Graphify knowledge graph over this project's source and docs.",
            check: CheckKnowledgeGraph,
            severity: MolcaOnboardingSeverity.Recommended,
            order: 2030,
            actionLabel: "Build",
            act: () => GraphifyBuild.Run(full: false),
            why: "It is the source of truth for what the API currently is, as opposed to what the docs "
                 + "described when they were written.");

        private static MolcaOnboardingCheck CheckKnowledgeGraph()
        {
            if (GraphifyBuild.IsBuilding)
                return MolcaOnboardingCheck.Blocked(GraphifyBuild.Status);

            return GraphifyCli.GraphExists
                ? MolcaOnboardingCheck.Done("Graph present.")
                : MolcaOnboardingCheck.Todo("Not built yet.");
        }
    }
}
