using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Authored configuration for the Molca MCP bridge: the enable flag, loopback port, and the list
    /// of <see cref="McpToolProvider"/> assets whose tools are exposed. Mirrors
    /// <c>NotificationSettings</c> — a single editor-only asset referenced from
    /// <see cref="MolcaEditorSettings"/>.
    /// </summary>
    /// <remarks>
    /// Config only — the auth token is a secret and lives in <see cref="McpAuth"/>
    /// (project-scoped EditorPrefs), never on this asset.
    /// <para>
    /// <b>Enable flag and port are per machine.</b> Both read through
    /// <see cref="MolcaLocalSettings"/> and their setters write only there, so the serialized fields below
    /// stay the project's authored default and switching a port never dirties a tracked asset. The provider
    /// list and the action allowlist are the opposite: project composition and project policy, committed so a
    /// change to what the bridge exposes or permits is visible in a diff.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-mcp.png")]
    [CreateAssetMenu(fileName = "MCP Settings", menuName = "Molca/Editor/MCP Settings", order = 110)]
    public class McpSettings : ScriptableObject
    {
        /// <summary>Default loopback port for the bridge listener.</summary>
        public const int DefaultPort = 7777;

        [Tooltip("Project default for starting the MCP bridge listener when the editor loads. Each developer "
               + "can override this locally without committing the change.")]
        [SerializeField] private bool enabled = false;

        [Tooltip("Project default loopback TCP port the bridge listens on (127.0.0.1 only). Each developer "
               + "can override this locally without committing the change.")]
        [SerializeField] private int port = DefaultPort;

        [Tooltip("Tool providers exposed through the bridge. SDK forks add their own here.")]
        [SerializeField] private List<McpToolProvider> providers = new List<McpToolProvider>();

        [Tooltip("Fully-qualified names of Action (mutating) tools permitted to run. Action tools NOT "
               + "in this list are always refused; those listed still require a confirmation step (Sprint 17).")]
        [SerializeField] private List<string> actionToolAllowlist = new List<string>();

        /// <summary>
        /// Whether the bridge listener should run on this machine — the local override if set, otherwise
        /// <see cref="ProjectDefaultEnabled"/>. The setter writes the machine-local overlay, never the asset.
        /// </summary>
        public bool Enabled
        {
            get => MolcaLocalOverlay.GetBool(this, MolcaLocalSettings.Keys.McpEnabled, enabled);
            set => MolcaLocalOverlay.SetBool(this, MolcaLocalSettings.Keys.McpEnabled, ref enabled, value);
        }

        /// <summary>
        /// Loopback port for the bridge listener on this machine — the local override if set, otherwise
        /// <see cref="ProjectDefaultPort"/>. The setter writes the machine-local overlay, never the asset.
        /// </summary>
        public int Port
        {
            get => MolcaLocalOverlay.GetInt(this, MolcaLocalSettings.Keys.McpPort, port);
            set => MolcaLocalOverlay.SetInt(this, MolcaLocalSettings.Keys.McpPort, ref port, value);
        }

        /// <summary>The committed project default for <see cref="Enabled"/>, ignoring any local override.</summary>
        public bool ProjectDefaultEnabled => enabled;

        /// <summary>The committed project default for <see cref="Port"/>, ignoring any local override.</summary>
        public int ProjectDefaultPort => port;

        /// <summary>True when this machine overrides the project default for <paramref name="key"/>.</summary>
        /// <param name="key">A key from <see cref="MolcaLocalSettings.Keys"/> belonging to this asset.</param>
        public bool HasLocalOverride(string key) => MolcaLocalOverlay.IsOverridden(this, key);

        /// <summary>Drops this machine's override for <paramref name="key"/>, restoring the project default.</summary>
        /// <param name="key">A key from <see cref="MolcaLocalSettings.Keys"/> belonging to this asset.</param>
        public void ClearLocalOverride(string key) => MolcaLocalSettings.Instance.Clear(key);

        /// <summary>The configured provider assets, in list order. Never null.</summary>
        public IReadOnlyList<McpToolProvider> Providers
            => providers ?? (IReadOnlyList<McpToolProvider>)System.Array.Empty<McpToolProvider>();

        /// <summary>Fully-qualified names of Action tools allowed to run (still confirmation-gated). Never null.</summary>
        public IReadOnlyList<string> ActionToolAllowlist
            => actionToolAllowlist ?? (IReadOnlyList<string>)System.Array.Empty<string>();

        /// <summary>True if <paramref name="toolName"/> is permitted by the action allowlist.</summary>
        public bool IsActionAllowed(string toolName)
            => !string.IsNullOrEmpty(toolName) && actionToolAllowlist != null && actionToolAllowlist.Contains(toolName);

        /// <summary>
        /// Builds a fresh <see cref="McpToolRegistry"/> from the configured providers.
        /// </summary>
        /// <returns>A registry flattening every configured provider's tools.</returns>
        public McpToolRegistry BuildRegistry() => McpToolRegistry.Build(Providers);

        /// <summary>
        /// Loads the existing MCP settings asset, creating one at the default path if none exists.
        /// </summary>
        /// <returns>The shared <see cref="McpSettings"/> asset.</returns>
        public static McpSettings GetOrCreateSettings()
            => MolcaEditorSettingsAsset.GetOrCreate<McpSettings>("MCP Settings.asset");
    }
}
