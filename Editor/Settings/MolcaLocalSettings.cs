using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditorInternal;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Per-machine overrides for the editor-only settings assets (MCP, Assistant, Automation): a sparse
    /// key → value overlay persisted to <c>UserSettings/MolcaLocalSettings.asset</c>, which Unity's standard
    /// <c>.gitignore</c> already excludes from version control.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/</c>. Registration: static singleton; not an asset.
    /// <para>
    /// This is the editor-side counterpart of the runtime <c>SettingModule</c> / <c>SettingState</c> split
    /// (<c>.claude/settings-system.md</c>): the settings asset holds the project's <b>authored defaults and
    /// policy</b> and is committed; a developer's own preferences — which model to talk to, which port to
    /// listen on, which automation profile to run under — live here and are never committed. Before this
    /// existed, changing a model or a port dirtied a tracked asset, so every developer's personal taste
    /// arrived as a repository diff and the effective safety boundary was whatever the last commit happened
    /// to carry.
    /// </para>
    /// <para>
    /// The overlay is deliberately <b>sparse</b>: a key is present only once the developer sets it, so an
    /// untouched field keeps tracking the project default as that default evolves. Removing an entry
    /// (<see cref="Clear"/>) is therefore a real operation and not the same as writing the default value.
    /// </para>
    /// <para>
    /// Only the fields listed in <see cref="Keys"/> are overridable. Project <i>policy</i> and <i>composition</i>
    /// — action allowlists, web-host allowlists, tool-provider lists — are intentionally absent: they belong in
    /// the committed asset where a change is reviewable in a diff. Secrets are absent for a different reason —
    /// they live in <c>McpAuth</c> / <c>AssistantApiAuth</c> / <c>IntegrationCredentialStore</c> and never on
    /// disk in plain form.
    /// </para>
    /// </remarks>
    public class MolcaLocalSettings : ScriptableObject
    {
        /// <summary>
        /// Overlay keys, one per overridable field. This list <b>is</b> the machine/project boundary: a field
        /// with a key here is a developer preference, and a field without one is project truth.
        /// </summary>
        public static class Keys
        {
            // --- MCP bridge: whether this machine runs a listener, and on which port. ---

            /// <summary><see cref="Mcp.McpSettings.Enabled"/> — per machine: not every clone runs the bridge.</summary>
            public const string McpEnabled = "mcp.enabled";

            /// <summary><see cref="Mcp.McpSettings.Port"/> — per machine: two projects on one box collide.</summary>
            public const string McpPort = "mcp.port";

            // --- Assistant: which backend this developer talks to, and how hard it works. ---

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.Enabled"/>.</summary>
            public const string AssistantEnabled = "assistant.enabled";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.Provider"/> — depends on which key the developer holds.</summary>
            public const string AssistantProvider = "assistant.provider";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.Model"/> (raw value; blank still means "provider default").</summary>
            public const string AssistantModel = "assistant.model";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.BaseUrl"/> (raw value; blank still means "provider default").</summary>
            public const string AssistantBaseUrl = "assistant.baseUrl";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.MaxTokens"/>.</summary>
            public const string AssistantMaxTokens = "assistant.maxTokens";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.StreamResponses"/>.</summary>
            public const string AssistantStreamResponses = "assistant.streamResponses";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.ReasoningEffort"/> — a cost/latency preference.</summary>
            public const string AssistantReasoningEffort = "assistant.reasoningEffort";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.AutoCompact"/>.</summary>
            public const string AssistantAutoCompact = "assistant.autoCompact";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.AutoCompactThreshold"/>.</summary>
            public const string AssistantAutoCompactThreshold = "assistant.autoCompactThreshold";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.LocalContextWindow"/> — matches the local runtime's configuration.</summary>
            public const string AssistantLocalContextWindow = "assistant.localContextWindow";

            /// <summary><see cref="Mcp.Assistant.AssistantSettings.SubAgentConcurrency"/> — scales with this machine's cores.</summary>
            public const string AssistantSubAgentConcurrency = "assistant.subAgentConcurrency";

            // --- Automation: which profile this machine runs under. ---

            /// <summary>
            /// <see cref="Automation.MolcaAutomationPolicySettings.ActiveProfile"/> — per machine, because
            /// "this box is a CI runner" is a property of the box, not of the project. The <i>allowlist</i>
            /// stays committed: which commands may ever run is a project decision.
            /// </summary>
            public const string AutomationActiveProfile = "automation.activeProfile";
        }

        // UserSettings/ is Unity's per-developer counterpart to ProjectSettings/ and is already excluded by
        // the standard Unity .gitignore, so an override here can never arrive as a repository diff.
        private const string SETTINGS_PATH = "UserSettings/MolcaLocalSettings.asset";

        /// <summary>One overridden field. Values are stored as invariant strings so the file stays diffable.</summary>
        [Serializable]
        private struct Entry
        {
            public string key;
            public string value;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();

        private static MolcaLocalSettings _instance;
        private static MolcaLocalSettings _overrideForTests;

        /// <summary>The overlay for this machine, loaded on first use.</summary>
        public static MolcaLocalSettings Instance
        {
            get
            {
                if (_overrideForTests != null) return _overrideForTests;
                if (_instance == null) _instance = LoadOrCreate();
                return _instance;
            }
        }

        /// <summary>
        /// Substitutes an in-memory overlay for the developer's real one, so a test can exercise override
        /// behavior without touching <c>UserSettings/MolcaLocalSettings.asset</c>. Pass <c>null</c> to restore.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="Automation.MolcaAutomationPolicySettings.OverrideForTests"/> and for the same
        /// reason: this file holds the developer's own configuration, so a test writing to it would silently
        /// re-point their assistant or change the automation profile they run under. Installed for the whole
        /// suite by <c>MolcaStoreIsolation</c>.
        /// </remarks>
        /// <param name="instance">A <see cref="ScriptableObject.CreateInstance{T}"/>d overlay, or null.</param>
        public static void OverrideForTests(MolcaLocalSettings instance) => _overrideForTests = instance;

        private static MolcaLocalSettings LoadOrCreate()
        {
            if (File.Exists(SETTINGS_PATH))
            {
                var objects = InternalEditorUtility.LoadSerializedFileAndForget(SETTINGS_PATH);
                foreach (var obj in objects)
                {
                    if (obj is MolcaLocalSettings loaded)
                    {
                        // Same flags as MolcaEditorSettings: hidden and not Unity-managed (we persist through
                        // Save()), but never NotEditable, which would freeze SerializedObject-driven fields.
                        loaded.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                        return loaded;
                    }
                }
            }

            var settings = CreateInstance<MolcaLocalSettings>();
            settings.name = nameof(MolcaLocalSettings);
            settings.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            // Not saved here: an empty overlay is indistinguishable from no overlay, and writing the file on
            // mere inspection would create it in every clone that only ever reads project defaults.
            return settings;
        }

        /// <summary>
        /// Writes the overlay to <c>UserSettings/MolcaLocalSettings.asset</c>, or removes that file once the
        /// last override is gone.
        /// </summary>
        /// <remarks>
        /// An empty overlay is deliberately left as <b>no file at all</b>, matching <see cref="LoadOrCreate"/>:
        /// "no overrides" and "no overlay" must be the same state, or clearing the last override would leave a
        /// stub behind that reads as configuration. It also keeps a clone that only ever uses project defaults
        /// from growing a file it never asked for.
        /// </remarks>
        public void Save()
        {
            // A test-injected overlay is not the real file; persisting it would defeat the isolation.
            if (_overrideForTests == this) return;

            if (entries == null || entries.Count == 0)
            {
                if (File.Exists(SETTINGS_PATH)) File.Delete(SETTINGS_PATH);
                return;
            }

            Directory.CreateDirectory("UserSettings");
            InternalEditorUtility.SaveToSerializedFileAndForget(
                new UnityEngine.Object[] { this }, SETTINGS_PATH, allowTextSerialization: true);
        }

        // -------------------------------------------------------------------
        // Override presence
        // -------------------------------------------------------------------

        /// <summary>True if <paramref name="key"/> is overridden on this machine.</summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        public bool Has(string key) => IndexOf(key) >= 0;

        /// <summary>
        /// Removes the override for <paramref name="key"/>, so the field tracks the project default again.
        /// </summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        public void Clear(string key)
        {
            var index = IndexOf(key);
            if (index < 0) return;
            entries.RemoveAt(index);
            Save();
        }

        /// <summary>Removes every override, restoring the project defaults for all fields.</summary>
        public void ClearAll()
        {
            if (entries == null || entries.Count == 0) return;
            entries.Clear();
            Save();
        }

        /// <summary>The keys currently overridden, for diagnostics and the settings UI. Never null.</summary>
        public IReadOnlyList<string> OverriddenKeys
        {
            get
            {
                var keys = new List<string>(entries?.Count ?? 0);
                if (entries != null)
                    foreach (var entry in entries)
                        keys.Add(entry.key);
                return keys;
            }
        }

        // -------------------------------------------------------------------
        // Typed accessors
        // -------------------------------------------------------------------

        /// <summary>The overridden bool, or <paramref name="projectDefault"/> when not overridden.</summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="projectDefault">The authored value from the committed settings asset.</param>
        public bool GetBool(string key, bool projectDefault)
        {
            var raw = Raw(key);
            if (raw == null) return projectDefault;
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Overrides <paramref name="key"/> with a bool.</summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="value">The machine-local value.</param>
        public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

        /// <summary>The overridden int, or <paramref name="projectDefault"/> when not overridden or unparseable.</summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="projectDefault">The authored value from the committed settings asset.</param>
        public int GetInt(string key, int projectDefault)
        {
            var raw = Raw(key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : projectDefault;
        }

        /// <summary>Overrides <paramref name="key"/> with an int.</summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="value">The machine-local value.</param>
        public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// The overridden string, or <paramref name="projectDefault"/> when not overridden. An override may
        /// legitimately be empty (e.g. "use the provider's default model"), which is distinct from absent.
        /// </summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="projectDefault">The authored value from the committed settings asset.</param>
        public string GetString(string key, string projectDefault) => Raw(key) ?? projectDefault;

        /// <summary>Overrides <paramref name="key"/> with a string.</summary>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="value">The machine-local value; may be empty.</param>
        public void SetString(string key, string value) => Set(key, value ?? string.Empty);

        /// <summary>
        /// The overridden enum member, or <paramref name="projectDefault"/> when not overridden or when the
        /// stored name no longer exists.
        /// </summary>
        /// <typeparam name="T">The enum type.</typeparam>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="projectDefault">The authored value from the committed settings asset.</param>
        /// <remarks>
        /// Stored by <b>name</b>, not by ordinal: an overlay outlives the enum declarations it references, and a
        /// member inserted mid-enum would silently re-point every stored ordinal after it. An unrecognized name
        /// falls back to the project default rather than throwing.
        /// </remarks>
        public T GetEnum<T>(string key, T projectDefault) where T : struct, Enum
        {
            var raw = Raw(key);
            if (string.IsNullOrEmpty(raw)) return projectDefault;
            return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(T), parsed)
                ? parsed
                : projectDefault;
        }

        /// <summary>Overrides <paramref name="key"/> with an enum member, stored by name.</summary>
        /// <typeparam name="T">The enum type.</typeparam>
        /// <param name="key">An overlay key from <see cref="Keys"/>.</param>
        /// <param name="value">The machine-local value.</param>
        public void SetEnum<T>(string key, T value) where T : struct, Enum => Set(key, value.ToString());

        // -------------------------------------------------------------------
        // Storage
        // -------------------------------------------------------------------

        private string Raw(string key)
        {
            var index = IndexOf(key);
            return index < 0 ? null : entries[index].value;
        }

        private void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            entries ??= new List<Entry>();

            var index = IndexOf(key);
            if (index >= 0)
            {
                if (entries[index].value == value) return;   // No write, no file churn.
                entries[index] = new Entry { key = key, value = value };
            }
            else
            {
                entries.Add(new Entry { key = key, value = value });
            }
            Save();
        }

        private int IndexOf(string key)
        {
            if (entries == null || string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].key == key)
                    return i;
            return -1;
        }
    }

    /// <summary>
    /// Applies <see cref="MolcaLocalSettings"/> to an editor settings asset's overridable fields: reads
    /// resolve override-then-authored, and writes go to the overlay instead of the asset.
    /// </summary>
    /// <remarks>
    /// Placement: alongside <see cref="MolcaLocalSettings"/>. Registration: static utility.
    /// <para>
    /// The overlay only shadows a <b>persistent</b> asset — the project's committed settings file. A
    /// <see cref="ScriptableObject.CreateInstance{T}"/>d settings object (tests, previews, the eval runner) is
    /// throwaway configuration with no project default to shadow, so it reads and writes its own fields as
    /// before. Without that rule a test setting <c>Provider</c> on its own instance would write the developer's
    /// overlay and leak into every later test that expected the authored default. This mirrors the existing
    /// <c>IsPersistent</c> guard in <see cref="Automation.MolcaAutomationPolicySettings"/>'s persist path.
    /// </para>
    /// </remarks>
    internal static class MolcaLocalOverlay
    {
        /// <summary>True when <paramref name="asset"/> is a committed asset the overlay should shadow.</summary>
        /// <param name="asset">The settings asset.</param>
        internal static bool Shadows(UnityEngine.Object asset) => UnityEditor.EditorUtility.IsPersistent(asset);

        /// <summary>Resolves a bool: the local override if this is a committed asset, else the authored value.</summary>
        internal static bool GetBool(UnityEngine.Object asset, string key, bool authored)
            => Shadows(asset) ? MolcaLocalSettings.Instance.GetBool(key, authored) : authored;

        /// <summary>Writes a bool to the overlay, or to the authored field for a throwaway instance.</summary>
        internal static void SetBool(UnityEngine.Object asset, string key, ref bool authored, bool value)
        {
            if (Shadows(asset)) MolcaLocalSettings.Instance.SetBool(key, value);
            else authored = value;
        }

        /// <summary>Resolves an int: the local override if this is a committed asset, else the authored value.</summary>
        internal static int GetInt(UnityEngine.Object asset, string key, int authored)
            => Shadows(asset) ? MolcaLocalSettings.Instance.GetInt(key, authored) : authored;

        /// <summary>Writes an int to the overlay, or to the authored field for a throwaway instance.</summary>
        internal static void SetInt(UnityEngine.Object asset, string key, ref int authored, int value)
        {
            if (Shadows(asset)) MolcaLocalSettings.Instance.SetInt(key, value);
            else authored = value;
        }

        /// <summary>Resolves a string: the local override if this is a committed asset, else the authored value.</summary>
        internal static string GetString(UnityEngine.Object asset, string key, string authored)
            => Shadows(asset) ? MolcaLocalSettings.Instance.GetString(key, authored) : authored;

        /// <summary>Writes a string to the overlay, or to the authored field for a throwaway instance.</summary>
        internal static void SetString(UnityEngine.Object asset, string key, ref string authored, string value)
        {
            if (Shadows(asset)) MolcaLocalSettings.Instance.SetString(key, value);
            else authored = value ?? string.Empty;
        }

        /// <summary>Resolves an enum: the local override if this is a committed asset, else the authored value.</summary>
        internal static T GetEnum<T>(UnityEngine.Object asset, string key, T authored) where T : struct, Enum
            => Shadows(asset) ? MolcaLocalSettings.Instance.GetEnum(key, authored) : authored;

        /// <summary>Writes an enum to the overlay, or to the authored field for a throwaway instance.</summary>
        internal static void SetEnum<T>(UnityEngine.Object asset, string key, ref T authored, T value)
            where T : struct, Enum
        {
            if (Shadows(asset)) MolcaLocalSettings.Instance.SetEnum(key, value);
            else authored = value;
        }

        /// <summary>True when <paramref name="asset"/> is committed and <paramref name="key"/> is overridden here.</summary>
        /// <param name="asset">The settings asset.</param>
        /// <param name="key">An overlay key from <see cref="MolcaLocalSettings.Keys"/>.</param>
        internal static bool IsOverridden(UnityEngine.Object asset, string key)
            => Shadows(asset) && MolcaLocalSettings.Instance.Has(key);
    }
}
