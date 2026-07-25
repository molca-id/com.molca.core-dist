using UnityEngine;

namespace Molca.Editor.Telemetry
{
    /// <summary>
    /// Project-wide policy for editor usage telemetry. Editor-only settings asset located by type via
    /// <see cref="MolcaEditorSettingsAsset.GetOrCreate{T}"/>; runtime code never reads it.
    /// </summary>
    /// <remarks>
    /// Telemetry is license-scoped, not user-scoped: reports carry the licensee, a one-way HMAC of the
    /// machine id computed <i>server-side</i>, and framework version facts. No email, no raw device id,
    /// no project or asset names. See <c>Documentation~/reference/TELEMETRY.md</c> for the event
    /// dictionary and exactly what each event carries.
    /// </remarks>
    internal sealed class MolcaEditorTelemetrySettings : ScriptableObject
    {
        [Tooltip("When false, no editor usage event is queued or sent. Add-on install reporting stops too.")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("Log every queued event to the console. Use when auditing what this project reports.")]
        [SerializeField] private bool _verbose = false;

        internal bool Enabled => _enabled;
        internal bool Verbose => _verbose;

        /// <summary>
        /// Reads the project policy without creating an asset. Absence means "not configured yet", which
        /// is the default-on state — creating a settings asset as a side effect of the first event would
        /// dirty the project on load.
        /// </summary>
        internal static MolcaEditorTelemetrySettings FindExisting()
        {
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets($"t:{nameof(MolcaEditorTelemetrySettings)}"))
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<MolcaEditorTelemetrySettings>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null) return asset;
            }
            return null;
        }

        /// <summary>Creates the asset on demand so a project can opt out through the Inspector.</summary>
        internal static MolcaEditorTelemetrySettings GetOrCreate() =>
            MolcaEditorSettingsAsset.GetOrCreate<MolcaEditorTelemetrySettings>("Telemetry Settings.asset");
    }
}
